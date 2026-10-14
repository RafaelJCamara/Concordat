import { expect, test, type Locator, type Page, type Request } from '@playwright/test';
import { READER_STORAGE_STATE } from './global-setup';
import {
  createContractRaw,
  ensureEmptySubject,
  ensureEnvironment,
  ensureHeldVersion,
  ownerCredential,
} from './registry';
import {
  addPublishBindingRaw,
  readSummary,
  reportViolationRaw,
  tier,
  type SummaryView,
} from './summary';

/**
 * The dashboard, against the real registry (ADR-037).
 *
 * <b>Every number on it comes from `GET /v1/environments/{env}/summary`, and nothing derives one in
 * the browser.</b> The first test is that claim: opening the page makes one request for the summary
 * and none for the subject list, which is what the page used to read and count. The rest are what the
 * numbers and panels say about what was arranged.
 *
 * <b>Nothing here asserts an absolute count of `dev`</b>, which every other spec writes to and which
 * grows on every run. The arranged environment is `e2e-dashboard`, and each assertion is made against
 * what the registry itself reports for it (read through the API, so the browser and the registry are
 * compared) or against a difference this spec caused (read before and after it arranged anything).
 *
 * <b>What a run leaves behind, permanently:</b> two subjects, one contract with two publish bindings
 * and a consume binding, and one reported violation, all in `e2e-dashboard`, all named for the run.
 * There is no delete and the audit trail is append-only. **No other spec may write to
 * `e2e-dashboard`**, which is what lets "the newest subject" and "the newest breaking change" be
 * asserted by name; it is created once by this file.
 */
const RUN = Date.now();
const ENVIRONMENT = 'e2e-dashboard';
const EMPTY_ENVIRONMENT = 'e2e-empty';

const HELD = `acme.e2e.dashboard.held.${RUN}`;
const BARE = `acme.e2e.dashboard.bare.${RUN}`;
const CONTRACT = `e2e-dashboard-${RUN}`;
const EXCHANGE = `dashboard-${RUN}`;

test.describe.configure({ mode: 'serial' });

/** Points the app at an environment by answering `config.json`, which a deployment supplies. */
async function aimAt(page: Page, environment: string): Promise<void> {
  await page.route('**/config.json', (route) =>
    route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify({ defaultEnvironment: environment }),
    }),
  );
}

/** The big number on the stat card with this title. */
function stat(page: Page, title: string): Locator {
  return page
    .locator('cd-stat-card')
    .filter({ has: page.getByText(title, { exact: true }) })
    .locator('p.text-3xl');
}

const breakingPanel = (page: Page): Locator => page.locator('cd-breaking-changes-panel');
const enforcementPanel = (page: Page): Locator => page.locator('cd-enforcement-panel');

let before: SummaryView;
let after: SummaryView;

test.beforeAll(async () => {
  const credential = await ownerCredential();
  await ensureEnvironment(credential, ENVIRONMENT);
  await ensureEnvironment(credential, EMPTY_ENVIRONMENT);

  before = await readSummary(credential, ENVIRONMENT);

  // A subject with a version held at the gate (a newly required field breaks the default policy),
  // then one with nothing registered, created last so it is the newest.
  await ensureHeldVersion(credential, ENVIRONMENT, HELD);
  await ensureEmptySubject(credential, ENVIRONMENT, BARE, 'json');

  await createContractRaw(credential, ENVIRONMENT, CONTRACT, 'ENFORCE');
  const subjects = [{ subject: HELD, selector: 'latest' }];
  await addPublishBindingRaw(
    credential,
    ENVIRONMENT,
    CONTRACT,
    EXCHANGE,
    'orders.created',
    subjects,
  );
  await addPublishBindingRaw(
    credential,
    ENVIRONMENT,
    CONTRACT,
    `${EXCHANGE}-b`,
    'orders.shipped',
    subjects,
  );

  await reportViolationRaw(credential, ENVIRONMENT, `${EXCHANGE}/orders.created`);

  after = await readSummary(credential, ENVIRONMENT);
});

