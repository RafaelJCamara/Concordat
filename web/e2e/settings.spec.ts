import { expect, test } from '@playwright/test';
import { READER_STORAGE_STATE } from './global-setup';

/**
 * Environment settings and brokers, against a registry that decides what is valid.
 *
 * <b>It does not change `dev`'s registration policy or its compatibility default.</b> Both are
 * shared with every other spec: moving the policy to `CI_ONLY` would lock the owner out of the
 * writes half this suite makes, and the compatibility default changes what a registration is
 * judged against. They are unit-tested — what is sent, and what is not — and the pre-release
 * toggle, which affects nothing else here, is exercised for real and put back.
 *
 * <b>A broker is added, used and removed inside one serial block, named by the run</b>, so the
 * registry is left as found. Its endpoint is a port nothing listens on: the registry attempts a
 * real AMQP handshake, fails, records why and returns it — which is the behaviour the page
 * exists to show, and a reachable broker would need a RabbitMQ this suite does not start.
 */
const RUN = Date.now();
const BROKER = `e2e-broker-${RUN}`;
const DEAD_ENDPOINT = 'amqp://127.0.0.1:1';

test.describe('environment settings', () => {
  test('shows the environment’s own rules as text', async ({ page }) => {
    await page.goto('/settings/environment');

    await expect(page.getByRole('heading', { level: 1, name: 'Settings' })).toBeVisible();
    // The summary's own terms, as definitions — the edit form below repeats several of these
    // words as labels, which is why this does not match on bare text.
    for (const term of ['Registration', 'Default compatibility', 'Pre-release versions']) {
      await expect(page.locator('dt', { hasText: term })).toBeVisible();
    }
  });

  test('opens from the sidebar’s Settings row, which stays marked on every tab', async ({
    page,
  }) => {
    await page.goto('/settings');

    await expect(page).toHaveURL(/\/settings\/environment$/);

    const row = page.getByRole('navigation', { name: 'Main' }).getByRole('link', {
      name: 'Settings',
    });
    await expect(row).toHaveAttribute('aria-current', 'page');

    await page
      .getByRole('navigation', { name: 'Settings' })
      .getByRole('link', {
        name: 'Brokers',
      })
      .click();

    await expect(page).toHaveURL(/\/settings\/brokers$/);
    await expect(row).toHaveAttribute('aria-current', 'page');
  });

  test('has nothing to save until something changes', async ({ page }) => {
    await page.goto('/settings/environment');

    await expect(page.getByRole('button', { name: 'Save changes' })).toBeDisabled();
    await expect(page.getByText('Nothing has changed.')).toBeVisible();
  });

  test('saves a description, and the registry agrees after a reload', async ({ page }) => {
    const description = `e2e run ${RUN}`;
    await page.goto('/settings/environment');

    await page.getByLabel('Description').fill(description);
    await page.getByRole('button', { name: 'Save changes' }).click();

    await expect(page.getByRole('status')).toContainText('Settings saved');

    await page.reload();
    await expect(page.getByText(description).first()).toBeVisible();
    await expect(page.getByLabel('Description')).toHaveValue(description);
  });

  test('turns pre-release versions on and puts them back', async ({ page }) => {
    // The checkbox decision #8 left owing. Read first, then toggle, then restore: the state this
    // leaves behind is the state it found, whichever that was.
    await page.goto('/settings/environment');

    const box = page.getByLabel('Allow pre-release versions');
    const original = await box.isChecked();

    await box.setChecked(!original);
    await page.getByRole('button', { name: 'Save changes' }).click();
    await expect(page.getByRole('status')).toContainText('Settings saved');

    await page.reload();
    await expect(page.getByLabel('Allow pre-release versions')).toBeChecked({ checked: !original });

    await page.getByLabel('Allow pre-release versions').setChecked(original);
    await page.getByRole('button', { name: 'Save changes' }).click();
    await expect(page.getByRole('status')).toContainText('Settings saved');

    await page.reload();
    await expect(page.getByLabel('Allow pre-release versions')).toBeChecked({ checked: original });
  });

  test('explains the registration policy before it is changed', async ({ page }) => {
    await page.goto('/settings/environment');

    await page.getByLabel('Registration policy').selectOption('CI_ONLY');

    await expect(page.getByText(/A person cannot, however senior/)).toBeVisible();

    // Not saved: the registry is still on its own policy. This only reads the explanation.
    await page.reload();
    await expect(page.getByLabel('Registration policy')).not.toHaveValue('CI_ONLY');
  });
});

