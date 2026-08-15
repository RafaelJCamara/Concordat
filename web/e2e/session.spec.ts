import { expect, test } from '@playwright/test';
import { OWNER } from './registry';

/**
 * Signing in, staying signed in, and signing out (decision 26).
 *
 * The reload case is the one that could not be tested any other way. `session-api.spec.ts`
 * proves the client sends `withCredentials`; `SessionCookieTests` proves the API sets an
 * httpOnly cookie and trades it back. Neither can prove that a *browser* keeps the cookie across
 * a real navigation and that the app asks for it before the first screen renders — which is the
 * entire feature.
 */
test.describe('a session', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/sign-in');
    await page.getByRole('textbox', { name: 'Email' }).fill(OWNER.email);
    await page.getByRole('textbox', { name: 'Password' }).fill(OWNER.password);
    await page.getByRole('button', { name: /sign in|create owner account/i }).click();

    await expect(page.getByText(OWNER.email)).toBeVisible();
  });

  test('survives a full page reload', async ({ page }) => {
    // The credential lives in memory only -- localStorage is readable by any script on the page
    // and ADR-006 declined that trade -- so after this navigation the app has nothing left
    // except an httpOnly cookie it cannot read, and one route that will trade it back.
    await page.goto('/subjects');

    await expect(page.getByText(OWNER.email)).toBeVisible();
    await expect(page.getByRole('button', { name: 'Sign out' })).toBeVisible();
  });

  test('resumes before the first screen renders, not after a flash of signed-out', async ({
    page,
  }) => {
    // provideAppInitializer runs /auth/resume before the first render. If it ran after, a
    // signed-in user would see the sign-in affordance flash on every page load -- the sort of
    // defect that is obvious in person and invisible to every unit test.
    await page.goto('/subjects');

    await expect(page.getByRole('link', { name: 'Sign in' })).toHaveCount(0);
  });

  test('ends on sign out, and the reload does not bring it back', async ({ page }) => {
    await page.getByRole('button', { name: 'Sign out' }).click();
    await expect(page).toHaveURL(/\/sign-in/);

    // Script cannot delete an httpOnly cookie, so this only passes if the API actually cleared
    // it. A client-side-only sign-out would look identical until the next reload.
    await page.goto('/subjects');

    await expect(page.getByRole('link', { name: 'Sign in' })).toBeVisible();
    await expect(page.getByText(OWNER.email)).toHaveCount(0);
  });

  test('the session cookie is not readable by script', async ({ page }) => {
    // The property the whole design rests on. If this ever fails, the credential is one XSS away
    // from being stolen and the localStorage answer ADR-006 refused would be no worse.
    const visible = await page.evaluate(() => document.cookie);

    expect(visible).not.toContain('concordat_session');
  });
});
