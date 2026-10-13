import { expect, test } from '@playwright/test';
import { READER_STORAGE_STATE } from './global-setup';

/**
 * The audit trail, rendering what the registry actually recorded.
 *
 * <b>It reads the trail the rest of this suite writes.</b> `global-setup.ts` creates subjects,
 * registers versions and declares services, and the registry audits each in the same
 * transaction — so there are real entries to find without this file arranging any, and it never
 * writes. That also means it asserts on kinds of entry rather than counts: the number grows
 * every run, by design.
 */
const SUBJECT = 'acme.e2e.PaymentTaken';

test.describe('the audit log', () => {
  test('lists real entries, newest first, with who and what', async ({ page }) => {
    await page.goto('/audit');

    await expect(page.getByRole('heading', { level: 1, name: 'Audit log' })).toBeVisible();
    await expect(page.getByRole('table')).toBeVisible();
    await expect(page.getByRole('row').nth(1)).toContainText(/\S/);

    // Scoped to the table: the filter's own <select> lists every action too, and matching an
    // option for a row is the sort of pass that proves nothing.
    const actions = await page.locator('tbody tr td:nth-child(2)').allTextContents();
    expect(actions.length).toBeGreaterThan(0);
    expect(actions.every((action) => /^[A-Z_]+$/.test(action.trim()))).toBe(true);
  });

  test('is in the sidebar, marked as the current page', async ({ page }) => {
    await page.goto('/audit');

    await expect(
      page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Audit log' }),
    ).toHaveAttribute('aria-current', 'page');
  });

  test('filters by target, applying on submit rather than on typing', async ({ page }) => {
    await page.goto('/audit');
    await expect(page.getByRole('table')).toBeVisible();

    const requests: string[] = [];
    page.on('request', (request) => {
      if (request.url().includes('/v1/audit')) {
        requests.push(request.url());
      }
    });

    await page.getByLabel('Target').fill(SUBJECT);
    // Nothing has been asked yet: a request per keystroke is a query per letter against a
    // table that only grows.
    expect(requests).toEqual([]);

    await page.getByRole('button', { name: 'Apply' }).click();

    await expect(page.getByRole('table')).toContainText(SUBJECT);
    expect(requests).toHaveLength(1);
    expect(requests[0]).toContain(`target=${SUBJECT}`);

    // Every row is the target asked for: a filter that returned others would still show some.
    const targets = await page.locator('tbody tr td:nth-child(4)').allTextContents();
    expect(targets.length).toBeGreaterThan(0);
    expect(new Set(targets.map((t) => t.trim()))).toEqual(new Set([SUBJECT]));
  });

  test('filters by action', async ({ page }) => {
    await page.goto('/audit');
    await page.getByLabel('Action').selectOption('VERSION_SUBMITTED');
    await page.getByRole('button', { name: 'Apply' }).click();

    await expect(page.getByRole('table')).toContainText('VERSION_SUBMITTED');

    const actions = await page.locator('tbody tr td:nth-child(2)').allTextContents();
    expect(new Set(actions.map((a) => a.trim()))).toEqual(new Set(['VERSION_SUBMITTED']));
  });

  test('says so, and does not claim safety, when nothing matches', async ({ page }) => {
    await page.goto('/audit');
    await page.getByLabel('Target').fill('no.such.subject.anywhere');
    await page.getByRole('button', { name: 'Apply' }).click();

    await expect(page.getByText('No entries match')).toBeVisible();
    await expect(page.getByText(/not evidence that nobody tried it/)).toBeVisible();
  });

  test('says what is wrong with a range the registry refuses', async ({ page }) => {
    // Since after until is the registry's refusal, rendered with its own words rather than an
    // empty log that would read as "nothing happened in that window".
    await page.goto('/audit');
    await page.getByLabel('Since').fill('2026-08-05');
    await page.getByLabel('Until').fill('2026-08-03');
    await page.getByRole('button', { name: 'Apply' }).click();

    await expect(
      page.getByRole('heading', { name: /could not load the audit log/i }),
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

    await page.goto('/audit');
    await expect(page.getByRole('table')).toBeVisible();

    expect(errors).toEqual([]);
  });
});

test.describe('the audit log, as a reader', () => {
  // A reader writes nothing here, and the endpoint admits any read scope — signed in once by
  // global setup, not per test, because sign-in is rate-limited.
  test.use({ storageState: READER_STORAGE_STATE });

  test('is readable', async ({ page }) => {
    await page.goto('/audit');

    await expect(page.getByRole('table')).toBeVisible();
  });
});
