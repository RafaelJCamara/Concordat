import { expect, test } from '@playwright/test';
import { READER_STORAGE_STATE } from './global-setup';

/**
 * Notification subscriptions, against a registry that validates the endpoint.
 *
 * <b>Every subscription this creates is deleted before the block ends.</b> A live subscription
 * is delivered to by a background pump, and the rest of the suite generates real events —
 * approvals, registrations, contract changes — so one left behind would be sent traffic for the
 * remainder of the run, and for a webhook that would be outbound requests from the registry to
 * wherever it points. The webhook points at a closed local port for the same reason: nothing
 * leaves the machine. Run serially and cleaned up in the last tests, so what the registry holds
 * afterwards is what it held before.
 */
const RUN = Date.now();
const EMAIL = `e2e-notify-${RUN}@concordat.test`;
const HOOK = 'https://127.0.0.1:1/e2e-hook';
const HOOK_POLICY = 'https://127.0.0.1:1/e2e-policy-change-hook';

test.describe.serial('notifications', () => {
  test('is a settings tab, marked as the current page', async ({ page }) => {
    await page.goto('/settings/notifications');

    await expect(
      page.getByRole('navigation', { name: 'Settings' }).getByRole('link', {
        name: 'Notifications',
      }),
    ).toHaveAttribute('aria-current', 'page');
  });

  test('says that ticking nothing means every event', async ({ page }) => {
    await page.goto('/settings/notifications');

    await expect(page.getByText(/None ticked means every event/)).toBeVisible();
  });

  test('subscribes an email address to every event', async ({ page }) => {
    await page.goto('/settings/notifications');

    await page.getByLabel('Email address').fill(EMAIL);
    await page.getByRole('button', { name: 'Subscribe' }).click();

    const row = page.getByRole('listitem').filter({ hasText: EMAIL });

    await expect(row).toContainText('EMAIL');
    // An empty list is the registry's own spelling of "all", and must not be drawn as "none".
    await expect(row).toContainText('Every event');
    await expect(page.getByLabel('Email address')).toHaveValue('');
    // Email has nothing to sign, so there is no secret panel.
    await expect(page.getByText(/will not be shown again/)).toHaveCount(0);
  });

  test('is refused a webhook that is not https, in the registry’s own words', async ({ page }) => {
    await page.goto('/settings/notifications');

    await page.getByLabel('Channel').selectOption('WEBHOOK');
    await page.getByLabel('Webhook URL').fill('http://127.0.0.1:1/plain');
    await page.getByRole('button', { name: 'Subscribe' }).click();

    await expect(page.getByRole('heading', { name: /could not subscribe/i })).toBeVisible();
    await expect(page.getByLabel('Webhook URL')).toHaveValue('http://127.0.0.1:1/plain');
  });

  test('subscribes a webhook to chosen events, and shows its signing secret once', async ({
    page,
  }) => {
    await page.goto('/settings/notifications');

    await page.getByLabel('Channel').selectOption('WEBHOOK');
    await page.getByLabel('Webhook URL').fill(HOOK);
    await page.getByLabel('BREAKING_CHANGE_SUBMITTED').check();
    await page.getByLabel('ENFORCEMENT_VIOLATION').check();
    await page.getByRole('button', { name: 'Subscribe' }).click();

    await expect(page.getByText(/will not be shown again/)).toBeVisible();
    expect(await page.getByLabel('Webhook signing secret').inputValue()).not.toBe('');

    const row = page.getByRole('listitem').filter({ hasText: HOOK });

    await expect(row).toContainText('BREAKING_CHANGE_SUBMITTED, ENFORCEMENT_VIOLATION');
    await expect(row).not.toContainText('Every event');

    // Dismissing takes the secret off the page, and out of memory with it.
    await page.getByRole('button', { name: 'I have saved it' }).click();
    await expect(page.getByLabel('Webhook signing secret')).toHaveCount(0);
    expect(await page.content()).not.toContain('whsec_');
  });

  test('subscribes to a policy change, an event the registry accepts as its own', async ({
    page,
  }) => {
    // ADR-034. The web's list of events and the registry's are two lists, and a registry that has
    // not been taught the token answers 400 "unknown event" for it: this is the one place the two
    // are asked to agree at runtime. It is a webhook to a closed port, deleted below, for the
    // reason in the header: a live subscription is delivered to by a background pump, and the rest
    // of the suite changes policies.
    await page.goto('/settings/notifications');

    await page.getByLabel('Channel').selectOption('WEBHOOK');
    await page.getByLabel('Webhook URL').fill(HOOK_POLICY);
    await expect(page.getByText('A subject’s compatibility policy was changed.')).toBeVisible();
    await page.getByLabel('SUBJECT_POLICY_CHANGED').check();
    await expect(page.getByText('1 of 8 events.')).toBeVisible();
    await page.getByRole('button', { name: 'Subscribe' }).click();

    // A registry that does not know the token answers 400 here, and the page shows "Could not
    // subscribe" instead of the secret.
    await expect(page.getByText(/will not be shown again/)).toBeVisible();
    await page.getByRole('button', { name: 'I have saved it' }).click();

    // As the registry stored it and sent it back, after a fresh read.
    await page.reload();

    const row = page.getByRole('listitem').filter({ hasText: HOOK_POLICY });

    await expect(row).toContainText('SUBJECT_POLICY_CHANGED');
    await expect(row).not.toContainText('Every event');
  });

  test('does not show the secret again after a reload, because it cannot', async ({ page }) => {
    await page.goto('/settings/notifications');

    // The registry keeps only an encrypted reference.
    await expect(page.getByLabel('Webhook signing secret')).toHaveCount(0);
    await expect(page.getByText(/will not be shown again/)).toHaveCount(0);
    await expect(page.getByRole('listitem').filter({ hasText: HOOK })).toBeVisible();
  });

  test('mutes and unmutes, keeping the settings', async ({ page }) => {
    await page.goto('/settings/notifications');

    const row = page.getByRole('listitem').filter({ hasText: HOOK });

    await row.getByRole('button', { name: 'Mute' }).click();
    await expect(row).toContainText('MUTED');

    await page.reload();
    await expect(page.getByRole('listitem').filter({ hasText: HOOK })).toContainText('MUTED');

    await page
      .getByRole('listitem')
      .filter({ hasText: HOOK })
      .getByRole('button', {
        name: 'Unmute',
      })
      .click();
    await expect(page.getByRole('listitem').filter({ hasText: HOOK })).not.toContainText('MUTED');
    await expect(page.getByRole('listitem').filter({ hasText: HOOK })).toContainText(
      'BREAKING_CHANGE_SUBMITTED',
    );
  });

  test('a reader sees the subscriptions and none of the controls', async ({ browser }) => {
    const context = await browser.newContext({ storageState: READER_STORAGE_STATE });
    const page = await context.newPage();

    await page.goto('/settings/notifications');

    await expect(page.getByText(HOOK)).toBeVisible();
    await expect(page.getByRole('button', { name: 'Mute' })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Delete' })).toHaveCount(0);
    await expect(page.getByLabel('Webhook URL')).toHaveCount(0);

    await context.close();
  });

  test('deletes the webhook in two clicks, naming what is lost', async ({ page }) => {
    await page.goto('/settings/notifications');

    const row = page.getByRole('listitem').filter({ hasText: HOOK });
    await row.getByRole('button', { name: 'Delete' }).click();

    await expect(row.getByText(/mute it to keep them/)).toBeVisible();

    await row.getByRole('button', { name: 'Confirm' }).click();

    await expect(page.getByRole('listitem').filter({ hasText: HOOK })).toHaveCount(0);
  });

  test('deletes the policy-change webhook, so no policy a later spec changes is sent anywhere', async ({
    page,
  }) => {
    await page.goto('/settings/notifications');

    const row = page.getByRole('listitem').filter({ hasText: HOOK_POLICY });
    await row.getByRole('button', { name: 'Delete' }).click();
    await row.getByRole('button', { name: 'Confirm' }).click();

    await expect(page.getByRole('listitem').filter({ hasText: HOOK_POLICY })).toHaveCount(0);
  });

  test('deletes the email subscription, leaving the registry as it was found', async ({ page }) => {
    await page.goto('/settings/notifications');

    const row = page.getByRole('listitem').filter({ hasText: EMAIL });
    await row.getByRole('button', { name: 'Delete' }).click();
    await row.getByRole('button', { name: 'Confirm' }).click();

    await expect(page.getByRole('listitem').filter({ hasText: EMAIL })).toHaveCount(0);
  });

  test('reports no console errors', async ({ page }) => {
    const errors: string[] = [];
    page.on('console', (message) => {
      if (message.type() === 'error' && !message.text().startsWith('Failed to load resource')) {
        errors.push(message.text());
      }
    });
    page.on('pageerror', (error) => errors.push(error.message));

    await page.goto('/settings/notifications');
    await expect(page.getByRole('button', { name: 'Subscribe' })).toBeVisible();

    expect(errors).toEqual([]);
  });
});
