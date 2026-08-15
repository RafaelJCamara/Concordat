import { expect, type Page } from '@playwright/test';
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
export async function signIn(page: Page, who: typeof OWNER | typeof READER): Promise<void> {
  await page.goto('/sign-in');
  await page.getByRole('textbox', { name: 'Email' }).fill(who.email);
  await page.getByRole('textbox', { name: 'Password' }).fill(who.password);
  await page.getByRole('button', { name: /sign in|create owner account/i }).click();
  await expect(page.getByText(who.email)).toBeVisible();
}
