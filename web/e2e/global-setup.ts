import { chromium } from '@playwright/test';
import {
  ensureEnvironment,
  ensureHeldVersion,
  ensureOwner,
  ensureReader,
  ensureSubject,
  OWNER,
} from './registry';

const REGISTRY = process.env.CONCORDAT_REGISTRY ?? 'http://localhost:5062';
const WEB = process.env.CONCORDAT_WEB_URL ?? 'http://localhost:4300';

/** Where the signed-in-as-OWNER browser state lands, for playwright.config.ts to load by default. */
export const OWNER_STORAGE_STATE = 'e2e/.auth/owner.json';

/**
 * Checks both halves are up, then arranges the accounts every test needs.
 *
 * <b>The reachability check is separate from the arrangement, and says which one is missing.</b>
 * "connect ECONNREFUSED 127.0.0.1:4300" in the middle of a sign-in test is a message that sends
 * somebody looking at the sign-in code.
 */
export default async function globalSetup(): Promise<void> {
  await reachable(WEB, 'the web app', 'npm start -- --port 4300');
  await reachable(
    `${REGISTRY}/health/ready`,
    'the registry',
    'cd deploy/compose && CONCORDAT_IMAGE=concordat/api:local docker compose --profile registry up -d',
  );

  const owner = await ensureOwner();
  await ensureReader(owner);

  // The app's default environment. Without it the subject list renders a 404 and every
  // assertion about the page shape is really an assertion about an error state.
  await ensureEnvironment(owner, 'dev');

  // One real subject, so the list has a row. See ensureSubject for why an empty table would
  // make every assertion here vacuous.
  await ensureSubject(owner, 'dev', 'acme.e2e.OrderCreated');

  // And a second whose tip is held at the approval gate, so the screens that render a status
  // have a real `AWAITING_APPROVAL` to render rather than only the happy one. See
  // ensureHeldVersion: this is the shape of the defect that shipped once already.
  await ensureHeldVersion(owner, 'dev', 'acme.e2e.PaymentTaken');

  await captureOwnerSession();
}

/**
 * Signs in as OWNER through the real form once, and saves the resulting browser state so every
 * test starts already signed in.
 *
 * <b>Most of this suite is about reading, and reading now requires a caller (H1).</b> Signing in
 * inside every read-only test would work, but it would also mean every one of them is partly a
 * test of the sign-in form — and a broken form would then fail fifteen unrelated tests with
 * fifteen different-looking failures instead of one. `authorization.spec.ts` and `session.spec.ts`
 * still sign in for real where the sign-in itself, or a *different* caller, is what is under
 * test; those explicitly override this default per test or per file.
 *
 * A real page, not `request.newContext()`: sign-in here is a bearer credential kept in memory
 * plus an httpOnly cookie for silent resume (decision 26), not just a cookie a request context
 * could capture on its own, and the point is to reproduce exactly what a signed-in tab already
 * looks like to the app.
 */
async function captureOwnerSession(): Promise<void> {
  const browser = await chromium.launch();
  const page = await browser.newPage();

  try {
    await page.goto(`${WEB}/sign-in`);
    await page.getByRole('textbox', { name: 'Email' }).fill(OWNER.email);
    await page.getByRole('textbox', { name: 'Password' }).fill(OWNER.password);
    await page.getByRole('button', { name: /sign in|create owner account/i }).click();
    await page.getByText(OWNER.email).waitFor();

    await page.context().storageState({ path: OWNER_STORAGE_STATE });
  } finally {
    await browser.close();
  }
}

async function reachable(url: string, what: string, howToStart: string): Promise<void> {
  try {
    const response = await fetch(url);

    if (!response.ok) {
      throw new Error(`${response.status}`);
    }
  } catch (cause) {
    throw new Error(
      `Cannot reach ${what} at ${url}. Start it with:\n\n  ${howToStart}\n\n` +
        'See web/e2e/README.md.',
      { cause },
    );
  }
}
