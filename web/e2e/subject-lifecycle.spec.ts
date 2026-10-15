import { expect, test } from '@playwright/test';
import { READER_STORAGE_STATE } from './global-setup';
import {
  addConsumeBindingRaw,
  createContractRaw,
  ensureEmptySubject,
  ensureEnvironment,
  ensureSubject,
  ownerCredential,
  purgeSubjectRaw,
  readAudit,
  readContract,
  readSubject,
  retireSubject,
} from './registry';

/**
 * Retiring a subject and deleting it for good, driven through the subject's page (ADR-046).
 *
 * <b>It deletes what it makes.</b> Every subject is named for the run and ends deleted, so the
 * permanent-subject counts in `README.md` do not move; what a run leaves is its audit entries, and one
 * empty contract (nothing deletes a contract), named for the run.
 *
 * What the registry decides (the retired rule, what stops a deletion, the 404 a schema answers
 * afterwards) is proved over real HTTP in the .NET suite. This is what only a browser can show: that the
 * controls exist for the people who may use them and for nobody else, that each takes two steps, that
 * deleting wants the name, and that a refusal reaches the person in the registry's own words.
 */
const RUN = Date.now();
const ENVIRONMENT = 'dev';
const NAMESPACE = `acme.e2e.lifecycle.${RUN}`;

const RETIRED = `${NAMESPACE}.Retired`;
const DELETED = `${NAMESPACE}.Deleted`;
const BOUND = `${NAMESPACE}.Bound`;
const CONTRACT = `e2e-lifecycle-${RUN}`;

test.describe.configure({ mode: 'serial' });

