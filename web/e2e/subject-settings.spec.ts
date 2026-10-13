import { expect, test, type Page, type Request } from '@playwright/test';
import { READER_STORAGE_STATE } from './global-setup';
import {
  createSubjectRaw,
  ensureEnvironmentWithDefault,
  OWNER,
  ownerCredential,
  patchSubjectRaw,
  readAudit,
  readSubject,
  registerVersion,
  retireSubject,
} from './registry';

/**
 * Changing a subject's owner and compatibility policy, on its page, against the real registry.
 *
 * <b>Every sentence in a confirmation is asserted here against what the registry then does.</b>
 * Nothing about what these two changes affect is guessed: a change of owner leaves one audit
 * entry naming the account that made it and no more, a change of policy leaves none, the next
 * registration is the first to feel a policy and a held version stays held when it is relaxed, and
 * a subject with no policy of its own is judged by the registry's built-in default and not by the
 * environment's. The last three are things the registry *does*, not things anyone designed, and
 * this is the file that goes red when they change — at which point a sentence in
 * `subject-owner-editor.ts` or `subject-policy-editor.ts`, a row in STATUS.md and ADR-032 need to
 * change with them.
 *
 * <b>A change cannot be dry-run and a subject cannot be deleted.</b> Each describe makes subjects
 * of its own, named for the run (`acme.e2e.settings.<stem>.<timestamp>`), and a run leaves four
 * behind in `dev` and one in `e2e-inherit`. Never the shared fixtures: `acme.e2e.PaymentTaken`,
 * `acme.e2e.LedgerPosted`, `acme.e2e.OrderCreated` and the environment's own defaults belong to
 * other specs, and nothing here changes them. `README.md` says so.
 *
 * What the forms *accept* is `subject-settings-parity.spec.ts`; this file is about the screen —
 * what it asks, what it sends, what it shows, and that it shows nothing the registry has not said.
 */
const RUN = Date.now();
const ENVIRONMENT = 'dev';
const INHERIT_ENVIRONMENT = 'e2e-inherit';

const named = (stem: string) => `acme.e2e.settings.${stem}.${RUN}`;

const schema = (type: string) =>
  JSON.stringify({ type: 'object', properties: { id: { type } }, required: ['id'] });

async function arrange(
  name: string,
  extra: Record<string, unknown> = {},
  environment = ENVIRONMENT,
): Promise<void> {
  const verdict = await createSubjectRaw(await ownerCredential(), environment, {
    name,
    format: 'json',
    owner: 'arranged-by-e2e',
    ...extra,
  });

  expect(verdict.status, `arranging ${name}`).toBe(201);
}

async function open(page: Page, name: string): Promise<void> {
  await page.goto(`/subjects/${name}`);
  await expect(page.getByRole('heading', { name })).toBeVisible();
}

/** Watches the requests that change a subject. */
function writes(page: Page): { patches: Request[]; puts: Request[] } {
  const seen = { patches: [] as Request[], puts: [] as Request[] };

  page.on('request', (request) => {
    if (/\/v1\/environments\/[^/]+\/subjects\/[^/]+$/.test(request.url())) {
      if (request.method() === 'PATCH') {
        seen.patches.push(request);
      }
    }

    if (request.method() === 'PUT' && request.url().endsWith('/compatibility-policy')) {
      seen.puts.push(request);
    }
  });

  return seen;
}

const changeOwner = (page: Page) => page.getByRole('button', { name: 'Change owner' });
const changePolicy = (page: Page) => page.getByRole('button', { name: 'Change policy' });

