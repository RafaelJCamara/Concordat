import { expect, test, type Page, type Request } from '@playwright/test';
import { OWNER_STORAGE_STATE } from './global-setup';
import { ensureEnvironment, ownerCredential } from './registry';

/**
 * The subject list's pages, the address that holds them, and the order they are in, against the real
 * registry (ADR-052, ADR-049).
 *
 * The unit tests stub the store and the .NET tests never load a page, and the ways this screen can be wrong are
 * the ones between them: a page that is right in the store and wrong in the address, an out-of-range page that
 * is a blank table, a Back button that lands on a page the screen does not show, a "newest first" that is a
 * sentence and not an order. So these ask the registry what the order is, and compare the screen to it.
 *
 * <b>It reads an environment of its own, `e2e-paging`, which holds 130 subjects and is never written to by
 * another spec.</b> 130 because that is three pages at the default size of 50 and six at 25, with a last page
 * that is not full (30 and 5), which is what the page arithmetic needs; and an environment of its own so the
 * count is a fact and not whatever `dev` has grown to. The subjects have fixed names (`e2e.paging.s001` to
 * `s130`) and are made once, in order, so a re-run finds them and creates nothing. `README.md` counts them. If a
 * first run is interrupted the subjects are made in the same order next time, and no assertion here depends
 * on which subject was made when: the order is read back from the registry.
 */
const ENVIRONMENT = 'e2e-paging';
const REGISTRY = process.env.CONCORDAT_REGISTRY ?? 'http://localhost:5062';
const COUNT = 130;
const name = (n: number) => `e2e.paging.s${String(n).padStart(3, '0')}`;

/** What the registry says the order is, newest first, for a prefix: the standard the screen is held to. */
async function newestFirst(prefix = ''): Promise<{ name: string; createdAt: string }[]> {
  const response = await fetch(
    `${REGISTRY}/v1/environments/${ENVIRONMENT}/subjects?sort=newest&limit=500&prefix=${encodeURIComponent(prefix)}`,
    { headers: { authorization: `Bearer ${await ownerCredential()}` } },
  );

  expect(response.ok).toBe(true);

  return (await response.json()) as { name: string; createdAt: string }[];
}

async function arrange(): Promise<void> {
  const credential = await ownerCredential();

  await ensureEnvironment(credential, ENVIRONMENT);

  const existing = new Set((await newestFirst()).map((s) => s.name));

  // In order and one at a time: the registry stamps each with its own clock, and the order the screen shows
  // is that, so concurrent creates would tie on a clock tick and fall back to the name.
  for (let n = 1; n <= COUNT; n++) {
    if (existing.has(name(n))) {
      continue;
    }

    const response = await fetch(`${REGISTRY}/v1/environments/${ENVIRONMENT}/subjects`, {
      method: 'POST',
      headers: { authorization: `Bearer ${credential}`, 'content-type': 'application/json' },
      body: JSON.stringify({ name: name(n), format: 'json', owner: 'e2e' }),
    });

    if (!response.ok && response.status !== 409) {
      throw new Error(`Creating ${name(n)} failed with ${response.status}.`);
    }
  }
}

const rowNames = (page: Page) => page.locator('tbody tr td:first-child');
const extent = (page: Page) => page.getByTestId('list-extent');
const pager = (page: Page) => page.getByRole('navigation', { name: 'Subject pages' });
const pageSize = (page: Page) => page.getByTestId('page-size');

/** The names of the rows on screen, in order. */
async function shown(page: Page): Promise<string[]> {
  return (await rowNames(page).allInnerTexts()).map((n) => n.trim());
}

