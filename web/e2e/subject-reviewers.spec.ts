import { expect, test, type Page } from '@playwright/test';
import { READER_STORAGE_STATE } from './global-setup';
import {
  decideRaw,
  ensureHeldVersion,
  OWNER,
  ownerCredential,
  putReviewersRaw,
  readAudit,
  readSubject,
} from './registry';

/**
 * Naming who may approve or reject a subject's held versions (ADR-043), on the subject's page and in
 * the approval queue, against the real registry.
 *
 * <b>The rule is the registry's, and the screens are what this file checks.</b> A subject that names
 * reviewers refuses a decision from anyone else, an administrator included (`403 not_a_reviewer`),
 * and the queue says so instead of offering a button that would be refused. Every sentence in the
 * confirmation is asserted against what the registry then does: one audit entry naming the account
 * and both sets, and nobody notified (the notification half is the .NET suite's, which can see the
 * outbox; a browser reading it races the pump).
 *
 * <b>A held version cannot be undone, so each describe makes a subject of its own</b>, named for the
 * run (`acme.e2e.reviewers.<stem>.<timestamp>`), with a version held at the gate. The run leaves
 * nothing waiting: `afterAll` clears each subject's reviewers and rejects what is still held, so the
 * shared queue (`approvals.spec.ts` reads it) is not left holding a proposal that no signed-in
 * account may decide. It cannot run if the process is killed, and then a held version is left behind
 * with its reviewers named; clear them through `PUT …/reviewers` with the owner credential.
 */
const RUN = Date.now();
const ENVIRONMENT = 'dev';

const named = (stem: string) => `acme.e2e.reviewers.${stem}.${RUN}`;

/** Somebody else, who is not the signed-in account and never will be. */
const SOMEONE_ELSE = 'someone-else@example.com';

const arranged: string[] = [];

async function arrange(name: string): Promise<void> {
  await ensureHeldVersion(await ownerCredential(), ENVIRONMENT, name);
  arranged.push(name);
}

async function open(page: Page, name: string): Promise<void> {
  await page.goto(`/subjects/${name}`);
  await expect(page.getByRole('heading', { name })).toBeVisible();
}

const changeReviewers = (page: Page) => page.getByRole('button', { name: 'Change reviewers' });

function card(page: Page, subject: string) {
  return page.getByRole('listitem').filter({ has: page.getByRole('link', { name: subject }) });
}

/** Pages in until the subject's row is on screen, as `approvals.spec.ts` does. */
async function reveal(page: Page, subject: string): Promise<void> {
  const more = page.getByRole('button', { name: 'Show more' });

  while ((await card(page, subject).count()) === 0 && (await more.count()) > 0) {
    await more.click();
    await expect(page.getByRole('button', { name: 'Loading…' })).toHaveCount(0);
  }
}

test.afterAll(async () => {
  const credential = await ownerCredential();

  for (const name of arranged) {
    await putReviewersRaw(credential, ENVIRONMENT, name, []);

    const subject = await readSubject(credential, ENVIRONMENT, name);

    for (const version of subject.versions) {
      if (version.status === 'AWAITING_APPROVAL') {
        await decideRaw(credential, ENVIRONMENT, name, version.ordinal, 'reject');
      }
    }
  }
});

