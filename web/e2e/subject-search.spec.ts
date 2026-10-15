import { expect, test, type Page, type Request } from '@playwright/test';
import { ensureEnvironment, ensureSubject, ownerCredential } from './registry';

/**
 * The subject search box, against the real registry (ADR-038).
 *
 * The box is the registry's prefix filter, and what makes it worth a browser test is that every
 * way it can be wrong is invisible to the layers on either side: the unit tests stub the store, the
 * .NET tests never load a page, and a box that matched anywhere in a name, or ignored case, or
 * treated an underscore as a wildcard, would look right in both. So these ask the registry, and
 * assert on what came back across the wire as well as on the screen.
 *
 * <b>It makes five subjects per run, in `dev`, and they are permanent</b>: there is no delete and
 * a retired name stays taken. Every one is named for the run under one prefix
 * (`acme.e2e.prefix.<timestamp>.`), so a search for that prefix finds exactly this run's
 * subjects whatever earlier runs left behind, and nothing here touches a name another spec reads.
 * `README.md` counts them.
 */
const RUN = Date.now();
const ENVIRONMENT = 'dev';
const PREFIX = `acme.e2e.prefix.${RUN}.`;

const ORDER_CREATED = `${PREFIX}orders.OrderCreated`;
const ORDER_CREATED_LOWER = `${PREFIX}orders.ordercreated`;
const INVOICE_RAISED = `${PREFIX}billing.InvoiceRaised`;
const WILD_UNDERSCORE = `${PREFIX}wild.a_b`;
const WILD_LETTER = `${PREFIX}wild.axb`;

const ALL = [ORDER_CREATED, ORDER_CREATED_LOWER, INVOICE_RAISED, WILD_UNDERSCORE, WILD_LETTER];

const searchBox = (page: Page) => page.getByRole('searchbox', { name: /Search subjects/ });
const rowNames = (page: Page) => page.locator('tbody tr td:first-child');

/** The requests that carried a `prefix`, in order: what the registry was actually asked. */
function prefixRequests(page: Page): string[] {
  const seen: string[] = [];

  page.on('request', (request: Request) => {
    const url = new URL(request.url());

    if (request.method() === 'GET' && /\/v1\/environments\/[^/]+\/subjects$/.test(url.pathname)) {
      const prefix = url.searchParams.get('prefix');

      if (prefix !== null) {
        seen.push(prefix);
      }
    }
  });

  return seen;
}

