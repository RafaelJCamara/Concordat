import { expect, test } from '@playwright/test';

/**
 * The subject list, rendering what a real registry actually sent.
 *
 * <b>This suite exists because of the bug it would have caught.</b> `VersionStatus.Dismissed`
 * shipped with M7 and `wire-tokens.ts` never learned it. The web app's unknown-token guard is
 * strict by design — a token it does not recognise is a newer server, and guessing would be
 * worse — so one dismissed version in an environment failed the entire subject list with
 * *"the registry sent 'DISMISSED' for 'status', which this build does not recognise"*.
 *
 * Every unit test on both sides passed. 1,489 .NET tests, 187 Angular tests, and none of them
 * loaded a page against a running registry. The first browser to do so found it in ten seconds.
 */
test.describe('the subject list', () => {
  test('renders rows from the registry rather than an error', async ({ page }) => {
    await page.goto('/subjects');

    await expect(page.getByRole('heading', { name: 'Subjects' })).toBeVisible();

    // The guard renders this alert for any token it cannot parse. Asserting its ABSENCE is the
    // whole point: a vocabulary that drifts fails here and nowhere else.
    await expect(page.getByRole('heading', { name: /could not load subjects/i })).toHaveCount(0);
    await expect(page.getByRole('table')).toBeVisible();
  });

  test('reports no console errors while doing it', async ({ page }) => {
    // A page that renders while logging a failed request is a page that is quietly degraded --
    // /auth/resume 404ing against a stale API looked exactly like this, and looked fine.
    const errors: string[] = [];
    page.on('console', (message) => {
      if (message.type() === 'error') {
        errors.push(message.text());
      }
    });
    page.on('pageerror', (error) => errors.push(error.message));

    await page.goto('/subjects');
    await expect(page.getByRole('heading', { name: 'Subjects' })).toBeVisible();

    expect(errors).toEqual([]);
  });

  test('names the environment it is reading', async ({ page }) => {
    await page.goto('/subjects');

    // Cheap, and it catches a whole class of "which registry am I even looking at" confusion
    // that costs real time during an incident.
    await expect(page.getByText('dev').first()).toBeVisible();
  });
});
