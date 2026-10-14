import { expect, test, type Locator, type Page } from '@playwright/test';
import { READER_STORAGE_STATE } from './global-setup';
import {
  addConsumeBindingRaw,
  createContractRaw,
  OWNER,
  ownerCredential,
  readAudit,
  readContract,
  resolveRoute,
  setEnforcementRaw,
} from './registry';

/**
 * Changing and removing a contract's bindings, on the contract's page, against the real registry.
 *
 * <b>This is the hole it closes, walked in a browser.</b> A binding used to have no identity, so
 * there was an `add` for each kind and nothing else: a binding added without a precedence could
 * never be separated from a later overlapping one, and the registry's own refusal told you to
 * "give the two bindings different precedence values" when the first had none to change. The first
 * test here reproduces it (the overlap is refused) and then closes it (the first binding is
 * given a precedence, and the second is accepted).
 *
 * <b>Every sentence in a confirmation is asserted against what the registry then does</b>, as in
 * `subject-settings.spec.ts`: that only the subjects and the precedence change and the binding
 * keeps its id, that an overlap is refused in the registry's own words and leaves the binding as
 * it was, that a removed binding stops governing its route (and a route something else still
 * matches stays governed), that one audit entry of its own action names the account and what
 * changed. When one stops being true, a sentence in `binding-editor.ts`, a row in STATUS.md and
 * ADR-035 change with it.
 *
 * <b>One contract per run, named by the run, in \`dev\`.</b> A contract cannot be deleted and its
 * audit rows are append-only, so a run leaves one behind, like \`contracts.spec.ts\` does (and this
 * file touches nothing that spec reads). It is arranged through the API where the arrangement is
 * not what is being tested (creating the contract, adding a consume binding).
 */
const RUN = Date.now();
const ENVIRONMENT = 'dev';
const NAME = `e2e-bindings-${RUN}`;
// An exchange of its own, per run. A route is governed by every contract in the environment that
// matches it, and `dev` already holds the other specs' contracts (and every earlier run's), so
// resolving `orders` / `orders.created` would answer with all of them. Naming the exchange for the
// run is what makes "the registry resolves this route to my binding" a claim about this contract.
const EXCHANGE = `bindings-${RUN}`;
const ORDER_CREATED = 'acme.e2e.OrderCreated';
const PAYMENT_TAKEN = 'acme.e2e.PaymentTaken';
const LEDGER_POSTED = 'acme.e2e.LedgerPosted';

test.describe.configure({ mode: 'serial' });

const open = async (page: Page) => {
  await page.goto(`/contracts/${NAME}`);
  await expect(page.getByRole('heading', { level: 1 })).toContainText(NAME);
};

const editor = (page: Page): Locator => page.locator('cd-binding-editor');

async function addPublish(
  page: Page,
  pattern: string,
  subject: string,
  precedence?: string,
): Promise<void> {
  await page.getByLabel('Exchange').fill(EXCHANGE);
  await page.getByLabel('Routing key pattern').fill(pattern);
  await page.getByLabel('Subjects', { exact: true }).first().fill(subject);
  if (precedence !== undefined) {
    await page.getByLabel(/Precedence/).fill(precedence);
  }
  await page.getByRole('button', { name: 'Add publish binding' }).click();
}

