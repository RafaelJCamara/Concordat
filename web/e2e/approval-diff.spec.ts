import { expect, test, type Locator, type Page } from '@playwright/test';
import { ownerCredential } from './registry';

/**
 * The approval review's detailed view: the held change as a line diff, with the registry's own
 * findings on the lines they are about (ADR-050).
 *
 * <b>The documents are really diffed, and the findings are really the registry's.</b> Each
 * fixture below is a subject whose proposal the compatibility engine held, in all three formats,
 * so a line is tied to a finding by a path that was not written for this test — `#/properties/id`
 * for JSON Schema, `#/fields/customer/fields/age` for Avro, `.acme.Order/1` for Protobuf. A
 * unit test that built a result by hand would agree with the page by construction.
 *
 * <b>Nothing here decides.</b> Approving or rejecting is permanent, so the fixtures are fixed
 * names created once and found again on every later run (look first, as `ensureHeldVersion`
 * does), and no test touches the shared `PaymentTaken` or `LedgerPosted` beyond reading them.
 */
const REGISTRY = process.env.CONCORDAT_REGISTRY ?? 'http://localhost:5062';
const SHARED_HELD = 'acme.e2e.PaymentTaken';
const VIEW_KEY = 'concordat.approvals.review-view';

const FIXTURES = {
  avro: 'acme.e2e.diff.avro',
  proto: 'acme.e2e.diff.proto',
  long: 'acme.e2e.diff.long',
  /** More changed lines than the view will show: the fallback. */
  huge: 'acme.e2e.diff.huge',
  /** A schema near the registry's ceiling with a handful of changes: the guard must not trip. */
  dense: 'acme.e2e.diff.dense',
} as const;

const AVRO = (idType: string, ageType: string, extra = ''): string =>
  `{"type":"record","name":"Order","namespace":"acme","fields":[{"name":"id","type":"${idType}"},` +
  `{"name":"customer","type":{"type":"record","name":"Cust","fields":[{"name":"age","type":"${ageType}"}]}}${extra}]}`;

const PROTO = (idType: string, qtyType: string): string =>
  `syntax = "proto3";\npackage acme;\nmessage Order {\n  ${idType} id = 1;\n  ${qtyType} qty = 2;\n}\n`;

/** `count` properties named `p0`…, each a string, with the type of the listed ones changed. */
const WIDE = (count: number, changed: (index: number) => boolean): string => {
  const properties: Record<string, { type: string }> = {};

  for (let i = 0; i < count; i++) {
    properties[`p${i}`] = { type: changed(i) ? 'integer' : 'string' };
  }

  return JSON.stringify({ type: 'object', properties });
};

async function api(path: string, credential: string, init?: RequestInit): Promise<Response> {
  return fetch(`${REGISTRY}${path}`, {
    ...init,
    headers: {
      'content-type': 'application/json',
      authorization: `Bearer ${credential}`,
      ...(init?.headers ?? {}),
    },
  });
}

/**
 * Makes a subject with a version held at the approval gate, unless one already is.
 *
 * Looks before it writes: re-posting the base while a proposal is pending is a revert, which the
 * registry dismisses, so "tolerate a 409" would grow the subject on every run.
 */
async function ensureHeld(
  credential: string,
  name: string,
  format: 'json' | 'avro' | 'protobuf',
  base: string,
  proposal: string,
): Promise<void> {
  const created = await api('/v1/environments/dev/subjects', credential, {
    method: 'POST',
    body: JSON.stringify({ name, format, owner: 'e2e' }),
  });

  if (!created.ok && created.status !== 409) {
    throw new Error(`Creating ${name} failed with ${created.status}.`);
  }

  const read = await api(`/v1/environments/dev/subjects/${name}`, credential);
  const { versions } = (await read.json()) as { versions: { status: string }[] };

  if (versions.some((v) => v.status === 'AWAITING_APPROVAL')) {
    return;
  }

  for (const schema of versions.length === 0 ? [base, proposal] : [proposal]) {
    const registered = await api(`/v1/environments/dev/subjects/${name}/versions`, credential, {
      method: 'POST',
      body: JSON.stringify({ schema, registeredBy: 'e2e' }),
    });

    if (!registered.ok) {
      throw new Error(`Registering a version of ${name} failed with ${registered.status}.`);
    }
  }
}

