import { expect, test, type Page } from '@playwright/test';
import {
  decideRaw,
  ensureHeldVersion,
  ensureSubject,
  ownerCredential,
  patchSubjectRaw,
  readSubject,
  registerVersion,
  TYPE_CHANGE_SCHEMAS,
} from './registry';

/**
 * The pill on the subjects page that says a version is awaiting approval, and where it goes (ADR-052),
 * against versions the compatibility engine really held.
 *
 * <b>What the unit tests cannot say is that the link arrives.</b> The table builds the address, the
 * approvals page reads it and the registry narrows the queue, and each is right about itself; a pill that
 * linked to a screen that ignored the address, or to one that opened the wrong version, would pass all
 * three. So these click it, in a browser, and look at what the approvals screen then shows.
 *
 * <b>It makes four subjects per run in `dev` (`acme.e2e.pill.<timestamp>.<stem>`), and rejects what it held.</b>
 * `one` has a single held version, `many` has two, `plain` has none and `old` is deprecated and has none.
 * A held version cannot be undone, and `approvals.spec.ts` reads the shared queue, so an `afterAll` rejects
 * what this run held even when a test failed; a killed process leaves them waiting. Subjects are permanent,
 * like every other spec's. It never touches the shared pair (`PaymentTaken`, `LedgerPosted`).
 */
const RUN = Date.now();
const ENVIRONMENT = 'dev';
const STEM = `acme.e2e.pill.${RUN}`;
const ONE = `${STEM}.one`;
const MANY = `${STEM}.many`;
const PLAIN = `${STEM}.plain`;
const OLD = `${STEM}.old`;

const row = (page: Page, subject: string) =>
  page.getByRole('row').filter({ has: page.getByRole('link', { name: subject, exact: true }) });

/** Finds the subject's row on the list by searching for it, the way a person with a long list would. */
async function findRow(page: Page, subject: string) {
  await page.goto(`/subjects?q=${encodeURIComponent(subject)}`);
  await expect(row(page, subject)).toBeVisible();

  return row(page, subject);
}

const card = (page: Page, subject: string, ordinal: number) =>
  page.locator('li').filter({ has: page.locator(`[data-pending-key="${subject}@${ordinal}"]`) });