/** The subject-list requests the page makes, as `limit`/`offset`/`sort`/`prefix`, in order. */
function listRequests(
  page: Page,
): { limit: string; offset: string; sort: string; prefix: string }[] {
  const seen: { limit: string; offset: string; sort: string; prefix: string }[] = [];

  page.on('request', (request: Request) => {
    const url = new URL(request.url());

    if (
      request.method() === 'GET' &&
      new RegExp(`/v1/environments/${ENVIRONMENT}/subjects$`).test(url.pathname)
    ) {
      seen.push({
        limit: url.searchParams.get('limit') ?? '',
        offset: url.searchParams.get('offset') ?? '',
        sort: url.searchParams.get('sort') ?? '',
        prefix: url.searchParams.get('prefix') ?? '',
      });
    }
  });

  return seen;
}

test.describe('the pages of the subject list', () => {
  test.beforeAll(async () => {
    test.setTimeout(120_000);
    await arrange();
  });

  test.beforeEach(async ({ page }) => {
    // The one request stubbed: the deployment says which environment this is, as `new-subject.spec.ts` does for
    // its empty one. Everything else is the real registry.
    await page.route('**/config.json', (route) =>
      route.fulfill({
        contentType: 'application/json',
        body: JSON.stringify({ defaultEnvironment: ENVIRONMENT }),
      }),
    );
  });

  test('opens on the newest subjects, with no action, in the order the registry gives', async ({
    page,
  }) => {
    const asked = listRequests(page);
    const expected = await newestFirst();

    await page.goto('/subjects');
    await expect(rowNames(page)).toHaveCount(50);

    // Newest first, from the first byte: the registry is asked for the order, a page and a size, once.
    expect(asked).toEqual([{ limit: '50', offset: '0', sort: 'newest', prefix: '' }]);
    expect(await shown(page)).toEqual(expected.slice(0, 50).map((s) => s.name));
    await expect(extent(page)).toHaveText(`Page 1 of 3 · subjects 1–50 of ${COUNT} · newest first`);
    // The plain list has the plain address.
    expect(new URL(page.url()).search).toBe('');
  });

  test('shows a creation date that makes the order visible, not only asserted', async ({
    page,
  }) => {
    await page.goto('/subjects');
    await expect(rowNames(page)).toHaveCount(50);

    const created = await page
      .locator('tbody td[data-testid="created"] time')
      .evaluateAll((times) => times.map((t) => t.getAttribute('datetime') ?? ''));

    expect(created).toHaveLength(50);
    const stamps = created.map((c) => Date.parse(c));
    expect(stamps.every((t) => Number.isFinite(t))).toBe(true);
    // Descending, which is what "newest first" means, read off the dates in the rows.
    expect(stamps).toEqual([...stamps].sort((a, b) => b - a));

    await expect(page.getByRole('columnheader', { name: 'Created' })).toHaveAttribute(
      'aria-sort',
      'descending',
    );
    await expect(page.locator('caption')).toContainText('Newest first');
    // The old column, which was the latest version's date and looked like the subject's age, is gone.
    await expect(page.getByRole('columnheader', { name: 'Registered' })).toHaveCount(0);
  });

  test('turns the page with Next, keeps the focus there, and puts the page in the address', async ({
    page,
  }) => {
    const expected = await newestFirst();
    await page.goto('/subjects');
    await expect(rowNames(page)).toHaveCount(50);

    const next = pager(page).getByRole('button', { name: 'Next' });
    await next.focus();
    await page.keyboard.press('Enter');

    await expect(extent(page)).toHaveText(
      `Page 2 of 3 · subjects 51–100 of ${COUNT} · newest first`,
    );
    expect(await shown(page)).toEqual(expected.slice(50, 100).map((s) => s.name));
    expect(new URL(page.url()).searchParams.get('page')).toBe('2');
    // Not moved by the page change: the same button, still focused, so the next press turns the next page.
    await expect(next).toBeFocused();
    await expect(pager(page).getByRole('button', { name: 'Page 2' })).toHaveAttribute(
      'aria-current',
      'page',
    );

    await page.keyboard.press('Enter');
    await expect(extent(page)).toContainText('Page 3 of 3 · subjects 101–130');
    expect(await shown(page)).toEqual(expected.slice(100, 130).map((s) => s.name));
    // The last page: Next is still there and still focused, and says it cannot go on.
    await expect(next).toBeFocused();
    await expect(next).toHaveAttribute('aria-disabled', 'true');
  });

  test('puts no row on two pages and none on no page', async ({ page }) => {
    const expected = (await newestFirst()).map((s) => s.name);
    const seen: string[] = [];

    await page.goto('/subjects');

    for (let p = 1; p <= 3; p++) {
      await expect(extent(page)).toContainText(`Page ${p} of 3`);
      seen.push(...(await shown(page)));

      if (p < 3) {
        await pager(page).getByRole('button', { name: 'Next' }).click();
      }
    }

    expect(seen).toHaveLength(COUNT);
    expect(new Set(seen).size).toBe(COUNT);
    expect(seen).toEqual(expected);
  });

  test('jumps to a numbered page, and the first and last are always there', async ({ page }) => {
    await page.goto('/subjects?size=25');
    await expect(extent(page)).toContainText('Page 1 of 6');

    await pager(page).getByRole('button', { name: 'Page 6' }).click();

    await expect(extent(page)).toContainText(`Page 6 of 6 · subjects 126–${COUNT} of ${COUNT}`);
    await expect(rowNames(page)).toHaveCount(5);
    // Both parameters, in whichever order the router wrote them: the order is not part of the address's meaning.
    expect(Object.fromEntries(new URL(page.url()).searchParams)).toEqual({ page: '6', size: '25' });
  });

  test('Back and Forward restore the pages', async ({ page }) => {
    const expected = (await newestFirst()).map((s) => s.name);
    await page.goto('/subjects');
    await expect(rowNames(page)).toHaveCount(50);

    await pager(page).getByRole('button', { name: 'Next' }).click();
    await expect(extent(page)).toContainText('Page 2 of 3');
    await pager(page).getByRole('button', { name: 'Next' }).click();
    await expect(extent(page)).toContainText('Page 3 of 3');

    await page.goBack();
    await expect(extent(page)).toContainText('Page 2 of 3');
    expect(await shown(page)).toEqual(expected.slice(50, 100));

    await page.goBack();
    await expect(extent(page)).toContainText('Page 1 of 3');
    expect(await shown(page)).toEqual(expected.slice(0, 50));
    expect(new URL(page.url()).search).toBe('');

    await page.goForward();
    await expect(extent(page)).toContainText('Page 2 of 3');
  });

  test('lands on the same page after a reload, and from a link someone sent', async ({
    page,
    browser,
  }) => {
    const expected = (await newestFirst()).map((s) => s.name);
    await page.goto('/subjects?page=3');
    await expect(extent(page)).toContainText('Page 3 of 3');

    await page.reload();
    await expect(extent(page)).toContainText('Page 3 of 3 · subjects 101–130');
    expect(await shown(page)).toEqual(expected.slice(100, 130));

    // A second browser context, signed in as the same account (the stored session), given the same address.
    const other = await browser.newContext({ storageState: OWNER_STORAGE_STATE });
    const there = await other.newPage();
    await there.route('**/config.json', (route) =>
      route.fulfill({
        contentType: 'application/json',
        body: JSON.stringify({ defaultEnvironment: ENVIRONMENT }),
      }),
    );
    await there.goto(page.url());
    await expect(extent(there)).toContainText('Page 3 of 3 · subjects 101–130');
    expect(await shown(there)).toEqual(expected.slice(100, 130));
    await other.close();
  });

  test('puts a page past the end on the last page, says so, and does not leave the bad address behind', async ({
    page,
  }) => {
    const expected = (await newestFirst()).map((s) => s.name);
    await page.goto('/subjects');
    await expect(rowNames(page)).toHaveCount(50);

    await page.goto('/subjects?page=99');

    await expect(page.getByTestId('page-clamped')).toHaveText(
      'Page 99 does not exist, so this is page 3, the last.',
    );
    await expect(extent(page)).toContainText('Page 3 of 3');
    expect(await shown(page)).toEqual(expected.slice(100, 130));
    expect(new URL(page.url()).searchParams.get('page')).toBe('3');
    // Never a blank table or an empty-state sentence on the way.
    await expect(page.getByText('No subjects yet')).toHaveCount(0);

    // Back leaves the page that named 99 for the one before it, rather than into it again.
    await page.goBack();
    await expect(page).toHaveURL(/\/subjects$/);
  });

  test('rewrites an address that is not one the screen would write, and shows the first page', async ({
    page,
  }) => {
    await page.goto('/subjects?page=abc&size=7&q=%20');

    await expect(extent(page)).toContainText('Page 1 of 3');
    await expect(rowNames(page)).toHaveCount(50);
    expect(new URL(page.url()).search).toBe('');
  });

  test('changes the size, keeps the row being looked at, and says so in the address', async ({
    page,
  }) => {
    const expected = (await newestFirst()).map((s) => s.name);
    await page.goto('/subjects?page=3&size=25');
    // Page 3 of 25 begins at row 51.
    await expect(extent(page)).toContainText('subjects 51–75');

    await pageSize(page).selectOption('50');

    // Row 51 is the first row of page 2 at 50 to a page.
    await expect(extent(page)).toHaveText(
      `Page 2 of 3 · subjects 51–100 of ${COUNT} · newest first`,
    );
    expect(await shown(page)).toEqual(expected.slice(50, 100));
    expect(new URL(page.url()).search).toBe('?page=2');

    await pageSize(page).selectOption('100');
    await expect(extent(page)).toContainText('Page 1 of 2 · subjects 1–100');
    expect(new URL(page.url()).search).toBe('?size=100');
  });

  test('searches by prefix across the pages, and a new prefix starts again at the first page', async ({
    page,
  }) => {
    const asked = listRequests(page);
    const expected = (await newestFirst('e2e.paging.s0')).map((s) => s.name);
    expect(expected).toHaveLength(99);

    await page.goto('/subjects?page=3');
    await expect(extent(page)).toContainText('Page 3 of 3');

    await page.getByRole('searchbox', { name: /Search subjects/ }).fill('e2e.paging.s0');

    // 99 match: two pages, and the page was reset, not carried over to a page that may not exist.
    await expect(extent(page)).toHaveText(
      'Page 1 of 2 · subjects 1–50 of 99 starting with e2e.paging.s0 · newest first',
    );
    expect(await shown(page)).toEqual(expected.slice(0, 50));
    expect(new URL(page.url()).search).toBe('?q=e2e.paging.s0');

    await pager(page).getByRole('button', { name: 'Next' }).click();
    await expect(extent(page)).toContainText('Page 2 of 2 · subjects 51–99 of 99');
    expect(await shown(page)).toEqual(expected.slice(50, 99));
    expect(new URL(page.url()).search).toBe('?q=e2e.paging.s0&page=2');

    // Every request carried the prefix once there was one, and the page's offset.
    const withPrefix = asked.filter((r) => r.prefix === 'e2e.paging.s0');
    expect(withPrefix.map((r) => r.offset)).toEqual(['0', '50']);

    // Reloading the search lands on the same page of the same search.
    await page.reload();
    await expect(extent(page)).toContainText('Page 2 of 2 · subjects 51–99 of 99');
    await expect(page.getByRole('searchbox', { name: /Search subjects/ })).toHaveValue(
      'e2e.paging.s0',
    );
  });

  test('Back leaves a search for the list it was typed over', async ({ page }) => {
    await page.goto('/subjects');
    await expect(rowNames(page)).toHaveCount(50);

    await page.getByRole('searchbox', { name: /Search subjects/ }).fill('e2e.paging.s00');
    await expect(extent(page)).toContainText('of 9 starting with e2e.paging.s00');

    // A search replaces the entry rather than adding one, so Back leaves the list altogether and does not
    // walk back through every prefix that was typed on the way.
    await page.goBack();
    await expect(page).not.toHaveURL(/\/subjects/);
  });

  test('keeps the last answer: a slow page does not land over the one clicked after it', async ({
    page,
  }) => {
    const expected = (await newestFirst()).map((s) => s.name);
    await page.goto('/subjects?size=25');
    await expect(rowNames(page)).toHaveCount(25);

    // The answer to page 2 is held back until page 3 has been shown, as a slow request is.
    let release: () => void = () => {};
    const held = new Promise<void>((resolve) => (release = resolve));
    await page.route(
      (url) => url.searchParams.get('offset') === '25',
      async (route) => {
        await held;
        await route.continue().catch(() => undefined);
      },
    );

    await pager(page).getByRole('button', { name: 'Page 2' }).click();
    await pager(page).getByRole('button', { name: 'Page 3' }).click();

    await expect(extent(page)).toContainText('Page 3 of 6 · subjects 51–75');
    expect(await shown(page)).toEqual(expected.slice(50, 75));

    release();
    await page.waitForTimeout(500);

    await expect(extent(page)).toContainText('Page 3 of 6 · subjects 51–75');
    expect(await shown(page)).toEqual(expected.slice(50, 75));
  });

  test('dims the rows while the next page is on its way, and does not blank them', async ({
    page,
  }) => {
    await page.goto('/subjects');
    await expect(rowNames(page)).toHaveCount(50);

    let release: () => void = () => {};
    const held = new Promise<void>((resolve) => (release = resolve));
    await page.route(
      (url) => url.searchParams.get('offset') === '50',
      async (route) => {
        await held;
        await route.continue();
      },
    );

    await pager(page).getByRole('button', { name: 'Next' }).click();

    await expect(page.locator('[aria-busy="true"]')).toBeVisible();
    await expect(rowNames(page)).toHaveCount(50);
    // The sentence still names the rows that are there.
    await expect(extent(page)).toContainText('Page 1 of 3');

    release();
    await expect(extent(page)).toContainText('Page 2 of 3');
    await expect(page.locator('[aria-busy="true"]')).toHaveCount(0);
  });

  test('is operated from the keyboard alone', async ({ page }) => {
    await page.goto('/subjects?size=25');
    await expect(rowNames(page)).toHaveCount(25);

    // Tab to a numbered page by name and press it.
    const four = pager(page).getByRole('button', { name: 'Page 4' });
    await four.focus();
    await page.keyboard.press('Space');

    await expect(extent(page)).toContainText('Page 4 of 6');
    await expect(four).toBeFocused();

    // The size is a select, and changes with the arrow keys.
    await pageSize(page).focus();
    await page.keyboard.press('ArrowDown');
    await expect(extent(page)).toContainText(/Page \d of 3/);
  });

  test('does not scroll sideways on a phone, with the pager in view', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto('/subjects?page=2&size=25');
    await expect(rowNames(page)).toHaveCount(25);
    await pager(page).scrollIntoViewIfNeeded();

    await expect(pager(page)).toBeVisible();
    const overflow = await page.evaluate(
      () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
    );
    expect(overflow).toBeLessThanOrEqual(0);

    // And the pager itself fits its row or wraps: nothing in it is wider than the screen.
    const box = await pager(page).boundingBox();
    expect(box!.x).toBeGreaterThanOrEqual(0);
    expect(box!.x + box!.width).toBeLessThanOrEqual(390);
  });

  test('raises nothing in the console, paging or not', async ({ page }) => {
    const problems: string[] = [];

    page.on('console', (message) => {
      if (message.type() === 'error' || message.type() === 'warning') {
        problems.push(message.text());
      }
    });
    page.on('pageerror', (error) => problems.push(error.message));

    await page.goto('/subjects');
    await expect(rowNames(page)).toHaveCount(50);
    await pager(page).getByRole('button', { name: 'Next' }).click();
    await expect(extent(page)).toContainText('Page 2 of 3');
    await page.goto('/subjects?page=99');
    await expect(page.getByTestId('page-clamped')).toBeVisible();

    expect(problems).toEqual([]);
  });
});