test.beforeAll(async () => {
  test.setTimeout(120_000);
  const credential = await ownerCredential();

  await ensureHeld(
    credential,
    FIXTURES.avro,
    'avro',
    AVRO('string', 'int'),
    AVRO('long', 'string', ',{"name":"total","type":"double"}'),
  );
  await ensureHeld(
    credential,
    FIXTURES.proto,
    'protobuf',
    PROTO('string', 'int32'),
    PROTO('int64', 'string'),
  );
  // Sixty properties, one changed deep in the middle: the unchanged ones are what folds.
  await ensureHeld(
    credential,
    FIXTURES.long,
    'json',
    WIDE(60, () => false),
    WIDE(60, (i) => i === 30),
  );
  // 520 properties each change type: two changed lines apiece, past the view's thousand.
  await ensureHeld(
    credential,
    FIXTURES.huge,
    'json',
    WIDE(520, () => false),
    WIDE(520, () => true),
  );
  // The registry takes 512 KiB; this is about 450 KiB and some fifty thousand lines laid out.
  await ensureHeld(
    credential,
    FIXTURES.dense,
    'json',
    WIDE(17_000, () => false),
    WIDE(17_000, (i) => i === 5 || i === 8_500 || i === 16_995),
  );
});

function card(page: Page, subject: string): Locator {
  return page
    .getByRole('listitem')
    .filter({ has: page.getByRole('link', { name: subject, exact: true }) });
}

/** Pages in until the subject's row is on screen, as `approvals.spec.ts` does (ADR-042). */
async function reveal(page: Page, subject: string): Promise<Locator> {
  const more = page.getByRole('button', { name: 'Show more' });

  while ((await card(page, subject).count()) === 0 && (await more.count()) > 0) {
    await more.click();
    await expect(page.getByRole('button', { name: 'Loading…' })).toHaveCount(0);
  }

  return card(page, subject);
}

/** Opens the queue, and the review of one subject. */
async function openReview(page: Page, subject: string): Promise<Locator> {
  await page.goto('/approvals');
  const row = await reveal(page, subject);
  await row.getByRole('button', { name: /review changes and impact/i }).click();

  return row;
}

/** The row of the diff whose text is exactly this, with indentation ignored. */
function diffRow(row: Locator, text: string): Locator {
  return row.locator('[data-row]').filter({
    has: row
      .page()
      .locator('code', { hasText: new RegExp(`^(Added: |Removed: )?\\s*${escape(text)}$`) }),
  });
}