test.describe('searching subjects by the start of their name', () => {
  test.beforeAll(async () => {
    const credential = await ownerCredential();

    await ensureEnvironment(credential, ENVIRONMENT);

    for (const name of ALL) {
      await ensureSubject(credential, ENVIRONMENT, name);
    }
  });

  test.beforeEach(async ({ page }) => {
    await page.goto('/subjects');
    await expect(searchBox(page)).toBeVisible();
  });

  test('finds this run’s subjects by their shared prefix, and only those', async ({ page }) => {
    const asked = prefixRequests(page);

    await searchBox(page).fill(PREFIX);

    await expect(rowNames(page)).toHaveCount(ALL.length);
    for (const name of ALL) {
      await expect(page.getByRole('link', { name, exact: true })).toBeVisible();
    }

    // The registry was asked, with the prefix as typed: nothing was filtered in the browser.
    expect(asked).toEqual([PREFIX]);
    await expect(page.getByText(`${ALL.length} subjects starting with`)).toBeVisible();
  });

  test('narrows as the prefix grows, one request per pause in typing', async ({ page }) => {
    const asked = prefixRequests(page);
    const box = searchBox(page);

    // Typed a key at a time with no pause long enough to ask: the registry hears about the end.
    await box.pressSequentially(`${PREFIX}orders.`, { delay: 15 });

    await expect(rowNames(page)).toHaveCount(2);
    expect(asked).toEqual([`${PREFIX}orders.`]);

    await box.pressSequentially('Order', { delay: 15 });
    await expect(rowNames(page)).toHaveCount(1);
    await expect(page.getByRole('link', { name: ORDER_CREATED, exact: true })).toBeVisible();
    expect(asked).toEqual([`${PREFIX}orders.`, `${PREFIX}orders.Order`]);
  });

  test('is case-sensitive, because subject names are', async ({ page }) => {
    const box = searchBox(page);

    await box.fill(`${PREFIX}orders.Order`);
    await expect(rowNames(page)).toHaveCount(1);
    await expect(page.getByRole('link', { name: ORDER_CREATED, exact: true })).toBeVisible();

    await box.fill(`${PREFIX}orders.order`);
    await expect(rowNames(page)).toHaveCount(1);
    await expect(page.getByRole('link', { name: ORDER_CREATED_LOWER, exact: true })).toBeVisible();

    // Not found by any casing of the namespace either.
    await box.fill(PREFIX.toUpperCase());
    await expect(page.getByText('No subject names start with')).toBeVisible();
  });

  test('treats an underscore as an underscore and not as any one character', async ({ page }) => {
    await searchBox(page).fill(`${PREFIX}wild.a_`);

    await expect(rowNames(page)).toHaveCount(1);
    await expect(page.getByRole('link', { name: WILD_UNDERSCORE, exact: true })).toBeVisible();
    await expect(page.getByRole('link', { name: WILD_LETTER, exact: true })).toHaveCount(0);
  });

  test('matches the start of a name, not the middle and not the owner', async ({ page }) => {
    // Both were true of the box before the registry took it over. OrderCreated is in the middle
    // of ORDER_CREATED, and every subject made here is owned by `e2e`.
    await searchBox(page).fill('OrderCreated');
    await expect(page.getByText('No subject names start with')).toBeVisible();

    await searchBox(page).fill('e2e');
    await expect(page.getByText('No subject names start with')).toBeVisible();
    await expect(page.getByText('“e2e”')).toBeVisible();
  });

  test('says when nothing starts with it, keeps the box, and clears in one step', async ({
    page,
  }) => {
    const box = searchBox(page);

    await box.fill(`${PREFIX}nothing.`);

    await expect(page.getByText('No subject names start with')).toBeVisible();
    await expect(page.getByText(`“${PREFIX}nothing.”`)).toBeVisible();
    await expect(page.getByText('Names are case-sensitive')).toBeVisible();
    // Not the empty-environment state: that one offers to create the first subject.
    await expect(page.getByText('No subjects yet')).toHaveCount(0);
    await expect(box).toBeVisible();

    await page.getByRole('button', { name: 'Clear search' }).click();

    await expect(box).toHaveValue('');
    await expect(rowNames(page).first()).toBeVisible();
    await expect(page.getByText('No subject names start with')).toHaveCount(0);
  });

  test('says why a prefix that no name could start with finds nothing', async ({ page }) => {
    await searchBox(page).fill(`${PREFIX}wild-`);

    await expect(page.getByText('No subject names start with')).toBeVisible();
    await expect(page.getByText("'-' (U+002D) is not allowed in a subject name")).toBeVisible();
  });

  test('sends a wildcard or an escape as text, and finds nothing for it', async ({ page }) => {
    const asked = prefixRequests(page);

    // Characters that mean something to a LIKE pattern or a glob. The registry answers an empty
    // list to each; what is asserted here is that the browser put them on the wire as a value.
    for (const hostile of [`${PREFIX}%`, '%', `${PREFIX}wild.a\\_`, `${PREFIX}*`]) {
      await searchBox(page).fill(hostile);
      // The panel names the prefix the registry answered, so waiting for this one's name is
      // waiting for this one's answer and not reading the last one's.
      await expect(page.getByText(`“${hostile}”`)).toBeVisible();
      await expect(rowNames(page)).toHaveCount(0);
    }

    expect(asked).toEqual([`${PREFIX}%`, '%', `${PREFIX}wild.a\\_`, `${PREFIX}*`]);
  });

  test('keeps the box, and its focus, while an answer is awaited', async ({ page }) => {
    // Hold the registry's answer to the prefix so the in-between state can be looked at.
    let release: () => void = () => {};
    const held = new Promise<void>((resolve) => (release = resolve));

    await page.route(
      (url) => url.searchParams.get('prefix') === `${PREFIX}orders.`,
      async (route) => {
        await held;
        await route.continue();
      },
    );

    const box = searchBox(page);
    await box.focus();
    await box.pressSequentially(`${PREFIX}orders.`, { delay: 10 });

    // The rows that were there are still there, dimmed, and the box is the same focused box.
    await expect(page.locator('[aria-busy="true"]')).toBeVisible();
    await expect(box).toBeFocused();
    await expect(rowNames(page).first()).toBeVisible();

    release();
    await expect(rowNames(page)).toHaveCount(2);
    await expect(page.locator('[aria-busy="true"]')).toHaveCount(0);
    await expect(box).toBeFocused();
  });

  test('shows the answer to the prefix in the box, not a slower answer to an earlier one', async ({
    page,
  }) => {
    // The first prefix's answer is held back until after the second's has been shown, which is
    // what a slow search does to a fast typist. If the screen showed the stale one it would list
    // the wrong subjects under a box that says something else.
    let release: () => void = () => {};
    const held = new Promise<void>((resolve) => (release = resolve));
    const slow = `${PREFIX}billing.`;
    const fast = `${PREFIX}orders.`;

    await page.route(
      (url) => url.searchParams.get('prefix') === slow,
      async (route) => {
        await held;
        await route.continue().catch(() => undefined);
      },
    );

    const box = searchBox(page);
    await box.fill(slow);
    await page.waitForRequest((request) => request.url().includes('prefix='));
    await box.fill(fast);

    await expect(rowNames(page)).toHaveCount(2);
    await expect(page.getByText(`2 subjects starting with`)).toBeVisible();

    release();
    // Give a late answer the chance to land if it were going to.
    await page.waitForTimeout(500);

    await expect(rowNames(page)).toHaveCount(2);
    await expect(page.getByRole('link', { name: INVOICE_RAISED, exact: true })).toHaveCount(0);
    await expect(box).toHaveValue(fast);
  });

  test('says how many more there are, and reads them on request, in name order (ADR-045)', async ({
    page,
  }) => {
    // The screen asks for fifty, and this run has five. Cutting the page to two on the way out makes
    // the registry's real total and real offset do the work, which a stubbed answer would not test.
    const limits: string[] = [];
    const offsets: string[] = [];

    await page.route(
      (url) => url.searchParams.get('prefix') === PREFIX && /\/subjects$/.test(url.pathname),
      async (route) => {
        const url = new URL(route.request().url());
        limits.push(url.searchParams.get('limit') ?? '');
        offsets.push(url.searchParams.get('offset') ?? '');
        url.searchParams.set('limit', '2');
        await route.continue({ url: url.toString() });
      },
    );

    await searchBox(page).fill(PREFIX);

    await expect(rowNames(page)).toHaveCount(2);
    const extent = page.getByTestId('list-extent');
    await expect(extent).toContainText(`Showing the first 2 of ${ALL.length}`);
    await expect(extent).toContainText(`${ALL.length - 2} more are not shown yet`);

    await page.getByRole('button', { name: 'Show more' }).click();
    await expect(rowNames(page)).toHaveCount(4);
    await expect(extent).toContainText(`Showing the first 4 of ${ALL.length}`);
    await expect(extent).toContainText('1 more is not shown yet');

    await page.getByRole('button', { name: 'Show more' }).click();
    await expect(rowNames(page)).toHaveCount(ALL.length);
    // Everything is on screen, so the screen stops saying it is not.
    await expect(extent).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Show more' })).toHaveCount(0);

    // The names are the registry's order, each once, with no row repeated across the pages.
    const names = await rowNames(page).allInnerTexts();
    expect(names.map((n) => n.trim())).toEqual([...ALL].sort((a, b) => (a < b ? -1 : 1)));

    // The page asked for fifty and continued from what it held.
    expect(new Set(limits)).toEqual(new Set(['50']));
    expect(offsets).toEqual(['', '2', '4']);
  });

  test('does not scroll sideways on a phone', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto('/subjects');

    await searchBox(page).fill(PREFIX);
    await expect(rowNames(page)).toHaveCount(ALL.length);

    const overflow = await page.evaluate(
      () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
    );
    expect(overflow).toBeLessThanOrEqual(0);
  });

  test('raises nothing in the console, searching or not', async ({ page }) => {
    const problems: string[] = [];

    page.on('console', (message) => {
      if (message.type() === 'error' || message.type() === 'warning') {
        problems.push(message.text());
      }
    });
    page.on('pageerror', (error) => problems.push(error.message));

    await searchBox(page).fill(`${PREFIX}orders.`);
    await expect(rowNames(page)).toHaveCount(2);
    await searchBox(page).fill('nothing.');
    await expect(page.getByText('No subject names start with')).toBeVisible();
    await searchBox(page).fill('');
    await expect(rowNames(page).first()).toBeVisible();

    expect(problems).toEqual([]);
  });
});
