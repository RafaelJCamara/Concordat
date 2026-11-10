import { expect, type Page } from '@playwright/test';
import { TOUR_SEEN_KEY } from '../src/app/core/config/tour';
import type { OWNER, READER } from './registry';

/**
 * Signs in through the real form, as a person would.
 *
 * <b>Through the UI rather than by planting a cookie.</b> A test that set the session
 * directly would skip the one thing several of these specs exist to prove: that a real
 * sign-in produces a scope set the real directive and the real guard read the same way.
 * That gap is where `DISMISSED` hid for a whole milestone.
 *
 * Waits on the actor's own email appearing in the sidebar rather than on a navigation. The
 * form both signs in and, on an unclaimed instance, creates the first account — so the
 * button's label differs and the destination can too, but the session footer naming who you
 * are is true in both cases and is the thing later steps actually depend on.
 */
export async function signIn(
  page: Page,
  who: typeof OWNER | typeof READER,
  options: { readonly tour?: 'seen' | 'fresh' } = {},
): Promise<void> {
  await page.goto('/sign-in');
  // The guided tour starts by itself the first time a browser is signed in (ADR-051), and its
  // card would sit over the page of every spec that signs in in a context with no saved state.
  // So the browser is told, before it signs in, that it has seen the tour. A spec about the tour
  // itself asks for `'fresh'` and gets the browser a first-time visitor has.
  if (options.tour !== 'fresh') {
    await markTourSeen(page);
  }
  await page.getByRole('textbox', { name: 'Email' }).fill(who.email);
  await page.getByRole('textbox', { name: 'Password' }).fill(who.password);
  await page.getByRole('button', { name: /sign in|create owner account/i }).click();
  // The sidebar's account control, not any text on the page: a subject owned by this account
  // shows the same email in a table cell, and a bare match would be ambiguous.
  await expect(page.locator('cd-app-sidebar').getByText(who.email)).toBeVisible();
}

/**
 * Writes the guided tour's "seen" flag into the page's origin, as a browser that has already
 * been shown it would have. The key comes from the app's own source, so the helper cannot
 * drift from what the app reads.
 */
export async function markTourSeen(page: Page): Promise<void> {
  await page.evaluate((key) => localStorage.setItem(key, '1'), TOUR_SEEN_KEY);
}