function escape(text: string): string {
  return text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

/** The finding box that sits directly under a diff row. */
function findingUnder(rowLocator: Locator): Locator {
  return rowLocator.locator('xpath=following-sibling::*[1][@data-testid="diff-finding"]');
}

test.describe('the choice between detailed and summary', () => {
  test('starts on the summary, which is what the screen showed before, and fetches no schema', async ({
    page,
  }) => {
    const schemaRequests: string[] = [];
    page.on('request', (request) => {
      if (new URL(request.url()).pathname.startsWith('/v1/schemas/')) {
        schemaRequests.push(request.url());
      }
    });

    const row = await openReview(page, SHARED_HELD);

    await expect(row.getByRole('radio', { name: 'Summary' })).toBeChecked();
    await expect(row.getByText('required_field_added')).toBeVisible();
    await expect(row.getByTestId('diff-region')).toHaveCount(0);
    expect(schemaRequests).toEqual([]);
  });

  test('is a radio group the keyboard can operate', async ({ page }) => {
    const row = await openReview(page, SHARED_HELD);
    const group = row.getByRole('radiogroup', { name: 'Show the changes as' });

    await expect(group.getByRole('radio')).toHaveCount(2);

    await group.getByRole('radio', { name: 'Summary' }).focus();
    await page.keyboard.press('ArrowLeft');

    await expect(group.getByRole('radio', { name: 'Detailed' })).toBeChecked();
    await expect(row.getByTestId('diff-region')).toBeVisible();

    await page.keyboard.press('ArrowRight');

    await expect(group.getByRole('radio', { name: 'Summary' })).toBeChecked();
    await expect(row.getByTestId('diff-region')).toHaveCount(0);
  });

  test('remembers the choice for the next visit, in either direction', async ({ page }) => {
    let row = await openReview(page, SHARED_HELD);
    await row.getByRole('radio', { name: 'Detailed' }).check();
    await expect(row.getByTestId('diff-region')).toBeVisible();

    expect(await page.evaluate((key) => localStorage.getItem(key), VIEW_KEY)).toBe('detailed');

    // A new visit: the page is loaded again and the review opened again.
    row = await openReview(page, SHARED_HELD);
    await expect(row.getByRole('radio', { name: 'Detailed' })).toBeChecked();
    await expect(row.getByTestId('diff-region')).toBeVisible();

    await row.getByRole('radio', { name: 'Summary' }).check();
    row = await openReview(page, SHARED_HELD);

    await expect(row.getByRole('radio', { name: 'Summary' })).toBeChecked();
    await expect(row.getByTestId('diff-region')).toHaveCount(0);
  });

  test('works with browser storage blocked, and keeps the choice for the session', async ({
    page,
  }) => {
    await page.addInitScript(() => {
      for (const method of ['getItem', 'setItem', 'removeItem'] as const) {
        Storage.prototype[method] = function () {
          throw new DOMException('The operation is insecure.', 'SecurityError');
        };
      }
    });
    const errors: string[] = [];
    page.on('pageerror', (error) => errors.push(error.message));

    // With storage refused nothing can remember the tour was seen, so it starts, as ADR-051 says it
    // does once per page load. Skip it the way a person would, then do what the test is about.
    await page.goto('/approvals');
    await page.getByRole('button', { name: 'Skip tour' }).click();
    const row = await reveal(page, SHARED_HELD);
    await row.getByRole('button', { name: /review changes and impact/i }).click();
    await expect(row.getByRole('radio', { name: 'Summary' })).toBeChecked();

    await row.getByRole('radio', { name: 'Detailed' }).check();

    await expect(row.getByTestId('diff-region')).toBeVisible();
    expect(errors).toEqual([]);
  });

  test('moves every open review together, because it is one preference', async ({ page }) => {
    await page.goto('/approvals');
    const first = await reveal(page, SHARED_HELD);
    const second = await reveal(page, 'acme.e2e.LedgerPosted');
    await first.getByRole('button', { name: /review changes and impact/i }).click();
    await second.getByRole('button', { name: /review changes and impact/i }).click();

    await first.getByRole('radio', { name: 'Detailed' }).check();

    await expect(second.getByRole('radio', { name: 'Detailed' })).toBeChecked();
    await expect(second.getByTestId('diff-region')).toBeVisible();
  });
});

test.describe('the detailed view', () => {
  test.beforeEach(async ({ page }) => {
    await page.addInitScript((key) => localStorage.setItem(key, 'detailed'), VIEW_KEY);
  });

  test('marks added and removed lines with a sign and with words, and puts the finding under its line', async ({
    page,
  }) => {
    const row = await openReview(page, SHARED_HELD);
    await expect(row.getByTestId('diff-region')).toBeVisible();

    // PaymentTaken adds `total` and requires it. The registry reports `#/required`.
    const added = diffRow(row, '"required": [');

    await expect(added).toHaveAttribute('data-row', 'added');
    await expect(added.locator('code .sr-only')).toHaveText('Added: ');
    await expect(added.locator('[aria-hidden="true"]').filter({ hasText: '+' })).toHaveCount(1);

    const finding = findingUnder(added);
    await expect(finding).toContainText('required_field_added');
    await expect(finding).toContainText("'total' is now required");
    await expect(finding.locator('.sr-only')).toContainText('Registry finding on the line above');

    // The registry's own count of findings is all on lines, none left over.
    await expect(row.getByTestId('diff-unplaced')).toHaveCount(0);
  });

  test('says how much changed, in words', async ({ page }) => {
    const row = await openReview(page, SHARED_HELD);

    await expect(row.getByTestId('diff-totals')).toContainText(/lines? added, .*\d+ removed/);
  });

  test('names a scrolling region a keyboard can reach', async ({ page }) => {
    const row = await openReview(page, SHARED_HELD);
    const region = row.getByRole('region', { name: 'Schema changes, v1 to v2' });

    await expect(region).toBeVisible();
    await expect(region).toHaveAttribute('tabindex', '0');

    await region.focus();
    await expect(region).toBeFocused();
  });

  test('ties an Avro finding to the field its name-based path names', async ({ page }) => {
    const row = await openReview(page, FIXTURES.avro);

    // `#/fields/customer/fields/age`: the record that is the type of `customer`.
    const age = diffRow(row, '"name": "age",');
    await expect(findingUnder(age)).toContainText('type_narrowed');

    // `#/fields/id`.
    const id = diffRow(row, '"name": "id",');
    await expect(findingUnder(id)).toContainText('type_narrowed');

    // A field the change added: `#/fields/total`, on an added line.
    const total = diffRow(row, '"name": "total",');
    await expect(total).toHaveAttribute('data-row', 'added');
    await expect(findingUnder(total)).toContainText('required_field_added');
  });

  test('ties a Protobuf finding to the field with that number, and once for both directions', async ({
    page,
  }) => {
    const row = await openReview(page, FIXTURES.proto);

    const id = diffRow(row, 'int64 id = 1;');
    await expect(id).toHaveAttribute('data-row', 'added');
    const finding = findingUnder(id);
    await expect(finding).toContainText('wire_type_changed');
    // The registry says it twice, BACKWARD and FORWARD, in the same words.
    await expect(finding).toContainText('BACKWARD + FORWARD');
    await expect(diffRow(row, 'string id = 1;')).toHaveAttribute('data-row', 'removed');
  });

  test('folds the unchanged lines behind buttons, and opens them', async ({ page }) => {
    const row = await openReview(page, FIXTURES.long);
    const folds = row.getByRole('button', { name: /^Show \d+ unchanged lines?$/ });

    // Three lines of context after the change, and four before it: three above the line the
    // registry's finding sits on, which is the line above the change.
    const SHOWN_CONTEXT = 7;

    await expect(folds).toHaveCount(2);
    await expect(row.locator('[data-row="context"]')).toHaveCount(SHOWN_CONTEXT);

    const first = folds.first();
    const hidden = Number(/\d+/.exec((await first.getAttribute('aria-label')) ?? '')?.[0]);
    expect(hidden).toBeGreaterThan(20);

    await first.click();

    await expect(folds).toHaveCount(1);
    await expect(row.locator('[data-row="context"]')).toHaveCount(SHOWN_CONTEXT + hidden);
  });

  test('says so, and shows the summary, when more lines changed than the view will show', async ({
    page,
  }) => {
    const row = await openReview(page, FIXTURES.huge);

    await expect(row.getByTestId('diff-fallback')).toContainText('More than 1,000 lines changed');
    await expect(row.getByTestId('diff-fallback')).toContainText('The summary is shown instead.');
    await expect(row.getByTestId('diff-region')).toHaveCount(0);
    // The summary the reviewer would have had.
    await expect(row.getByText('type_narrowed').first()).toBeVisible();
    // And the choice is still Detailed: the next, smaller change is shown in detail.
    await expect(row.getByRole('radio', { name: 'Detailed' })).toBeChecked();
  });

  test('diffs a schema near the registry’s ceiling off the main thread', async ({ page }) => {
    const workers: string[] = [];
    page.on('worker', (worker) => workers.push(worker.url()));
    await page.addInitScript(() => {
      (window as unknown as { __long: number[] }).__long = [];
      new PerformanceObserver((list) => {
        for (const entry of list.getEntries()) {
          (window as unknown as { __long: number[] }).__long.push(entry.duration);
        }
      }).observe({ type: 'longtask', buffered: true });
    });

    const row = await openReview(page, FIXTURES.dense);

    await expect(row.getByTestId('diff-region')).toBeVisible({ timeout: 30_000 });
    // Three type changes in about fifty thousand lines: three pairs, and the folds between them.
    await expect(row.locator('[data-row="added"]')).toHaveCount(3);
    await expect(row.locator('[data-row="removed"]')).toHaveCount(3);
    await expect(row.getByRole('button', { name: /^Show \d{4,} unchanged lines$/ })).toHaveCount(4);

    // A fold of thousands of lines opens a screenful at a time, not all at once.
    const context = row.locator('[data-row="context"]');
    const before = await context.count();
    await row
      .getByRole('button', { name: /^Show \d{4,} unchanged lines$/ })
      .first()
      .click();
    await expect(context).toHaveCount(before + 500);

    // A worker did it. `worker-<hash>.js` is what the build names it and what the container's
    // policy for workers is keyed on, so this is also the proof it would be served.
    expect(workers.some((url) => /\/worker-[A-Za-z0-9_-]+\.js(\?|$)/.test(url))).toBe(true);

    // And the page was never blocked for long. A coarse bound on purpose (a loaded CI machine
    // stalls a page for a while without the code's help); on the author's machine the longest
    // block was under 160 ms at 6x CPU slowdown, and a diff on the main thread is seconds.
    const longest = await page.evaluate(() =>
      Math.max(0, ...(window as unknown as { __long: number[] }).__long),
    );
    expect(longest).toBeLessThan(1_000);
  });

  test('reports no console errors', async ({ page }) => {
    const errors: string[] = [];
    page.on('console', (message) => {
      if (message.type() === 'error') {
        errors.push(message.text());
      }
    });
    page.on('pageerror', (error) => errors.push(error.message));

    const row = await openReview(page, FIXTURES.avro);
    await expect(row.getByTestId('diff-region')).toBeVisible();

    expect(errors).toEqual([]);
  });
});

test.describe('the detailed view, laid out', () => {
  test.beforeEach(async ({ page }) => {
    await page.addInitScript((key) => localStorage.setItem(key, 'detailed'), VIEW_KEY);
  });

  for (const appearance of ['light', 'dark'] as const) {
    test(`tints added and removed rows in the ${appearance} theme`, async ({ page }) => {
      await page.addInitScript(
        (choice) => localStorage.setItem('concordat.appearance', choice),
        appearance,
      );
      const row = await openReview(page, FIXTURES.avro);
      await expect(row.getByTestId('diff-region')).toBeVisible();

      const background = (locator: Locator) =>
        locator.first().evaluate((element) => getComputedStyle(element).backgroundColor);

      // Neither is transparent, and they are not the same colour: that is the third signal,
      // after the sign and the word, and it has to exist in both themes.
      const added = await background(row.locator('[data-row="added"]'));
      const removed = await background(row.locator('[data-row="removed"]'));
      const context = await background(row.locator('[data-row="context"]'));

      expect(added).not.toBe(context);
      expect(removed).not.toBe(context);
      expect(added).not.toBe(removed);
      expect(await page.evaluate(() => document.documentElement.classList.contains('dark'))).toBe(
        appearance === 'dark',
      );
    });
  }

  test('does not widen the page at phone width: the diff scrolls inside its own region', async ({
    page,
  }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    const row = await openReview(page, FIXTURES.avro);
    const region = row.getByTestId('diff-region');
    await expect(region).toBeVisible();

    const overflow = await page.evaluate(
      () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
    );
    expect(overflow).toBeLessThanOrEqual(0);

    // A long line is reached by scrolling the region, not the page.
    const scrolls = await region.evaluate((element) => element.scrollWidth > element.clientWidth);
    expect(scrolls).toBe(true);

    // The finding is as wide as what is visible, so it can be read without scrolling.
    const finding = row.getByTestId('diff-finding').first();
    const [findingBox, regionBox] = [await finding.boundingBox(), await region.boundingBox()];
    expect(findingBox!.x + findingBox!.width).toBeLessThanOrEqual(
      regionBox!.x + regionBox!.width + 1,
    );
  });
});
