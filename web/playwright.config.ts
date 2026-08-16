import { defineConfig, devices } from '@playwright/test';
import { OWNER_STORAGE_STATE } from './e2e/global-setup';

/**
 * Browser end-to-end tests (M4.5, and the open half of decision 26).
 *
 * <b>These exist because unit tests cover each half in isolation and nothing drove the two
 * together.</b> `if-scope.spec.ts` proves the directive hides an affordance, `scope-guard.spec.ts`
 * proves the guard redirects, and the API's `AuthorizationTests` prove the server refuses — and
 * every one of them passed while `DISMISSED` broke the subject list outright, because no test
 * anywhere loaded a real page against a real registry.
 *
 * The registry and the dev server are NOT started here. Both need Docker and a database, and a
 * config that silently started a second copy of either would produce a suite that passes against
 * the wrong thing. `e2e/README.md` says what to run; `globalSetup` fails with that instruction
 * rather than a connection error nobody can act on.
 */
const webBaseUrl = process.env.CONCORDAT_WEB_URL ?? 'http://localhost:4300';

export default defineConfig({
  testDir: './e2e',
  globalSetup: './e2e/global-setup.ts',

  // No retries locally. A flaky E2E that passes on retry is a flaky E2E nobody investigates,
  // and this suite is small enough that a genuine failure is worth stopping for. CI retries
  // once, because a container that lost a race there is not usually a product defect.
  retries: process.env.CI ? 1 : 0,
  fullyParallel: false,

  expect: {
    /*
     * 15s rather than the 5s default, because of what this suite runs against.
     *
     * `ng serve` compiles a lazy route's chunk the first time a browser asks for it, so the
     * first navigation to each screen pays a build that no later test sees. The write route
     * rendered in 614ms warm and took over five seconds cold, and failed on precisely that —
     * the page was correct and fully rendered in the failure snapshot.
     *
     * Raising the timeout is the right fix where weakening the assertion would be wrong: the
     * product is not slow, the dev toolchain is, and every screen added from here would
     * otherwise need the same per-test workaround discovered the same way.
     */
    timeout: 15_000,
  },

  // Serial, deliberately. Every test signs in against one shared registry, and claiming an
  // instance is a one-way operation — parallel workers would race over a single global state.
  workers: 1,

  reporter: process.env.CI ? [['github'], ['list']] : [['list']],

  use: {
    baseURL: webBaseUrl,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'off',
    // Most of this suite is about reading, and reading now requires a caller (H1) — so most
    // tests start already signed in as OWNER, the same way a returning visitor's browser
    // would. `global-setup.ts` produces this by signing in through the real form once. A test
    // that is instead about being signed OUT (`authorization.spec.ts`'s anonymous cases) calls
    // `page.context().clearCookies()` for itself rather than this file carrying an exception.
    storageState: OWNER_STORAGE_STATE,
  },

  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
});