test.describe('what the arrangement did, as the registry counts it', () => {
  // Not about the browser: it is what makes every assertion below mean something. If the arranging
  // helpers ever stop doing what they say, this is the test that goes red, and not forty lines of
  // screen assertions that all say a number is wrong.
  test('moved exactly the numbers the page is about to be asked for', () => {
    expect(after.subjects.total - before.subjects.total).toBe(2);
    expect(after.versions.total - before.versions.total).toBe(2);
    expect(after.versions.awaitingApproval - before.versions.awaitingApproval).toBe(1);
    expect(tier(after, 'ENFORCE').contracts - tier(before, 'ENFORCE').contracts).toBe(1);
    expect(tier(after, 'ENFORCE').publishBindings - tier(before, 'ENFORCE').publishBindings).toBe(
      2,
    );
    expect(after.violations.distinct - before.violations.distinct).toBe(1);
    expect(after.newestSubjects.map((s) => s.name).slice(0, 2)).toEqual([BARE, HELD]);
    expect(after.breakingChanges[0]).toMatchObject({ subject: HELD, status: 'AWAITING_APPROVAL' });
  });
});

test.describe('the page', () => {
  test.beforeEach(async ({ page }) => {
    await aimAt(page, ENVIRONMENT);
  });

  test('asks the registry for its summary, and never for the subject list', async ({ page }) => {
    // The old dashboard fetched every subject with every version and counted them here. A request
    // for the list on this page means a number is being derived again, and the day that list is
    // paginated it is wrong without anything going red.
    const requests: string[] = [];
    page.on('request', (request: Request) => {
      if (request.url().includes('/v1/')) {
        requests.push(new URL(request.url()).pathname);
      }
    });

    await page.goto('/');
    await expect(stat(page, 'Subjects')).toHaveText(String(after.subjects.total));

    expect(
      requests.filter((path) => path === `/v1/environments/${ENVIRONMENT}/summary`),
    ).toHaveLength(1);
    expect(requests.filter((path) => path === `/v1/environments/${ENVIRONMENT}/subjects`)).toEqual(
      [],
    );
  });

  test('shows the registry’s counts under their titles', async ({ page }) => {
    await page.goto('/');

    await expect(stat(page, 'Subjects')).toHaveText(String(after.subjects.total));
    await expect(stat(page, 'Versions')).toHaveText(String(after.versions.total));
    await expect(stat(page, 'Awaiting approval')).toHaveText(
      String(after.versions.awaitingApproval),
    );
    await expect(stat(page, 'Violations')).toHaveText(String(after.violations.distinct));

    // Said in the registry's words, and no stronger.
    await expect(page.getByText(/seen in the last 24 hours/)).toBeVisible();
  });

  test('lists the held version first among the breaking changes, and it opens', async ({
    page,
  }) => {
    await page.goto('/');

    const first = breakingPanel(page).getByRole('listitem').first();
    await expect(first).toContainText(HELD);
    await expect(first).toContainText('Awaiting approval');
    await expect(first).toContainText('Proposed by');

    await first.getByRole('link').click();
    await expect(page).toHaveURL(
      new RegExp(`/subjects/${HELD.replaceAll('.', '\\.')}/versions/2$`),
    );
  });

  test('puts the newest subject first, including one with nothing registered', async ({ page }) => {
    await page.goto('/');

    const cards = page.locator('cd-subject-card');
    await expect(cards.first()).toContainText(BARE);
    await expect(cards.first()).toContainText('—');
    await expect(cards.first()).toContainText('0 versions');
    await expect(cards.nth(1)).toContainText(HELD);
    await expect(cards.nth(1)).toContainText('1 awaiting approval');

    await cards.first().click();
    await expect(page).toHaveURL(new RegExp(`/subjects/${BARE.replaceAll('.', '\\.')}$`));
  });

  test('counts enforcement coverage per mode, and says it is declared, not observed', async ({
    page,
  }) => {
    await page.goto('/');

    const enforce = enforcementPanel(page).getByRole('listitem').filter({ hasText: 'ENFORCE' });
    const row = tier(after, 'ENFORCE');

    await expect(enforce).toContainText(`${row.contracts} contract`);
    await expect(enforce).toContainText(`${row.publishBindings} publish binding`);
    await expect(enforce).toContainText(`${row.consumeBindings} consume binding`);

    // One row per mode, zeros included.
    await expect(enforcementPanel(page).getByRole('listitem')).toHaveCount(3);
    await expect(enforcementPanel(page)).toContainText('Declared, not observed');
  });

  test('shows no console error, and no request fails', async ({ page }) => {
    const errors: string[] = [];
    page.on('console', (message) => {
      if (message.type() === 'error') {
        errors.push(message.text());
      }
    });
    page.on('response', (response) => {
      if (response.url().includes('/v1/') && response.status() >= 400) {
        errors.push(`${response.status()} ${response.url()}`);
      }
    });

    await page.goto('/');
    await expect(stat(page, 'Subjects')).toHaveText(String(after.subjects.total));

    expect(errors).toEqual([]);
  });
});