test.describe.serial('brokers', () => {
  test('adds one, and says it has not been checked yet', async ({ page }) => {
    await page.goto('/settings/brokers');

    await page.getByLabel('Display name').fill(BROKER);
    await page.getByLabel('Endpoint').fill(DEAD_ENDPOINT);
    await page.getByRole('button', { name: 'Add broker' }).click();

    await expect(page.getByRole('status')).toContainText('Broker added');

    const row = page.getByRole('listitem').filter({ hasText: BROKER });

    await expect(row).toContainText('UNKNOWN');
    await expect(row).toContainText('Never checked');
    await expect(row).toContainText('no credentials');
    await expect(page.getByLabel('Display name')).toHaveValue('');
  });

  test('is refused a duplicate display name, in the registry’s words', async ({ page }) => {
    await page.goto('/settings/brokers');

    await page.getByLabel('Display name').fill(BROKER);
    await page.getByLabel('Endpoint').fill('amqp://127.0.0.1:2');
    await page.getByRole('button', { name: 'Add broker' }).click();

    await expect(page.getByRole('heading', { name: /could not add the broker/i })).toBeVisible();
    // What was typed stays, because the next move is to edit it.
    await expect(page.getByLabel('Display name')).toHaveValue(BROKER);
  });

  test('a check against nothing is a finding, not an error', async ({ page }) => {
    await page.goto('/settings/brokers');

    const row = page.getByRole('listitem').filter({ hasText: BROKER });
    await row.getByRole('button', { name: 'Check connection' }).click();

    // The registry attempted a real handshake, failed, recorded why and returned it. A page
    // that reported this as its own failure would blame the registry for somebody else's outage.
    await expect(row).toContainText('UNREACHABLE');
    await expect(row.getByText('Checked')).toBeVisible();
    await expect(page.getByRole('heading', { name: /could not check/i })).toHaveCount(0);
  });

  test('stores a credential and never shows it back', async ({ page }) => {
    await page.goto('/settings/brokers');

    const row = page.getByRole('listitem').filter({ hasText: BROKER });
    await row.getByRole('button', { name: 'Set credentials' }).click();

    await row.getByLabel('Username').fill('guest');
    await row.getByLabel('Password').fill('s3cret-value');
    await row.getByRole('button', { name: 'Save credentials' }).click();

    await expect(row).toContainText('credentials stored');

    // Closed and emptied, and the secret is nowhere in the page the registry sent back.
    await expect(row.getByLabel('Password')).toHaveCount(0);
    expect(await page.content()).not.toContain('s3cret-value');

    await page.reload();
    expect(await page.content()).not.toContain('s3cret-value');
    await expect(page.getByRole('listitem').filter({ hasText: BROKER })).toContainText(
      'credentials stored',
    );
  });

  test('a reader may check a broker and change nothing', async ({ browser }) => {
    const context = await browser.newContext({ storageState: READER_STORAGE_STATE });
    const page = await context.newPage();

    await page.goto('/settings/brokers');

    const row = page.getByRole('listitem').filter({ hasText: BROKER });

    await expect(row.getByRole('button', { name: 'Check connection' })).toBeVisible();
    await expect(row.getByRole('button', { name: /credentials/i })).toHaveCount(0);
    await expect(row.getByRole('button', { name: 'Remove broker' })).toHaveCount(0);
    await expect(page.getByLabel('Display name')).toHaveCount(0);

    await context.close();
  });

  test('removes the credentials in two clicks', async ({ page }) => {
    await page.goto('/settings/brokers');

    const row = page.getByRole('listitem').filter({ hasText: BROKER });
    await row.getByRole('button', { name: 'Remove credentials' }).click();

    await expect(row.getByText(/The next check will fail/)).toBeVisible();

    await row.getByRole('button', { name: 'Confirm' }).click();

    await expect(row).toContainText('no credentials');
  });

  test('removes the broker in two clicks, leaving the registry as it was found', async ({
    page,
  }) => {
    await page.goto('/settings/brokers');

    const row = page.getByRole('listitem').filter({ hasText: BROKER });
    await row.getByRole('button', { name: 'Remove broker' }).click();

    await expect(row.getByText(/Bindings that name it stop applying/)).toBeVisible();

    await row.getByRole('button', { name: 'Confirm' }).click();

    await expect(page.getByRole('listitem').filter({ hasText: BROKER })).toHaveCount(0);
  });

  test('reports no console errors', async ({ page }) => {
    const errors: string[] = [];
    page.on('console', (message) => {
      if (message.type() === 'error' && !message.text().startsWith('Failed to load resource')) {
        errors.push(message.text());
      }
    });
    page.on('pageerror', (error) => errors.push(error.message));

    await page.goto('/settings/brokers');
    await expect(page.getByRole('heading', { level: 1, name: 'Settings' })).toBeVisible();
    await page.goto('/settings/environment');
    await expect(page.getByRole('button', { name: 'Save changes' })).toBeVisible();

    expect(errors).toEqual([]);
  });
});
