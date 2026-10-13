import { expect, test } from '@playwright/test';
import { READER_STORAGE_STATE } from './global-setup';

/**
 * Contracts, against a registry that decides what overlaps.
 *
 * <b>One contract per run, named by the run, and everything is done to that one.</b> A contract
 * is created, given bindings and moved between modes, none of which can be undone from the UI,
 * so a test that reused a fixed name would pass once and be refused as a duplicate every run
 * after. It costs the registry one contract per run, which is the honest price of testing
 * something with a consequence — and nothing here touches a contract another spec depends on,
 * because none does.
 *
 * <b>The overlap refusal is the registry's own.</b> `orders.*` and `*.created` are textually
 * unrelated and both match `orders.created`, so telling them apart means intersecting the two
 * patterns, which is not something the browser can do. A unit test with a faked refusal agrees
 * with whoever wrote it; this is the only place the registry says it.
 */
const NAME = `e2e-contract-${Date.now()}`;
const SUBJECT = 'acme.e2e.OrderCreated';

test.describe.configure({ mode: 'serial' });

test.describe('contracts', () => {
  test('creates one, lets the registry choose the mode, and says it governs nothing yet', async ({
    page,
  }) => {
    await page.goto('/contracts');

    await page.getByLabel('New contract').fill(NAME);
    await page.getByRole('button', { name: 'Create' }).click();

    // MONITOR is the registry's own default: a contract authored by guessing about a live
    // topology must not start refusing traffic the moment it is written.
    await expect(page.getByRole('status')).toContainText(`Created ${NAME} as MONITOR`);
    await expect(page.getByRole('status')).toContainText('governs nothing until it has a binding');
    // In the list as well as in the announcement, so scoped to the list.
    await expect(page.getByRole('listitem').getByRole('link', { name: NAME })).toBeVisible();
  });

  test('empties the form after creating, so a second click cannot send a duplicate', async ({
    page,
  }) => {
    await page.goto('/contracts');
    await expect(page.getByLabel('New contract')).toHaveValue('');
  });

  test('refuses a duplicate name with the registry’s own words', async ({ page }) => {
    await page.goto('/contracts');

    await page.getByLabel('New contract').fill(NAME);
    await page.getByRole('button', { name: 'Create' }).click();

    await expect(page.getByRole('heading', { name: /could not create/i })).toBeVisible();
    await expect(page.getByRole('link', { name: NAME })).toHaveCount(1);
  });

  test('opens from the list and shows an empty contract plainly', async ({ page }) => {
    await page.goto('/contracts');
    await page.getByRole('link', { name: NAME }).click();

    await expect(page).toHaveURL(new RegExp(`/contracts/${NAME}$`));
    await expect(page.getByRole('heading', { level: 1 })).toContainText(NAME);
    await expect(page.getByText('governs no route yet')).toBeVisible();
  });

  test('adds a publish binding, shown only once the registry stored it', async ({ page }) => {
    await page.goto(`/contracts/${NAME}`);

    await page.getByLabel('Exchange').fill('orders');
    await page.getByLabel('Routing key pattern').fill('orders.*');
    await page.getByLabel('Subjects', { exact: true }).first().fill(SUBJECT);
    // Declared up front: a binding cannot be edited afterwards, and the registry only separates
    // an overlap when *both* bindings carry a value and the values differ.
    await page.getByLabel(/Precedence/).fill('1');
    await page.getByRole('button', { name: 'Add publish binding' }).click();

    await expect(page.getByRole('status')).toContainText('Publish binding added');
    await expect(page.getByText('Publishes · 1')).toBeVisible();
    await expect(page.getByRole('link', { name: new RegExp(SUBJECT) })).toBeVisible();

    // A bare subject means latest, which the registry stored as such.
    await expect(page.getByText('@latest')).toBeVisible();
    // The form emptied itself, so the same binding cannot be sent twice.
    await expect(page.getByLabel('Exchange')).toHaveValue('');
  });

  test('is refused an overlapping binding, by the registry, and keeps what was typed', async ({
    page,
  }) => {
    await page.goto(`/contracts/${NAME}`);

    // `*.created` and `orders.*` both match `orders.created`, and these carry different
    // subjects. This one declares no precedence, so nothing separates them.
    await page.getByLabel('Exchange').fill('orders');
    await page.getByLabel('Routing key pattern').fill('*.created');
    await page.getByLabel('Subjects', { exact: true }).first().fill('acme.e2e.PaymentTaken');
    await page.getByRole('button', { name: 'Add publish binding' }).click();

    const form = page.locator('form[aria-labelledby="add-publish-heading"]');

    await expect(form.getByRole('heading', { name: /could not add the binding/i })).toBeVisible();
    // Not added: the contract still has exactly the one binding.
    await expect(page.getByText('Publishes · 1')).toBeVisible();
    // And the form is as it was, because the next move is to edit it.
    await expect(page.getByLabel('Routing key pattern')).toHaveValue('*.created');
  });

  test('accepts the same overlap once the two precedences differ', async ({ page }) => {
    await page.goto(`/contracts/${NAME}`);

    await page.getByLabel('Exchange').fill('orders');
    await page.getByLabel('Routing key pattern').fill('*.created');
    await page.getByLabel('Subjects', { exact: true }).first().fill('acme.e2e.PaymentTaken');
    await page.getByLabel(/Precedence/).fill('2');
    await page.getByRole('button', { name: 'Add publish binding' }).click();

    await expect(page.getByText('Publishes · 2')).toBeVisible();
    await expect(page.getByText('precedence 1')).toBeVisible();
    await expect(page.getByText('precedence 2')).toBeVisible();
  });

  test('stops a malformed subject before it is sent', async ({ page }) => {
    await page.goto(`/contracts/${NAME}`);

    await page.getByLabel('Queue').fill('billing.orders');
    await page.getByLabel('Subjects', { exact: true }).last().fill('acme.e2e.OrderCreated@');
    await page.getByRole('button', { name: 'Add consume binding' }).click();

    await expect(page.getByRole('alert')).toContainText('after it');
    await expect(page.getByText('Consumes · 0')).toBeVisible();
  });

  test('adds a consume binding', async ({ page }) => {
    await page.goto(`/contracts/${NAME}`);

    await page.getByLabel('Queue').fill('billing.orders');
    await page.getByLabel('Subjects', { exact: true }).last().fill(`${SUBJECT}@1`);
    await page.getByRole('button', { name: 'Add consume binding' }).click();

    await expect(page.getByText('Consumes · 1')).toBeVisible();
    await expect(page.getByText('billing.orders')).toBeVisible();
  });

  test('moves to OFF in one click, the safe direction', async ({ page }) => {
    await page.goto(`/contracts/${NAME}`);

    await page.getByLabel('Mode').selectOption('OFF');
    await page.getByRole('button', { name: 'Change to OFF' }).click();

    await expect(page.getByRole('status')).toContainText('Enforcement is now OFF');
    await expect(page.getByRole('heading', { level: 1 }).locator('..')).toContainText('OFF');
  });

  test('takes two clicks to ENFORCE, and Cancel changes nothing', async ({ page }) => {
    await page.goto(`/contracts/${NAME}`);

    await page.getByLabel('Mode').selectOption('ENFORCE');
    await page.getByRole('button', { name: 'Change to ENFORCE' }).click();

    await expect(page.getByText(/refuse a message that violates it/)).toBeVisible();

    await page.getByRole('button', { name: 'Cancel' }).click();

    // The registry, not the page, says it is still OFF.
    await page.reload();
    await expect(page.getByRole('heading', { level: 1 }).locator('..')).toContainText('OFF');
  });

  test('enforces on the second click', async ({ page }) => {
    await page.goto(`/contracts/${NAME}`);

    await page.getByLabel('Mode').selectOption('ENFORCE');
    await page.getByRole('button', { name: 'Change to ENFORCE' }).click();
    await page.getByRole('button', { name: 'Confirm' }).click();

    await expect(page.getByRole('status')).toContainText('Enforcement is now ENFORCE');

    await page.reload();
    await expect(page.getByRole('heading', { level: 1 }).locator('..')).toContainText('ENFORCE');
  });

  test('is listed with its mode and binding counts', async ({ page }) => {
    await page.goto('/contracts');

    const row = page.getByRole('listitem').filter({ has: page.getByRole('link', { name: NAME }) });

    await expect(row).toContainText('ENFORCE');
    await expect(row).toContainText('2 publish');
    await expect(row).toContainText('1 consume');
  });

  test('says plainly when the contract does not exist', async ({ page }) => {
    await page.goto('/contracts/no-such-contract');

    await expect(
      page.getByRole('heading', { name: /could not load this contract/i }),
    ).toBeVisible();
  });

  test('reports no console errors', async ({ page }) => {
    const errors: string[] = [];
    page.on('console', (message) => {
      if (message.type() === 'error') {
        errors.push(message.text());
      }
    });
    page.on('pageerror', (error) => errors.push(error.message));

    await page.goto('/contracts');
    await page.getByRole('link', { name: NAME }).click();
    await expect(page.getByText('Publishes · 2')).toBeVisible();

    // A refused binding is an expected 4xx the screen renders; it must not also be a console
    // error. Provoke one and check.
    await page.getByLabel('Exchange').fill('orders');
    await page.getByLabel('Routing key pattern').fill('#');
    await page.getByLabel('Subjects', { exact: true }).first().fill('acme.e2e.PaymentTaken');
    await page.getByRole('button', { name: 'Add publish binding' }).click();
    await expect(page.getByRole('heading', { name: /could not add the binding/i })).toBeVisible();

    expect(errors.filter((e) => !e.startsWith('Failed to load resource'))).toEqual([]);
  });
});

test.describe('contracts, as a reader', () => {
  // Signed in once by global setup rather than in the test: sign-in is rate-limited. Depends on
  // the contract the serial block above created, which is why it follows it.
  test.use({ storageState: READER_STORAGE_STATE });

  test('sees the contract and none of the controls', async ({ page }) => {
    await page.goto('/contracts');
    await expect(page.getByRole('link', { name: NAME })).toBeVisible();
    await expect(page.getByLabel('New contract')).toHaveCount(0);

    await page.goto(`/contracts/${NAME}`);
    await expect(page.getByText('Publishes · 2')).toBeVisible();
    await expect(page.getByLabel('Mode')).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Add publish binding' })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Add consume binding' })).toHaveCount(0);
  });
});