test.describe('the awaiting-approval pill', () => {
  test.describe.configure({ mode: 'serial' });

  const held: Record<string, number[]> = {};

  test.beforeAll(async () => {
    test.setTimeout(60_000);
    const credential = await ownerCredential();

    for (const name of [ONE, MANY]) {
      await ensureHeldVersion(credential, ENVIRONMENT, name);
    }

    // A second held version for `many`: a different breaking change, which the engine holds as its own
    // proposal rather than as the same one again (ADR-040).
    const second = await registerVersion(
      credential,
      ENVIRONMENT,
      MANY,
      TYPE_CHANGE_SCHEMAS.proposal,
    );
    expect(second.status).toBeLessThan(300);

    await ensureSubject(credential, ENVIRONMENT, PLAIN);
    await ensureSubject(credential, ENVIRONMENT, OLD);
    const deprecated = await patchSubjectRaw(credential, ENVIRONMENT, OLD, {
      owner: 'e2e',
      deprecate: true,
    });
    expect(deprecated.status).toBeLessThan(300);

    for (const name of [ONE, MANY]) {
      held[name] = (await readSubject(credential, ENVIRONMENT, name)).versions
        .filter((v) => v.status === 'AWAITING_APPROVAL')
        .map((v) => v.ordinal);
    }

    expect(held[ONE]).toHaveLength(1);
    expect(held[MANY]).toHaveLength(2);
  });

  test.afterAll(async () => {
    const credential = await ownerCredential();

    for (const name of [ONE, MANY]) {
      const subject = await readSubject(credential, ENVIRONMENT, name).catch(() => null);

      for (const version of subject?.versions ?? []) {
        if (version.status === 'AWAITING_APPROVAL') {
          await decideRaw(credential, ENVIRONMENT, name, version.ordinal, 'reject');
        }
      }
    }
  });

  test('is a link a keyboard reaches, named for what it does, and looks like one', async ({
    page,
  }) => {
    const link = (await findRow(page, ONE)).getByRole('link', { name: /awaiting approval/ });

    await expect(link).toHaveText(/^\s*1 awaiting approval/);
    await expect(link).toHaveAccessibleName(
      `1 awaiting approval: review the held version of ${ONE}`,
    );
    await expect(link).toHaveAttribute(
      'href',
      `/approvals?subject=${encodeURIComponent(ONE)}&version=${held[ONE]![0]}`,
    );

    // Reached by Tab from the subject's own link, in the row's order.
    await row(page, ONE).getByRole('link', { name: ONE, exact: true }).focus();
    await page.keyboard.press('Tab');
    await expect(link).toBeFocused();

    // A pointer says so before it is pressed: a ring on hover, and the words underline.
    await link.hover();
    const ring = await link.evaluate((el) => getComputedStyle(el).boxShadow);
    expect(ring).not.toBe('none');
    expect(await link.evaluate((el) => getComputedStyle(el).cursor)).toBe('pointer');
  });

  test('takes the one held version to its review, opened, with the focus on its heading', async ({
    page,
  }) => {
    const link = (await findRow(page, ONE)).getByRole('link', { name: /awaiting approval/ });

    // From the keyboard, as it is meant to be reached.
    await link.focus();
    await page.keyboard.press('Enter');

    await expect(page).toHaveURL(
      new RegExp(`/approvals\\?subject=${encodeURIComponent(ONE).replace(/\./g, '\\.')}&version=`),
    );
    await expect(page.getByTestId('approvals-filter')).toContainText(
      `Showing changes waiting on ${ONE}`,
    );

    const target = card(page, ONE, held[ONE]![0]!);
    await expect(target).toHaveCount(1);
    await expect(target.getByRole('button', { name: /Hide changes and impact/ })).toBeVisible();
    await expect(page.locator(`[data-pending-key="${ONE}@${held[ONE]![0]}"]`)).toBeFocused();
    // Only that subject's: nothing else's version is listed.
    await expect(page.locator('li [data-pending-key]')).toHaveCount(1);
    // In the viewport: it was scrolled to.
    await expect(page.locator(`[data-pending-key="${ONE}@${held[ONE]![0]}"]`)).toBeInViewport();
  });

  test('says how many are waiting for this subject, not for the whole queue', async ({ page }) => {
    await page.goto(`/approvals?subject=${encodeURIComponent(ONE)}`);

    await expect(page.locator('li [data-pending-key]')).toHaveCount(1);
    // The registry counts the filtered queue; there is nothing more to show, so nothing says there is.
    await expect(page.getByTestId('queue-extent')).toHaveCount(0);
  });

  test('takes several held versions to the subject’s own list, none of them opened', async ({
    page,
  }) => {
    const link = (await findRow(page, MANY)).getByRole('link', { name: /awaiting approval/ });

    await expect(link).toHaveText(/^\s*2 awaiting approval/);
    await expect(link).toHaveAccessibleName(
      `2 awaiting approval: review the held versions of ${MANY}`,
    );

    await link.click();

    await expect(page).toHaveURL(
      new RegExp(`/approvals\\?subject=${encodeURIComponent(MANY).replace(/\./g, '\\.')}$`),
    );
    await expect(page.locator('li [data-pending-key]')).toHaveCount(2);
    for (const ordinal of held[MANY]!) {
      await expect(
        card(page, MANY, ordinal).getByRole('button', { name: /Review changes and impact/ }),
      ).toBeVisible();
    }
    // The reader chooses; nothing was opened for them.
    await expect(page.getByRole('button', { name: /Hide changes and impact/ })).toHaveCount(0);
  });

  test('"Show all" drops the filter and brings the rest of the queue back', async ({ page }) => {
    await page.goto(`/approvals?subject=${encodeURIComponent(ONE)}`);
    await expect(page.locator('li [data-pending-key]')).toHaveCount(1);

    await page.getByRole('link', { name: 'Show all' }).click();

    await expect(page).toHaveURL(/\/approvals$/);
    await expect(page.getByTestId('approvals-filter')).toHaveCount(0);
    // At least this run's two subjects and the shared pair, and they are not all one subject's.
    await expect.poll(async () => page.locator('li [data-pending-key]').count()).toBeGreaterThan(1);
  });

  test('says so when the subject has nothing held, and offers the rest', async ({ page }) => {
    await page.goto(`/approvals?subject=${encodeURIComponent(PLAIN)}`);

    await expect(page.getByTestId('approvals-filter-empty')).toContainText(
      `Nothing is waiting on a decision for ${PLAIN} — it may already have been decided`,
    );
    await expect(page.getByRole('link', { name: 'Show all' }).first()).toBeVisible();
    // Not the sentence for an empty queue: this queue is not empty.
    await expect(page.getByText('A breaking change registered against a subject')).toHaveCount(0);
  });

  test('says so when the version it was sent for was decided in the meantime', async ({ page }) => {
    const credential = await ownerCredential();
    const stale = `${STEM}.stale`;
    await ensureHeldVersion(credential, ENVIRONMENT, stale);
    const [version] = (await readSubject(credential, ENVIRONMENT, stale)).versions
      .filter((v) => v.status === 'AWAITING_APPROVAL')
      .map((v) => v.ordinal);

    // The list was read while it was held...
    const link = (await findRow(page, stale)).getByRole('link', { name: /awaiting approval/ });
    // ...and somebody else decided it before the click.
    await decideRaw(credential, ENVIRONMENT, stale, version!, 'reject');
    await link.click();

    await expect(page.getByTestId('approvals-filter-empty')).toContainText(
      `Nothing is waiting on a decision for ${stale} — it may already have been decided`,
    );
  });

  test('leaves a state with no destination inert: a plain subject has no pill, a deprecated one a badge that is not a link', async ({
    page,
  }) => {
    const plain = await findRow(page, PLAIN);
    await expect(plain.getByRole('link', { name: /awaiting approval/ })).toHaveCount(0);

    const old = await findRow(page, OLD);
    await expect(old).toContainText('DEPRECATED');
    // The only link in the row's State cell would be a lie; there is none, and nothing there is a button.
    await expect(old.locator('td').last().getByRole('link')).toHaveCount(0);
    await expect(old.locator('td').last().getByRole('button')).toHaveCount(0);
  });

  test('keeps the pill beside the lifecycle badge on a subject that is both', async ({ page }) => {
    // A deprecated subject can still have a held version; only the pill links.
    const credential = await ownerCredential();
    const both = `${STEM}.both`;
    await ensureHeldVersion(credential, ENVIRONMENT, both);
    await patchSubjectRaw(credential, ENVIRONMENT, both, { owner: 'e2e', deprecate: true });

    const cell = (await findRow(page, both)).locator('td').last();
    await expect(cell).toContainText('DEPRECATED');
    await expect(cell.getByRole('link')).toHaveCount(1);
    await expect(cell.getByRole('link')).toContainText('awaiting approval');

    for (const version of (await readSubject(credential, ENVIRONMENT, both)).versions) {
      if (version.status === 'AWAITING_APPROVAL') {
        await decideRaw(credential, ENVIRONMENT, both, version.ordinal, 'reject');
      }
    }
  });

  test('does not scroll sideways on a phone, filtered or not', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(`/approvals?subject=${encodeURIComponent(ONE)}&version=${held[ONE]![0]}`);
    await expect(page.getByTestId('approvals-filter')).toBeVisible();

    const overflow = await page.evaluate(
      () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
    );
    expect(overflow).toBeLessThanOrEqual(0);
  });

  test('raises nothing in the console on the way', async ({ page }) => {
    const problems: string[] = [];

    page.on('console', (message) => {
      if (message.type() === 'error' || message.type() === 'warning') {
        problems.push(message.text());
      }
    });
    page.on('pageerror', (error) => problems.push(error.message));

    const link = (await findRow(page, ONE)).getByRole('link', { name: /awaiting approval/ });
    await link.click();
    await expect(page.getByTestId('approvals-filter')).toBeVisible();
    await page.getByRole('link', { name: 'Show all' }).click();
    await expect(page.getByTestId('approvals-filter')).toHaveCount(0);

    expect(problems).toEqual([]);
  });
});
