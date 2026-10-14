import { expect, test } from '@playwright/test';
import {
  COMPATIBILITY_MODE_MEANING,
  COMPATIBILITY_MODES,
  COMPATIBILITY_SURFACE_MEANING,
  COMPATIBILITY_SURFACES,
  type CompatibilityMode,
  type CompatibilitySurface,
} from '../src/app/domain/registry/compatibility';
import {
  chooseAppearance,
  contrastOf,
  expectNoSidewaysOverflow,
  openByKeyboard,
} from './compatibility-help';
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

/**
 * What a compatibility mode and surface mean, where the environment's default is shown and chosen.
 *
 * <b>Reads and changes nothing.</b> It chooses other options in the selects and never saves, which
 * is how `explains the registration policy` above is written, because `dev`'s default is what
 * every other spec is judged against.
 *
 * It is a phone, in both appearances, because that is where the prototype's hover tooltip simply did
 * not exist. Each run opens the explanation with the keyboard, checks that the words on the page are
 * the words the domain holds for the tokens on the page, that the page does not scroll sideways with
 * every term open, and that the text is readable against what is behind it.
 */
for (const appearance of ['light', 'dark'] as const) {
  test.describe(`explaining compatibility, ${appearance}, at 390px`, () => {
    test.use({ viewport: { width: 390, height: 844 } });

    test.beforeEach(async ({ page }) => {
      await chooseAppearance(page, appearance);
    });

    test('describes each select by what its choice means, and follows the choice', async ({
      page,
    }) => {
      await page.goto('/settings/environment');

      const mode = page.getByLabel('Default compatibility mode');
      const surface = page.getByLabel('Default compatibility surface');
      await expect(mode).toBeVisible();

      // Whatever the environment holds now: this spec does not decide `dev`'s default.
      const held = {
        mode: (await mode.inputValue()) as CompatibilityMode,
        surface: (await surface.inputValue()) as CompatibilitySurface,
      };
      await expect(mode).toHaveAccessibleDescription(COMPATIBILITY_MODE_MEANING[held.mode]);
      await expect(surface).toHaveAccessibleDescription(
        COMPATIBILITY_SURFACE_MEANING[held.surface],
      );

      // The sentence is a description and not a name: the label is still the only way in, which
      // is what every other spec in this suite finds these two by.
      await expect(page.getByLabel('Default compatibility mode')).toHaveCount(1);
      await expect(page.getByLabel('Default compatibility surface')).toHaveCount(1);

      await mode.selectOption('FULL_TRANSITIVE');
      await surface.selectOption('SOURCE');

      await expect(mode).toHaveAccessibleDescription(COMPATIBILITY_MODE_MEANING.FULL_TRANSITIVE);
      await expect(surface).toHaveAccessibleDescription(COMPATIBILITY_SURFACE_MEANING.SOURCE);

      // Not saved: the registry still holds what it held.
      await page.reload();
      await expect(page.getByLabel('Default compatibility mode')).toHaveValue(held.mode);
    });

    test('opens from the keyboard, explains the default shown, and fits the screen', async ({
      page,
    }) => {
      await page.goto('/settings/environment');

      const mode = await page.getByLabel('Default compatibility mode').inputValue();
      const surface = await page.getByLabel('Default compatibility surface').inputValue();
      const guide = page.getByRole('button', { name: 'What does this default mean?' });

      // Closed, the terms are not on the page: `getByText(token, { exact: true })` is how other
      // specs find a policy, and it matches hidden text too.
      await expect(page.locator('dt', { hasText: new RegExp(`^${mode}$`) })).toHaveCount(0);

      await openByKeyboard(page, guide);

      const panel = page.locator(`#${await guide.getAttribute('aria-controls')}`);
      await expect(panel.locator('dt')).toHaveText([mode, surface]);
      await expect(panel.locator('dd')).toHaveText([
        COMPATIBILITY_MODE_MEANING[mode as CompatibilityMode],
        COMPATIBILITY_SURFACE_MEANING[surface as CompatibilitySurface],
      ]);

      await expectNoSidewaysOverflow(page, panel.locator('dt, dd'));
      for (const ratio of await contrastOf(panel.locator('dt, dd'))) {
        expect(ratio).toBeGreaterThanOrEqual(4.5);
      }

      // The same key closes it.
      await page.keyboard.press('Enter');
      await expect(guide).toHaveAttribute('aria-expanded', 'false');
      await expect(panel).toHaveCount(0);
    });

    test('offers every word to somebody choosing, and still fits with all ten open', async ({
      page,
    }) => {
      await page.goto('/settings/environment');

      const guide = page.getByRole('button', { name: 'What do these choices mean?' });
      // Space as well as Enter: a button answers both, and a handler on keydown would not.
      await openByKeyboard(page, guide, 'Space');

      const panel = page.locator(`#${await guide.getAttribute('aria-controls')}`);
      await expect(panel.locator('dt')).toHaveText([
        ...COMPATIBILITY_MODES,
        ...COMPATIBILITY_SURFACES,
      ]);
      await expect(panel.locator('dd')).toHaveText([
        ...COMPATIBILITY_MODES.map((m) => COMPATIBILITY_MODE_MEANING[m]),
        ...COMPATIBILITY_SURFACES.map((s) => COMPATIBILITY_SURFACE_MEANING[s]),
      ]);

      await expectNoSidewaysOverflow(page, panel.locator('dt, dd'));
      for (const ratio of await contrastOf(panel.locator('dt, dd'))) {
        expect(ratio).toBeGreaterThanOrEqual(4.5);
      }
    });
  });
}

test.describe('explaining compatibility on a touch screen', () => {
  test.use({ viewport: { width: 390, height: 844 }, hasTouch: true });

  test('opens with a tap, which is all a phone has', async ({ page }) => {
    await page.goto('/settings/environment');

    const guide = page.getByRole('button', { name: 'What does this default mean?' });
    await guide.tap();

    await expect(guide).toHaveAttribute('aria-expanded', 'true');
    await expect(page.locator(`#${await guide.getAttribute('aria-controls')}`)).toBeVisible();
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
