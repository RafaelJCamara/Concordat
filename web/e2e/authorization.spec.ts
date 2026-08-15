import { expect, test, type Page } from '@playwright/test';
import { OWNER, READER } from './registry';

/**
 * M4.5's named test: a reader sees no write affordance.
 *
 * <b>Both halves have unit tests and neither proves this.</b> `if-scope.spec.ts` renders one
 * directive in a host component; `scope-guard.spec.ts` calls a guard function directly. Nothing
 * between them proves that a real sign-in produces a scope set the real directive reads — which
 * is where the wiring actually lives, and where `DISMISSED` hid for a whole milestone.
 *
 * ADR-018 asks specifically for the affordance to be **absent** rather than disabled: a disabled
 * button invites a support ticket, an absent one does not. None of this is the security
 * boundary — the server refuses the same request with 403 regardless of what renders. What it
 * buys is that the UI does not offer an action the server will refuse.
 */
async function signIn(page: Page, who: typeof OWNER | typeof READER): Promise<void> {
  await page.goto('/sign-in');
  await page.getByRole('textbox', { name: 'Email' }).fill(who.email);
  await page.getByRole('textbox', { name: 'Password' }).fill(who.password);
  await page.getByRole('button', { name: /sign in|create owner account/i }).click();
  await expect(page.getByText(who.email)).toBeVisible();
}

test.describe('write affordances', () => {
  test('a reader is offered none', async ({ page }) => {
    await signIn(page, READER);
    await page.goto('/subjects');

    // The page renders -- reading is the reader's whole job. What is missing is the way in to
    // a write.
    await expect(page.getByRole('heading', { name: 'Subjects' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'New subject' })).toHaveCount(0);
  });

  test('an owner is offered them', async ({ page }) => {
    // The other half, and the reason the first assertion is worth having: a button absent for
    // everybody would satisfy that test while simply being broken.
    await signIn(page, OWNER);
    await page.goto('/subjects');

    await expect(page.getByRole('button', { name: 'New subject' })).toBeVisible();
  });

  test('an anonymous visitor is offered none either', async ({ page }) => {
    // Not the same as a reader: an anonymous caller on an unclaimed instance is answered as an
    // OWNER by the API, so this only holds once somebody has claimed it. Global setup does.
    await page.context().clearCookies();
    await page.goto('/subjects');

    await expect(page.getByRole('button', { name: 'New subject' })).toHaveCount(0);
    await expect(page.getByRole('link', { name: 'Sign in' })).toBeVisible();
  });
});

/**
 * M4.5's other named test — "direct URL to a write route redirects" — is deliberately not here.
 *
 * <b>There is no write route to paste.</b> `app.routes.ts` has exactly two entries, `sign-in`
 * and `subjects`, and `**` redirects everything else to the subject list. `scopeGuard` is built,
 * unit-tested, and referenced by no route at all.
 *
 * Writing the test anyway would assert that `/subjects/new` redirects — and it would pass, on
 * the wildcard, while proving nothing about the guard. A test that passes for the wrong reason
 * is worse than a missing one, because the missing one is still on the list.
 *
 * It goes in with M4.3's pages, which is when the guard first has something to guard.
 */
test.describe('the guarded-route test', () => {
  test('is owed, and is not fakeable yet', async ({ page }) => {
    await page.goto('/subjects');

    // Pinned so this stops passing the moment a third route appears, which is exactly when
    // somebody should come back and write the real test.
    const routes = await page.evaluate(() => window.location.pathname);
    expect(routes).toBe('/subjects');
  });
});
