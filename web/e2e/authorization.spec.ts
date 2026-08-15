import { expect, test } from '@playwright/test';
import { OWNER, READER } from './registry';
import { signIn } from './sign-in';

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
 * M4.5's other named test: a direct URL to a write route redirects.
 *
 * <b>Owed since M4.2 and unwritable until now.</b> `scopeGuard` was built, unit-tested and
 * referenced by no route at all, so there was nothing to paste: asserting that `/subjects/new`
 * redirected would have passed on the `**` wildcard and proved nothing about the guard. A test
 * that passes for the wrong reason is worse than a missing one, because the missing one is
 * still on the list.
 *
 * `/subjects/:name/versions/new` is the app's first write route and the first thing the guard
 * is attached to, so the test is finally about the guard rather than about the wildcard.
 */
// The subject `global-setup.ts` guarantees, so the screen behind the guard has something
// real to register against rather than rendering an error the assertions then trip over.
const WRITE_ROUTE = '/subjects/acme.e2e.OrderCreated/versions/new';

test.describe('a direct URL to a write route', () => {
  test('renders for someone who may write', async ({ page }) => {
    // First, because it is what makes the two redirect assertions meaningful: a route that
    // redirected for everybody would satisfy them while simply being broken.
    await signIn(page, OWNER);
    await page.goto(WRITE_ROUTE);

    await expect(page.getByRole('heading', { name: 'Register a version' })).toBeVisible();
    expect(await page.evaluate(() => window.location.pathname)).toBe(WRITE_ROUTE);
  });

  test('redirects a reader to the read surface', async ({ page }) => {
    // Not to a 403 page. Somebody who followed a link from an incident channel to a route
    // they cannot use is better served by the read surface they can use than by a dead end.
    await signIn(page, READER);
    await page.goto(WRITE_ROUTE);

    await expect(page.getByRole('heading', { name: 'Subjects' })).toBeVisible();
    expect(await page.evaluate(() => window.location.pathname)).toBe('/subjects');
  });

  test('sends a signed-out visitor to sign in, with somewhere to come back to', async ({
    page,
  }) => {
    // The difference between a locked door and a locked door with a key slot. They may well
    // be entitled to this route; they simply have not said who they are yet.
    await page.context().clearCookies();
    await page.goto(WRITE_ROUTE);

    await expect(page).toHaveURL(
      new RegExp(`/sign-in\\?returnTo=${encodeURIComponent(WRITE_ROUTE)}`),
    );
  });
});