test.describe('ending a subject from its page', () => {
  let credential = '';

  test.beforeAll(async () => {
    credential = await ownerCredential();

    await ensureEnvironment(credential, ENVIRONMENT);
    await ensureSubject(credential, ENVIRONMENT, RETIRED);
    await ensureSubject(credential, ENVIRONMENT, DELETED);
    await ensureEmptySubject(credential, ENVIRONMENT, BOUND, 'json');
  });

  test('retires a subject in two steps, and it stays, retired', async ({ page }) => {
    await page.goto(`/subjects/${RETIRED}`);
    await expect(page.getByRole('heading', { level: 1 })).toContainText(RETIRED);

    // Nothing is sent by the first click.
    await page.getByRole('button', { name: 'Retire subject…' }).click();
    await expect(page.getByText(`Retire ${RETIRED}?`)).toBeVisible();
    await expect(page.getByText('It cannot be undone.')).toBeVisible();
    expect((await readSubject(credential, ENVIRONMENT, RETIRED)).lifecycle).toBe('ACTIVE');

    await page.getByRole('button', { name: 'Yes, retire it' }).click();

    // What shows is the registry's account: the subject read again, and a sentence saying so.
    await expect(page.getByRole('status')).toContainText(`${RETIRED} is retired`);
    await expect(page.getByText('RETIRED', { exact: true }).first()).toBeVisible();
    expect((await readSubject(credential, ENVIRONMENT, RETIRED)).lifecycle).toBe('RETIRED');

    const entries = await readAudit(credential, ENVIRONMENT, RETIRED);
    expect(entries.filter((e) => e.action === 'SUBJECT_RETIRED')).toHaveLength(1);

    // The step it offers has changed, and there is no way to retire it twice.
    await expect(page.getByRole('button', { name: 'Retire subject…' })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Delete for good…' })).toBeVisible();
  });

  test('can keep a subject: going back sends nothing', async ({ page }) => {
    await page.goto(`/subjects/${DELETED}`);

    await page.getByRole('button', { name: 'Retire subject…' }).click();
    await page.getByRole('button', { name: 'Keep it active' }).click();

    await expect(page.getByRole('button', { name: 'Retire subject…' })).toBeVisible();
    expect((await readSubject(credential, ENVIRONMENT, DELETED)).lifecycle).toBe('ACTIVE');
  });

  test('offers no way to delete a subject that is not retired', async ({ page }) => {
    await page.goto(`/subjects/${DELETED}`);

    await expect(page.getByRole('button', { name: 'Retire subject…' })).toBeVisible();
    await expect(page.getByRole('button', { name: /Delete for good/ })).toHaveCount(0);
  });

  test('deleting wants the name typed out, and then removes the subject for good', async ({
    page,
  }) => {
    await retireSubject(credential, ENVIRONMENT, DELETED);

    await page.goto(`/subjects/${DELETED}`);
    await page.getByRole('button', { name: 'Delete for good…' }).click();

    await expect(page.getByText(`Delete ${DELETED} for good?`)).toBeVisible();
    const confirm = page.getByRole('button', { name: 'Yes, delete it for good' });
    await expect(confirm).toBeDisabled();

    const box = page.getByLabel(`Type ${DELETED} to confirm`);
    await box.fill(DELETED.toUpperCase());
    await expect(confirm).toBeDisabled();

    await box.fill(DELETED);
    await expect(confirm).toBeEnabled();
    await confirm.click();

    // The page is replaced by what the registry said it removed.
    await expect(page.getByRole('heading', { level: 1 })).toContainText(`${DELETED} is deleted`);
    await expect(page.getByText(/removed it with 1 version/)).toBeVisible();
    await expect(page.getByRole('link', { name: 'Back to all subjects' })).toBeVisible();

    // Gone from the registry, and the list the app reads.
    const gone = await fetch(
      `${process.env['CONCORDAT_REGISTRY'] ?? 'http://localhost:5062'}/v1/environments/${ENVIRONMENT}/subjects/${DELETED}`,
      { headers: { authorization: `Bearer ${credential}` } },
    );
    expect(gone.status).toBe(404);

    const entries = await readAudit(credential, ENVIRONMENT, DELETED);
    const purged = entries.filter((e) => e.action === 'SUBJECT_PURGED');
    expect(purged).toHaveLength(1);
    expect(purged[0]!.detail).toBe('1 version removed');
  });

  test('shows the registry’s refusal in its own words, and keeps the subject', async ({ page }) => {
    await createContractRaw(credential, ENVIRONMENT, CONTRACT);
    await addConsumeBindingRaw(credential, ENVIRONMENT, CONTRACT, `${CONTRACT}.queue`, [
      { subject: BOUND, selector: 'latest' },
    ]);
    await retireSubject(credential, ENVIRONMENT, BOUND);

    await page.goto(`/subjects/${BOUND}`);
    await page.getByRole('button', { name: 'Delete for good…' }).click();
    await page.getByLabel(`Type ${BOUND} to confirm`).fill(BOUND);
    await page.getByRole('button', { name: 'Yes, delete it for good' }).click();

    await expect(page.getByRole('alert')).toContainText('The registry did not delete it');
    await expect(page.getByRole('alert')).toContainText(`contract '${CONTRACT}'`);
    expect((await readSubject(credential, ENVIRONMENT, BOUND)).lifecycle).toBe('RETIRED');
  });

  test('works again once what held it is removed', async ({ page }) => {
    const contract = await readContract(credential, ENVIRONMENT, CONTRACT);
    const binding = contract.consumes[0]!;
    const removed = await fetch(
      `${process.env['CONCORDAT_REGISTRY'] ?? 'http://localhost:5062'}/v1/environments/${ENVIRONMENT}/contracts/${CONTRACT}/consumes/${binding.id}`,
      { method: 'DELETE', headers: { authorization: `Bearer ${credential}` } },
    );
    expect(removed.ok).toBe(true);

    await page.goto(`/subjects/${BOUND}`);
    await page.getByRole('button', { name: 'Delete for good…' }).click();
    await page.getByLabel(`Type ${BOUND} to confirm`).fill(BOUND);
    await page.getByRole('button', { name: 'Yes, delete it for good' }).click();

    await expect(page.getByRole('heading', { level: 1 })).toContainText(`${BOUND} is deleted`);
    await expect(page.getByText(/removed it with 0 versions/)).toBeVisible();
  });

  test.afterAll(async () => {
    // Whatever a failed test left, retired and deleted, so a run leaves no subject behind. The first
    // subject is deleted here and not in its own test because that test reads the audit entry it wrote.
    for (const name of [RETIRED, DELETED, BOUND]) {
      const view = await readSubject(credential, ENVIRONMENT, name).catch(() => null);

      if (view !== null) {
        if (view.lifecycle !== 'RETIRED') {
          await retireSubject(credential, ENVIRONMENT, name);
        }

        await purgeSubjectRaw(credential, ENVIRONMENT, name);
      }
    }
  });
});
test.describe('without subject:admin', () => {
  const WATCHED = `${NAMESPACE}.Watched`;

  test.beforeAll(async () => {
    await ensureSubject(await ownerCredential(), ENVIRONMENT, WATCHED);
  });

  test.afterAll(async () => {
    const owner = await ownerCredential();

    await retireSubject(owner, ENVIRONMENT, WATCHED);
    await purgeSubjectRaw(owner, ENVIRONMENT, WATCHED);
  });

  // The default context is the owner; a reader's sign-in is made once by global setup, because
  // sign-in is rate-limited.
  test.use({ storageState: READER_STORAGE_STATE });

  test('a reader sees the subject and is offered neither step', async ({ page }) => {
    await page.goto(`/subjects/${WATCHED}`);

    await expect(page.getByRole('heading', { level: 1 })).toContainText(WATCHED);
    await expect(page.getByRole('button', { name: /Retire subject|Delete for good/ })).toHaveCount(
      0,
    );
  });
});