test.describe('changing and removing a contract’s bindings', () => {
  test.beforeAll(async () => {
    await createContractRaw(await ownerCredential(), ENVIRONMENT, NAME);
  });

  test('a binding added without a precedence is given one afterwards, which separates the overlap', async ({
    page,
  }) => {
    const credential = await ownerCredential();
    await open(page);

    // The trap: the broad binding has no precedence ...
    await addPublish(page, 'orders.#', ORDER_CREATED);
    await expect(page.getByText('Publishes · 1')).toBeVisible();
    const before = (await readContract(credential, ENVIRONMENT, NAME)).publishes[0]!;
    expect(before.precedence).toBeNull();

    // ... so a narrower one with different subjects is refused, and the refusal says what to do.
    await addPublish(page, 'orders.created', PAYMENT_TAKEN, '10');
    const form = page.locator('form[aria-labelledby="add-publish-heading"]');

    await expect(form.getByRole('heading', { name: /could not add the binding/i })).toBeVisible();
    await expect(form).toContainText('different precedence values');
    await expect(page.getByText('Publishes · 1')).toBeVisible();

    // The way out: the broad binding is changed, from its own row.
    await page.getByRole('button', { name: `Change ${EXCHANGE} / orders.#` }).click();

    await expect(editor(page)).toBeVisible();
    await expect(editor(page).getByLabel('Subjects')).toHaveValue(`${ORDER_CREATED}@latest`);
    await expect(editor(page).getByRole('button', { name: 'Review change' })).toBeDisabled();

    await editor(page)
      .getByLabel(/Precedence/)
      .fill('1');
    await editor(page).getByRole('button', { name: 'Review change' }).click();

    // What the confirmation says, before it is done.
    await expect(editor(page)).toContainText('Only the subjects and the precedence change.');
    await expect(editor(page)).toContainText('are what the binding is, and stay');
    await expect(editor(page)).toContainText('from the next resolution');
    await expect(editor(page)).toContainText('keeps its answer until its cache expires');
    await expect(editor(page)).toContainText('CONTRACT_BINDING_CHANGED');
    await expect(editor(page)).toContainText('Nobody is notified.');
    expect((await readContract(credential, ENVIRONMENT, NAME)).publishes[0]!.precedence).toBeNull();

    await editor(page).getByRole('button', { name: 'Yes, change the binding' }).click();

    await expect(page.getByRole('status')).toContainText('Binding changed.');
    await expect(editor(page)).toHaveCount(0);
    await expect(page.getByText('precedence 1')).toBeVisible();

    // Stored, with the same id and nothing else changed.
    const after = (await readContract(credential, ENVIRONMENT, NAME)).publishes[0]!;
    expect(after.id).toBe(before.id);
    expect(after.precedence).toBe(1);
    expect(after.exchange).toBe(EXCHANGE);
    expect(after.routingKeyPattern).toBe('orders.#');
    expect(after.subjects).toEqual(before.subjects);

    // And the refused binding is now accepted, unchanged from what was typed (kept by the form).
    await page.getByRole('button', { name: 'Add publish binding' }).click();
    await expect(page.getByText('Publishes · 2')).toBeVisible();
  });

  test('is refused a change that would undo the separation, in the registry’s words, and nothing changes', async ({
    page,
  }) => {
    const credential = await ownerCredential();
    const specific = (await readContract(credential, ENVIRONMENT, NAME)).publishes.find(
      (b) => b.routingKeyPattern === 'orders.created',
    )!;
    await open(page);

    await page.getByRole('button', { name: `Change ${EXCHANGE} / orders.created` }).click();
    // Equal precedence (1 = 1) separates nothing, and the two carry different subjects.
    await editor(page)
      .getByLabel(/Precedence/)
      .fill('1');
    await editor(page).getByRole('button', { name: 'Review change' }).click();
    await editor(page).getByRole('button', { name: 'Yes, change the binding' }).click();

    await expect(
      editor(page).getByRole('heading', { name: 'The registry refused this change' }),
    ).toBeVisible();
    await expect(editor(page)).toContainText('different precedence values');
    // The panel stays, with what was typed, because the next move is to edit it.
    await expect(editor(page).getByLabel(/Precedence/)).toHaveValue('1');

    const stored = (await readContract(credential, ENVIRONMENT, NAME)).publishes.find(
      (b) => b.id === specific.id,
    )!;
    expect(stored.precedence).toBe(10);

    await editor(page).getByRole('button', { name: 'Cancel' }).click();
    await expect(editor(page)).toHaveCount(0);
  });

  test('changes a binding’s subjects, and the registry resolves the route to them', async ({
    page,
  }) => {
    const credential = await ownerCredential();
    await open(page);

    await page.getByRole('button', { name: `Change ${EXCHANGE} / orders.created` }).click();
    await editor(page).getByLabel('Subjects').fill(`${LEDGER_POSTED}@2`);
    await editor(page).getByRole('button', { name: 'Review change' }).click();
    await editor(page).getByRole('button', { name: 'Yes, change the binding' }).click();

    await expect(page.getByRole('status')).toContainText('Binding changed.');
    await expect(page.getByRole('link', { name: new RegExp(LEDGER_POSTED) })).toBeVisible();

    // The binding with the higher precedence answers for the route.
    const route = await resolveRoute(credential, ENVIRONMENT, EXCHANGE, 'orders.created');
    expect(route.contracts).toEqual([NAME]);
    expect(route.subjects).toEqual([{ subject: LEDGER_POSTED, selector: '2' }]);
  });

  test('says what a removal does, keeps the binding on "Keep it", and a route another binding matches stays governed', async ({
    page,
  }) => {
    const credential = await ownerCredential();
    await setEnforcementRaw(credential, ENVIRONMENT, NAME, 'ENFORCE');
    await open(page);

    await page.getByRole('button', { name: `Remove ${EXCHANGE} / orders.created` }).click();

    await expect(editor(page)).toContainText(`Remove the binding of ${EXCHANGE} / orders.created?`);
    await expect(editor(page)).toContainText(
      `Routes matching orders.created on ${EXCHANGE} stop being governed by this binding.`,
    );
    await expect(editor(page)).toContainText('This contract enforces');
    await expect(editor(page)).toContainText('CONTRACT_BINDING_REMOVED');
    await expect(editor(page)).toContainText('Nobody is notified.');

    await editor(page).getByRole('button', { name: 'Keep it' }).click();
    expect((await readContract(credential, ENVIRONMENT, NAME)).publishes).toHaveLength(2);

    await page.getByRole('button', { name: `Remove ${EXCHANGE} / orders.created` }).click();
    await editor(page).getByRole('button', { name: 'Yes, remove the binding' }).click();

    await expect(page.getByRole('status')).toContainText('Binding removed.');
    await expect(page.getByText('Publishes · 1')).toBeVisible();

    // The broader binding still matches orders.created, so the route is still governed, strictly.
    const route = await resolveRoute(credential, ENVIRONMENT, EXCHANGE, 'orders.created');
    expect(route.contracts).toEqual([NAME]);
    expect(route.enforcement).toBe('ENFORCE');
    expect(route.subjects).toEqual([{ subject: ORDER_CREATED, selector: 'latest' }]);
  });

  test('a route nothing else governs resolves as ungoverned once its last binding is removed', async ({
    page,
  }) => {
    const credential = await ownerCredential();
    await open(page);

    await page.getByRole('button', { name: `Remove ${EXCHANGE} / orders.#` }).click();
    await editor(page).getByRole('button', { name: 'Yes, remove the binding' }).click();

    await expect(page.getByRole('status')).toContainText('Binding removed.');
    await expect(page.getByText('governs no route yet')).toBeVisible();

    const route = await resolveRoute(credential, ENVIRONMENT, EXCHANGE, 'orders.created');
    expect(route.contracts).toEqual([]);
    expect(route.enforcement).toBe('OFF');
    expect((await readContract(credential, ENVIRONMENT, NAME)).enforcement).toBe('ENFORCE');
  });

  test('changes and removes a consume binding', async ({ page }) => {
    const credential = await ownerCredential();
    await addConsumeBindingRaw(credential, ENVIRONMENT, NAME, 'billing.orders', [
      { subject: ORDER_CREATED, selector: 'latest' },
    ]);
    await open(page);

    await page.getByRole('button', { name: 'Change billing.orders' }).click();
    // A consume binding has no precedence to offer.
    await expect(editor(page).getByLabel(/Precedence/)).toHaveCount(0);
    await editor(page).getByLabel('Subjects').fill(`${ORDER_CREATED}@2`);
    await editor(page).getByRole('button', { name: 'Review change' }).click();
    await expect(editor(page)).toContainText(
      'The queue and the virtual host are what the binding is',
    );
    await editor(page).getByRole('button', { name: 'Yes, change the binding' }).click();

    await expect(page.getByRole('status')).toContainText('Binding changed.');
    expect((await readContract(credential, ENVIRONMENT, NAME)).consumes[0]!.subjects).toEqual([
      { subject: ORDER_CREATED, selector: '2' },
    ]);

    await page.getByRole('button', { name: 'Remove billing.orders' }).click();
    await expect(editor(page)).toContainText('billing.orders is no longer expected to receive');
    await editor(page).getByRole('button', { name: 'Yes, remove the binding' }).click();

    await expect(page.getByRole('status')).toContainText('Binding removed.');
    await expect(page.getByText('Consumes · 0')).toBeVisible();
    expect((await readContract(credential, ENVIRONMENT, NAME)).consumes).toEqual([]);
  });

  test('left one entry of its own action for each, naming the account and what changed', async () => {
    const credential = await ownerCredential();
    const rows = await readAudit(credential, ENVIRONMENT, NAME);
    const of = (action: string) => rows.filter((r) => r.action === action);

    // Newest first. Changes: the precedence, the subjects, the consume subjects. (The refused
    // change left nothing, and neither did the confirmation that was cancelled.)
    expect(of('CONTRACT_BINDING_CHANGED').map((r) => r.detail)).toEqual([
      `consume billing.orders: subjects ${ORDER_CREATED}@latest -> ${ORDER_CREATED}@2`,
      `publish ${EXCHANGE} / orders.created: subjects ${PAYMENT_TAKEN}@latest -> ${LEDGER_POSTED}@2`,
      `publish ${EXCHANGE} / orders.#: precedence none -> 1`,
    ]);
    expect(of('CONTRACT_BINDING_REMOVED').map((r) => r.detail)).toEqual([
      `consume billing.orders -> ${ORDER_CREATED}@2`,
      `publish ${EXCHANGE} / orders.# -> ${ORDER_CREATED}@latest`,
      `publish ${EXCHANGE} / orders.created -> ${LEDGER_POSTED}@2`,
    ]);

    for (const row of [...of('CONTRACT_BINDING_CHANGED'), ...of('CONTRACT_BINDING_REMOVED')]) {
      expect(row.actor).toBe(OWNER.email);
      expect(row.target).toBe(NAME);
    }
  });

  test('reports no console errors, even for a refused change', async ({ page }) => {
    const credential = await ownerCredential();
    await createContractRaw(credential, ENVIRONMENT, `${NAME}-quiet`);

    const errors: string[] = [];
    page.on('console', (message) => {
      if (message.type() === 'error' && !message.text().startsWith('Failed to load resource')) {
        errors.push(message.text());
      }
    });
    page.on('pageerror', (error) => errors.push(error.message));

    await page.goto(`/contracts/${NAME}-quiet`);
    await addPublish(page, 'orders.#', ORDER_CREATED, '1');
    await expect(page.getByText('Publishes · 1')).toBeVisible();
    await addPublish(page, 'orders.created', PAYMENT_TAKEN, '2');
    await expect(page.getByText('Publishes · 2')).toBeVisible();

    await page.getByRole('button', { name: `Change ${EXCHANGE} / orders.created` }).click();
    await editor(page)
      .getByLabel(/Precedence/)
      .fill('1');
    await editor(page).getByRole('button', { name: 'Review change' }).click();
    await editor(page).getByRole('button', { name: 'Yes, change the binding' }).click();
    await expect(
      editor(page).getByRole('heading', { name: 'The registry refused this change' }),
    ).toBeVisible();

    expect(errors).toEqual([]);
  });
});

test.describe('changing and removing a binding, as a reader', () => {
  test.use({ storageState: READER_STORAGE_STATE });

  test('sees the bindings and is offered neither', async ({ page }) => {
    const credential = await ownerCredential();
    await createContractRaw(credential, ENVIRONMENT, `${NAME}-read`);
    await addConsumeBindingRaw(credential, ENVIRONMENT, `${NAME}-read`, 'orders.q', [
      { subject: ORDER_CREATED, selector: 'latest' },
    ]);

    await page.goto(`/contracts/${NAME}-read`);

    await expect(page.getByText('Consumes · 1')).toBeVisible();
    await expect(page.getByRole('button', { name: /^Change / })).toHaveCount(0);
    await expect(page.getByRole('button', { name: /^Remove / })).toHaveCount(0);
  });
});