test.describe('changing the owner', () => {
  test.describe.configure({ mode: 'serial' });
  const name = named('owner');

  test.beforeAll(async () => {
    await arrange(name);
  });

  test('takes two clicks, says what it does, and shows what the registry stored', async ({
    page,
  }) => {
    const credential = await ownerCredential();
    const sent = writes(page);

    await open(page, name);
    await expect(page.getByText('owned by arranged-by-e2e')).toBeVisible();
    await changeOwner(page).click();

    // Opens on the owner already shown, with nothing to review yet.
    await expect(page.getByLabel('New owner')).toHaveValue('arranged-by-e2e');
    await expect(page.getByRole('button', { name: 'Review change' })).toBeDisabled();

    // Padding, because the registry trims before it judges and the page must go where it was told.
    await page.getByLabel('New owner').fill('  team-after  ');
    await page.getByRole('button', { name: 'Review change' }).click();

    // The first click asks, and nothing has changed: not in the registry, not on the page.
    await expect(
      page.getByText(`Change the owner of ${name} from arranged-by-e2e to team-after?`),
    ).toBeVisible();
    await expect(page.getByText('Only the owner changes.')).toBeVisible();
    await expect(page.getByText('It is a label, not access')).toBeVisible();
    await expect(page.getByText('“owner changed”')).toBeVisible();
    await expect(page.getByText('Nobody is notified.')).toBeVisible();
    expect(sent.patches).toHaveLength(0);
    expect((await readSubject(credential, ENVIRONMENT, name)).owner).toBe('arranged-by-e2e');

    // The second does it.
    await page.getByRole('button', { name: 'Yes, change the owner' }).click();
    await expect(page.getByText(`The owner of ${name} is now team-after`)).toBeVisible();

    // The owner and nothing else was sent, trimmed, and never `deprecate`.
    expect(sent.patches).toHaveLength(1);
    expect(sent.patches[0]?.postDataJSON()).toEqual({ owner: 'team-after' });

    // What the page shows now is the registry's account, read afresh, and the editor has closed.
    await expect(page.getByText('owned by team-after')).toBeVisible();
    await expect(page.getByLabel('New owner')).toHaveCount(0);

    const view = await readSubject(credential, ENVIRONMENT, name);

    expect(view).toMatchObject({
      owner: 'team-after',
      lifecycle: 'ACTIVE',
      format: 'json',
      contentModel: 'OPEN',
      compatibilityPolicy: { mode: null, surface: null },
    });
  });

  test('is recorded as one entry that names who made it and not what changed', async () => {
    // The confirmation's third sentence, held against the audit trail: one SUBJECT_UPDATED, read
    // "owner changed", attributed to the account that signed in and not to the owner it named,
    // and naming neither the previous owner nor the new one.
    const rows = await readAudit(await ownerCredential(), ENVIRONMENT, name);
    const updates = rows.filter((r) => r.action === 'SUBJECT_UPDATED');

    expect(updates).toHaveLength(1);
    expect(updates[0]).toMatchObject({ actor: OWNER.email, detail: 'owner changed' });
    expect(JSON.stringify(updates[0])).not.toContain('team-after');
    expect(JSON.stringify(updates[0])).not.toContain('arranged-by-e2e');
  });

  test('withdraws the confirmation when the owner is edited, and sends nothing', async ({
    page,
  }) => {
    const sent = writes(page);

    await open(page, name);
    await changeOwner(page).click();
    await page.getByLabel('New owner').fill('first-choice');
    await page.getByRole('button', { name: 'Review change' }).click();
    await expect(page.getByRole('button', { name: 'Yes, change the owner' })).toBeVisible();

    await page.getByLabel('New owner').fill('second-choice');

    await expect(page.getByRole('button', { name: 'Yes, change the owner' })).toHaveCount(0);
    expect(sent.patches).toHaveLength(0);
    expect((await readSubject(await ownerCredential(), ENVIRONMENT, name)).owner).toBe(
      'team-after',
    );
  });

  test('has nothing to review for a blank owner or the owner already shown, and sends nothing', async ({
    page,
  }) => {
    const sent = writes(page);

    await open(page, name);
    await changeOwner(page).click();

    await page.getByLabel('New owner').fill('   ');
    await expect(page.getByText('Enter the new owner')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Review change' })).toBeDisabled();

    await page.getByLabel('New owner').fill('team-after');
    await expect(page.getByText(/is already the owner, as of the last time/)).toBeVisible();
    await expect(page.getByRole('button', { name: 'Review change' })).toBeDisabled();
    expect(sent.patches).toHaveLength(0);
  });

  test('says an over-long owner is wrong, with the registry’s own limit, and blocks it', async ({
    page,
  }) => {
    await open(page, name);
    await changeOwner(page).click();
    await page.getByLabel('New owner').fill('o'.repeat(257));

    await expect(page.getByText(/at most 256 characters; this one is 257/)).toBeVisible();
    await expect(page.getByLabel('New owner')).toHaveAttribute('aria-invalid', 'true');
    await expect(page.getByRole('button', { name: 'Review change' })).toBeDisabled();
  });

  test('says what the registry says when it refuses, and leaves the form editable', async ({
    page,
  }) => {
    // A change the form lets through and the registry refuses: the subject was retired after the
    // page read it. The page is a snapshot and the registry is the authority, so the refusal
    // renders in the registry's words, the form stays, and nothing on the page moves.
    const stale = named('retiredmeanwhile');
    await arrange(stale);

    await open(page, stale);
    await changeOwner(page).click();
    await page.getByLabel('New owner').fill('too-late');
    await page.getByRole('button', { name: 'Review change' }).click();
    await expect(page.getByRole('button', { name: 'Yes, change the owner' })).toBeVisible();

    await retireSubject(await ownerCredential(), ENVIRONMENT, stale);
    await page.getByRole('button', { name: 'Yes, change the owner' }).click();

    await expect(page.getByText('The registry refused this change')).toBeVisible();
    await expect(page.getByText(/is retired/)).toBeVisible();
    await expect(page.getByLabel('New owner')).toBeEditable();
    await expect(page.getByText('owned by arranged-by-e2e')).toBeVisible();
    expect((await readSubject(await ownerCredential(), ENVIRONMENT, stale)).owner).toBe(
      'arranged-by-e2e',
    );

    // And a fresh read of it offers nothing: retired is terminal.
    await page.reload();
    await expect(page.getByRole('heading', { name: stale })).toBeVisible();
    await expect(changeOwner(page)).toHaveCount(0);
    await expect(changePolicy(page)).toHaveCount(0);
  });
});

test.describe('changing the compatibility policy', () => {
  test.describe.configure({ mode: 'serial' });
  const name = named('policy');

  test.beforeAll(async () => {
    await arrange(name);
  });

  test('pins one, says what it does, and leaves no audit entry', async ({ page }) => {
    const credential = await ownerCredential();
    const sent = writes(page);

    await open(page, name);
    await expect(page.getByText('Inherited', { exact: true })).toBeVisible();
    await changePolicy(page).click();

    await page.getByLabel('Mode').selectOption('FULL_TRANSITIVE');
    // Half of one is wrong, as the registry says, and nothing can be reviewed.
    await expect(page.getByText(/Choose a surface as well as a mode/)).toBeVisible();
    await expect(page.getByRole('button', { name: 'Review change' })).toBeDisabled();

    await page.getByLabel('Surface').selectOption('SOURCE');
    await page.getByRole('button', { name: 'Review change' }).click();

    await expect(page.getByText(`Pin ${name} to FULL_TRANSITIVE · SOURCE?`)).toBeVisible();
    await expect(page.getByText(/takes effect for the next version registered/)).toBeVisible();
    await expect(page.getByText(/Versions already registered are not judged again/)).toBeVisible();
    await expect(page.getByText('This change is not written to the audit log.')).toBeVisible();
    await expect(page.getByText('Nobody is notified.')).toBeVisible();
    expect(sent.puts).toHaveLength(0);
    expect((await readSubject(credential, ENVIRONMENT, name)).compatibilityPolicy).toEqual({
      mode: null,
      surface: null,
    });

    await page.getByRole('button', { name: 'Yes, change the policy' }).click();
    await expect(
      page.getByText(/is now FULL_TRANSITIVE · SOURCE, as the registry answered/),
    ).toBeVisible();

    expect(sent.puts).toHaveLength(1);
    expect(sent.puts[0]?.postDataJSON()).toEqual({ mode: 'FULL_TRANSITIVE', surface: 'SOURCE' });

    // The page reads the subject again: the mode and the surface, in the Compatibility cell.
    await expect(page.getByText('FULL_TRANSITIVE', { exact: true })).toBeVisible();
    await expect(page.getByText('· SOURCE', { exact: true })).toBeVisible();
    expect((await readSubject(credential, ENVIRONMENT, name)).compatibilityPolicy).toEqual({
      mode: 'FULL_TRANSITIVE',
      surface: 'SOURCE',
    });

    // The confirmation's "not written to the audit log", held: arranged, then changed, and the
    // trail has only the creation.
    const rows = await readAudit(credential, ENVIRONMENT, name);

    expect(rows.map((r) => r.action)).toEqual(['SUBJECT_CREATED']);
  });

  test('inheriting asks a different question, and says what it is not', async ({ page }) => {
    const credential = await ownerCredential();
    const sent = writes(page);

    await open(page, name);
    await changePolicy(page).click();
    await page.getByLabel('Mode').selectOption('');
    await page.getByLabel('Surface').selectOption('');
    await page.getByRole('button', { name: 'Review change' }).click();

    await expect(
      page.getByText(`Remove the policy of ${name}, so that it inherits?`),
    ).toBeVisible();
    await expect(
      page.getByText('Inheriting is not the same as choosing the current default.'),
    ).toBeVisible();
    await expect(page.getByText('As this registry stands,')).toBeVisible();
    await expect(page.getByText('BACKWARD · WIRE_JSON', { exact: true })).toBeVisible();

    await page.getByRole('button', { name: 'Yes, inherit' }).click();
    await expect(page.getByText(/now has no policy of its own/)).toBeVisible();

    expect(sent.puts[0]?.postDataJSON()).toEqual({ mode: null, surface: null });
    await expect(page.getByText('Inherited', { exact: true })).toBeVisible();
    expect((await readSubject(credential, ENVIRONMENT, name)).compatibilityPolicy).toEqual({
      mode: null,
      surface: null,
    });
  });

  test('withdraws the confirmation when another policy is chosen, and sends nothing', async ({
    page,
  }) => {
    const sent = writes(page);

    await open(page, name);
    await changePolicy(page).click();
    await page.getByLabel('Mode').selectOption('FORWARD');
    await page.getByLabel('Surface').selectOption('WIRE');
    await page.getByRole('button', { name: 'Review change' }).click();
    await expect(page.getByRole('button', { name: 'Yes, change the policy' })).toBeVisible();

    await page.getByLabel('Mode').selectOption('FULL');

    await expect(page.getByRole('button', { name: 'Yes, change the policy' })).toHaveCount(0);
    expect(sent.puts).toHaveLength(0);
  });
});

test.describe('what a policy change does to registration', () => {
  test.describe.configure({ mode: 'serial' });
  const name = named('effect');

  test.beforeAll(async () => {
    // Starts permissive, so a breaking change registers: the baseline the confirmation's
    // "next registration" is measured against.
    await arrange(name, { compatibilityMode: 'NONE', compatibilitySurface: 'WIRE_JSON' });
    const credential = await ownerCredential();

    expect((await registerVersion(credential, ENVIRONMENT, name, schema('string'))).status).toBe(
      201,
    );
    const second = await registerVersion(credential, ENVIRONMENT, name, schema('integer'));

    expect(second.body).toMatchObject({ status: 'ACTIVE' });
  });

  test('takes effect for the next registration, and a held version stays held when it is relaxed', async ({
    page,
  }) => {
    const credential = await ownerCredential();

    await open(page, name);
    await expect(page.getByText('NONE', { exact: true })).toBeVisible();

    // Tighten it in the browser.
    await changePolicy(page).click();
    await page.getByLabel('Mode').selectOption('BACKWARD');
    await page.getByLabel('Surface').selectOption('WIRE_JSON');
    await page.getByRole('button', { name: 'Review change' }).click();
    await page.getByRole('button', { name: 'Yes, change the policy' }).click();
    await expect(page.getByText(/is now BACKWARD · WIRE_JSON/)).toBeVisible();

    // Versions already registered are not judged again.
    const afterTightening = await readSubject(credential, ENVIRONMENT, name);

    expect(afterTightening.versions.map((v) => v.status)).toEqual(['ACTIVE', 'ACTIVE']);

    // The next registration is the first to feel it: a breaking change is held.
    const held = await registerVersion(credential, ENVIRONMENT, name, schema('boolean'));

    expect(held.body).toMatchObject({ status: 'AWAITING_APPROVAL' });

    // Relax it, and the held version stays held.
    await page.reload();
    await expect(page.getByText('1 awaiting approval')).toBeVisible();
    await changePolicy(page).click();
    await page.getByLabel('Mode').selectOption('NONE');
    await page.getByRole('button', { name: 'Review change' }).click();
    await page.getByRole('button', { name: 'Yes, change the policy' }).click();
    await expect(page.getByText(/is now NONE · WIRE_JSON/)).toBeVisible();

    const afterRelaxing = await readSubject(credential, ENVIRONMENT, name);

    expect(afterRelaxing.versions.map((v) => v.status)).toEqual([
      'ACTIVE',
      'ACTIVE',
      'AWAITING_APPROVAL',
    ]);
    await expect(page.getByText('1 awaiting approval')).toBeVisible();
  });
});

test.describe('the sentence about inheriting, against an environment whose default is something else', () => {
  test('registration judges a subject with no policy of its own by the built-in default', async () => {
    // The environment's default is NONE, which holds nothing. If a subject that inherits followed
    // it, a breaking second version would register. It is held instead: judged by BACKWARD ·
    // WIRE_JSON, the registry's own. That is what `subject-policy-editor.ts` tells somebody who is
    // about to inherit, and this is where it is measured. When the registry starts reading the
    // environment's default this goes red, and that sentence, STATUS.md's row and ADR-032 change.
    const credential = await ownerCredential();
    const name = named('inherits');

    await ensureEnvironmentWithDefault(credential, INHERIT_ENVIRONMENT, {
      mode: 'NONE',
      surface: 'WIRE_JSON',
    });
    await arrange(name, {}, INHERIT_ENVIRONMENT);

    expect(
      (await registerVersion(credential, INHERIT_ENVIRONMENT, name, schema('string'))).status,
    ).toBe(201);
    const second = await registerVersion(credential, INHERIT_ENVIRONMENT, name, schema('boolean'));

    expect(second.body).toMatchObject({ status: 'AWAITING_APPROVAL' });
    expect((await readSubject(credential, INHERIT_ENVIRONMENT, name)).compatibilityPolicy).toEqual({
      mode: null,
      surface: null,
    });
  });
});

test.describe('a subject’s lifecycle decides whether it can be changed', () => {
  test('a deprecated one can, and a retired one cannot', async ({ page }) => {
    const credential = await ownerCredential();
    const name = named('life');

    await arrange(name);
    expect((await patchSubjectRaw(credential, ENVIRONMENT, name, { deprecate: true })).status).toBe(
      200,
    );

    await open(page, name);
    await expect(page.getByText('DEPRECATED', { exact: true })).toBeVisible();
    await expect(changeOwner(page)).toBeVisible();
    await expect(changePolicy(page)).toBeVisible();

    await retireSubject(credential, ENVIRONMENT, name);
    await page.reload();

    await expect(page.getByText('RETIRED', { exact: true })).toBeVisible();
    await expect(changeOwner(page)).toHaveCount(0);
    await expect(changePolicy(page)).toHaveCount(0);
  });
});

test.describe('a reader', () => {
  test.use({ storageState: READER_STORAGE_STATE });

  test('sees the owner and the policy and is offered neither change', async ({ page }) => {
    // Absent, not disabled (ADR-018): not a button to hover for a reason.
    await open(page, 'acme.e2e.OrderCreated');

    await expect(page.getByText(/owned by /)).toBeVisible();
    await expect(page.getByText('Compatibility', { exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: /^Change/ })).toHaveCount(0);
    await expect(page.getByText(/Change (owner|policy)/)).toHaveCount(0);
  });
});