test.describe('naming somebody else', () => {
  test.describe.configure({ mode: 'serial' });
  const name = named('else');

  test.beforeAll(async () => {
    await arrange(name);
  });

  test('says any administrator decides, until it is told otherwise', async ({ page }) => {
    await open(page, name);

    await expect(page.getByTestId('subject-reviewers')).toContainText(
      'Held versions are decided by any administrator.',
    );
  });

  test('takes two clicks, says what it does, and shows what the registry stored', async ({
    page,
  }) => {
    await open(page, name);
    await changeReviewers(page).click();

    // Opens empty, because nobody is named, with nothing to review yet.
    await expect(page.getByLabel('Reviewers, one per line')).toHaveValue('');
    await expect(page.getByRole('button', { name: 'Review change' })).toBeDisabled();

    // Padding, a blank line and a repeat in another case, because the registry trims, ignores
    // nothing it is given and keeps the first spelling: the page must go where it was told.
    await page
      .getByLabel('Reviewers, one per line')
      .fill(`  ${SOMEONE_ELSE}  \n\n${SOMEONE_ELSE.toUpperCase()}`);

    // The caller is not in the list: said, as advice, and the form still lets them ask.
    await expect(page.getByText(`You (${OWNER.email}) are not in this list`)).toBeVisible();
    await page.getByRole('button', { name: 'Review change' }).click();

    // The first click asks, and nothing has changed in the registry.
    await expect(page.getByText(`Let only ${SOMEONE_ELSE} decide ${name}?`)).toBeVisible();
    await expect(page.getByText('an administrator included, is refused')).toBeVisible();
    await expect(page.getByText('SUBJECT_REVIEWERS_CHANGED')).toBeVisible();
    await expect(page.getByText('Nobody is notified.')).toBeVisible();
    expect((await readSubject(await ownerCredential(), ENVIRONMENT, name)).reviewers).toEqual([]);

    await page.getByRole('button', { name: 'Yes, change the reviewers' }).click();

    // What the registry stored, in its own words and not the request's.
    await expect(page.getByRole('status')).toContainText(
      `Only ${SOMEONE_ELSE} may now decide the held versions of ${name}`,
    );
    await expect(page.getByTestId('subject-reviewers')).toContainText(`decided by ${SOMEONE_ELSE}`);
    await expect(page.getByLabel('Reviewers, one per line')).toHaveCount(0);
  });

  test('is audited once, as the signed-in account, reading the old set and the new', async () => {
    const entries = (await readAudit(await ownerCredential(), ENVIRONMENT, name)).filter(
      (e) => e.action === 'SUBJECT_REVIEWERS_CHANGED',
    );

    expect(entries).toHaveLength(1);
    expect(entries[0]?.actor).toBe(OWNER.email);
    expect(entries[0]?.detail).toBe(`reviewers anyone with the scope -> ${SOMEONE_ELSE}`);
  });

  test('the queue says who decides, and offers this administrator no decision', async ({
    page,
  }) => {
    await page.goto('/approvals');
    await reveal(page, name);

    const row = card(page, name);

    await expect(row.getByTestId('queue-reviewers')).toContainText(SOMEONE_ELSE);
    await expect(row.getByTestId('not-a-reviewer')).toContainText(
      `Only ${SOMEONE_ELSE} may decide this.`,
    );
    await expect(row.getByRole('button', { name: 'Approve' })).toHaveCount(0);
    await expect(row.getByRole('button', { name: 'Reject' })).toHaveCount(0);

    // Reading is not deciding: the review still opens.
    await expect(row.getByRole('button', { name: /review changes and impact/i })).toBeVisible();
  });

  test('the registry refuses the decision the page does not offer, for both verbs', async () => {
    const credential = await ownerCredential();

    expect(await decideRaw(credential, ENVIRONMENT, name, 2, 'approve')).toEqual({
      status: 403,
      code: 'not_a_reviewer',
    });
    expect(await decideRaw(credential, ENVIRONMENT, name, 2, 'reject')).toEqual({
      status: 403,
      code: 'not_a_reviewer',
    });

    const subject = await readSubject(credential, ENVIRONMENT, name);
    expect(subject.versions.find((v) => v.ordinal === 2)?.status).toBe('AWAITING_APPROVAL');
  });

  test('a repeat of the set writes nothing', async () => {
    const credential = await ownerCredential();

    expect((await putReviewersRaw(credential, ENVIRONMENT, name, [SOMEONE_ELSE])).status).toBe(200);

    const entries = (await readAudit(credential, ENVIRONMENT, name)).filter(
      (e) => e.action === 'SUBJECT_REVIEWERS_CHANGED',
    );
    expect(entries).toHaveLength(1);
  });

  test('clearing it opens the decision to this administrator again', async ({ page }) => {
    await open(page, name);
    await changeReviewers(page).click();
    await page.getByLabel('Reviewers, one per line').fill('');
    await page.getByRole('button', { name: 'Review change' }).click();

    await expect(page.getByText(`Clear the reviewers of ${name}?`)).toBeVisible();
    await expect(page.getByText('Any administrator may approve or reject')).toBeVisible();
    await page.getByRole('button', { name: 'Yes, clear the reviewers' }).click();

    await expect(page.getByRole('status')).toContainText(
      `The reviewers of ${name} are cleared. Any administrator may decide its held versions.`,
    );
    await expect(page.getByTestId('subject-reviewers')).toContainText('any administrator');

    await page.goto('/approvals');
    await reveal(page, name);

    const row = card(page, name);
    await expect(row.getByRole('button', { name: 'Approve' })).toBeVisible();
    await expect(row.getByTestId('not-a-reviewer')).toHaveCount(0);
    await expect(row.getByTestId('queue-reviewers')).toHaveCount(0);
  });
});