test.describe('on a phone, and in the dark', () => {
  test.beforeEach(async ({ page }) => {
    await aimAt(page, ENVIRONMENT);
  });

  test('fits 390px without scrolling sideways, panels stacked', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto('/');
    await expect(breakingPanel(page)).toContainText(HELD);

    const overflow = await page.evaluate(
      () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
    );
    expect(overflow).toBeLessThanOrEqual(0);

    // Stacked, not side by side: the second panel starts below the first one ends.
    const top = await breakingPanel(page).boundingBox();
    const bottom = await enforcementPanel(page).boundingBox();
    expect(bottom!.y).toBeGreaterThanOrEqual(top!.y + top!.height - 1);
  });

  test('draws in the dark theme without a light panel', async ({ page }) => {
    await page.addInitScript(() => localStorage.setItem('concordat.appearance', 'dark'));
    await page.goto('/');
    await expect(breakingPanel(page)).toContainText(HELD);

    await expect(page.locator('html')).toHaveClass(/\bdark\b/);

    // A panel painted with a hard-coded light surface is the failure a dark theme hides until
    // someone looks: its background must be the theme's card colour, which is dark.
    const lightness = await breakingPanel(page)
      .locator('section')
      .evaluate((element) => {
        const [r, g, b] = getComputedStyle(element)
          .backgroundColor.match(/\d+(\.\d+)?/g)!
          .map(Number);
        return (0.2126 * r! + 0.7152 * g! + 0.0722 * b!) / 255;
      });
    expect(lightness).toBeLessThan(0.3);
  });
});

test.describe('an empty environment', () => {
  // `e2e-empty` is created once by `new-subject.spec.ts` and nothing may create a subject, a
  // contract or a violation in it: every empty state here depends on that.
  test.beforeEach(async ({ page }) => {
    await aimAt(page, EMPTY_ENVIRONMENT);
  });

  test('says each panel is empty in words, and offers the first subject', async ({ page }) => {
    await page.goto('/');

    await expect(page.getByText('No subjects yet')).toBeVisible();
    await expect(breakingPanel(page)).toContainText('Nothing held or rejected');
    await expect(enforcementPanel(page)).toContainText('No contracts in this environment');
    await expect(stat(page, 'Subjects')).toHaveText('0');
    await expect(stat(page, 'Awaiting approval')).toHaveText('0');
    await expect(page.getByText('Nothing waiting')).toBeVisible();
    await expect(page.getByText(/None reported in the last 24 hours/)).toBeVisible();

    await page.getByRole('link', { name: 'Create the first subject' }).click();
    await expect(page).toHaveURL(/\/subjects\/new-subject$/);
  });

  test.describe('as a reader', () => {
    test.use({ storageState: READER_STORAGE_STATE });

    test('is told it is empty, with no way to create anything', async ({ page }) => {
      await page.goto('/');

      await expect(page.getByText('No subjects yet')).toBeVisible();
      await expect(page.getByRole('link', { name: 'Create the first subject' })).toHaveCount(0);
      await expect(breakingPanel(page)).toContainText('Nothing held or rejected');
    });
  });
});

test.describe('the default environment', () => {
  test('agrees with what the registry says about it, whatever that is', async ({ page }) => {
    // `dev` is written to by every spec, so no number is asserted outright: the page is compared to
    // the registry's own answer for the same environment.
    const credential = await ownerCredential();
    const dev = await readSummary(credential, 'dev');

    await page.goto('/');

    await expect(stat(page, 'Subjects')).toHaveText(String(dev.subjects.total));
    await expect(stat(page, 'Versions')).toHaveText(String(dev.versions.total));
    await expect(stat(page, 'Awaiting approval')).toHaveText(String(dev.versions.awaitingApproval));

    // The shared fixtures hold two versions at the gate and nothing here decides them, so the
    // panel is never empty, and at most five rows are drawn however many are held.
    const rows = breakingPanel(page).getByRole('listitem');
    await expect(rows.first()).toBeVisible();
    expect(await rows.count()).toBeLessThanOrEqual(5);
    expect(await page.locator('cd-subject-card').count()).toBeLessThanOrEqual(6);
  });
});