test.describe('naming the signed-in account', () => {
  const name = named('me');

  test.beforeAll(async () => {
    await arrange(name);
    await putReviewersRaw(await ownerCredential(), ENVIRONMENT, name, [
      SOMEONE_ELSE,
      OWNER.email.toUpperCase(),
    ]);
  });

  test('is offered the decision, in whatever case it was named, and can make it', async ({
    page,
  }) => {
    await page.goto('/approvals');
    await reveal(page, name);

    const row = card(page, name);

    await expect(row.getByTestId('queue-reviewers')).toContainText(SOMEONE_ELSE);
    await expect(row.getByTestId('not-a-reviewer')).toHaveCount(0);

    await row.getByRole('button', { name: 'Approve' }).click();
    await row.getByRole('button', { name: 'Confirm' }).click();

    await expect(page.getByRole('status')).toContainText(`Approved ${name} v2`);
    await expect(row).toHaveCount(0);

    // The registry agrees, which is the only thing that makes the announcement true.
    const subject = await readSubject(await ownerCredential(), ENVIRONMENT, name);
    expect(subject.latest).toBe(2);
  });
});

test.describe('as a reader', () => {
  test.use({ storageState: READER_STORAGE_STATE });
  const name = named('reader');

  test.beforeAll(async () => {
    await arrange(name);
    await putReviewersRaw(await ownerCredential(), ENVIRONMENT, name, [SOMEONE_ELSE]);
  });

  test('sees who decides, and is offered no control to change it', async ({ page }) => {
    await open(page, name);

    await expect(page.getByTestId('subject-reviewers')).toContainText(`decided by ${SOMEONE_ELSE}`);
    await expect(changeReviewers(page)).toHaveCount(0);
    await expect(page.getByText('Change reviewers')).toHaveCount(0);
  });

  test('sees who decides in the queue, and is not told they may not, having never been offered it', async ({
    page,
  }) => {
    await page.goto('/approvals');
    await reveal(page, name);

    const row = card(page, name);

    await expect(row.getByTestId('queue-reviewers')).toContainText(SOMEONE_ELSE);
    await expect(row.getByTestId('not-a-reviewer')).toHaveCount(0);
    await expect(row.getByRole('button', { name: 'Approve' })).toHaveCount(0);
  });
});

test.describe('refusals and the console', () => {
  const name = named('refused');

  test.beforeAll(async () => {
    await arrange(name);
  });

  test('refuses more than twenty on the page, before asking the registry', async ({ page }) => {
    await open(page, name);
    await changeReviewers(page).click();

    await page
      .getByLabel('Reviewers, one per line')
      .fill(Array.from({ length: 21 }, (_, i) => `r${i}@example.com`).join('\n'));

    await expect(page.getByText('at most 20 reviewers')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Review change' })).toBeDisabled();
  });

  test('reports no console errors through the whole journey', async ({ page }) => {
    const errors: string[] = [];
    page.on('console', (message) => {
      if (message.type() === 'error') {
        errors.push(message.text());
      }
    });
    page.on('pageerror', (error) => errors.push(error.message));

    await open(page, name);
    await changeReviewers(page).click();
    await page.getByLabel('Reviewers, one per line').fill(OWNER.email);
    await page.getByRole('button', { name: 'Review change' }).click();
    await page.getByRole('button', { name: 'Yes, change the reviewers' }).click();
    await expect(page.getByRole('status')).toContainText('may now decide');

    await page.goto('/approvals');
    await reveal(page, name);
    await expect(card(page, name).getByRole('button', { name: 'Approve' })).toBeVisible();

    expect(errors).toEqual([]);
  });
});
