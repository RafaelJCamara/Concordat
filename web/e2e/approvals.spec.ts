import { expect, test, type Page } from '@playwright/test';
import { READER_STORAGE_STATE } from './global-setup';
import { ensureHeldVersion, ownerCredential, readApprovalQueue } from './registry';

/**
 * The approval queue, against versions the compatibility engine really held.
 *
 * <b>Deciding is the one thing here that cannot be idempotent.</b> Approving a version makes it
 * `latest` and rejecting is final, so a test that decided the shared fixtures would leave them
 * gone for the next run — and `PaymentTaken` and `LedgerPosted` hold the held versions half of
 * this suite reads. The decision tests therefore create a subject of their own, named by the
 * run, and decide that. It costs the registry two subjects per run, which is the honest price of
 * testing something that has a consequence; the read-only tests below it reuse the shared pair
 * and leave nothing behind.
 */
const RUN = Date.now();
const SHARED_HELD = 'acme.e2e.PaymentTaken';

function card(page: Page, subject: string) {
  return page.getByRole('listitem').filter({ has: page.getByRole('link', { name: subject }) });
}

/**
 * Pages in until the subject's row is on screen.
 *
 * The queue is the registry's, a page at a time and oldest first (ADR-042), so a version proposed
 * this run is the newest and is on page one only while dev holds fewer than a page of older
 * ones. Nothing here deletes a held version, and a suite that fails when the queue grows past
 * fifty would be a red run about nothing the screen is for.
 */
async function reveal(page: Page, subject: string): Promise<void> {
  const more = page.getByRole('button', { name: 'Show more' });

  while ((await card(page, subject).count()) === 0 && (await more.count()) > 0) {
    await more.click();
    await expect(page.getByRole('button', { name: 'Loading…' })).toHaveCount(0);
  }
}

test.describe('the approval queue', () => {
  test('lists a version the engine held, with who registered it and what it replaces', async ({
    page,
  }) => {
    await page.goto('/approvals');

    const row = card(page, SHARED_HELD);

    await expect(row).toContainText('AWAITING_APPROVAL');
    await expect(row).toContainText('v2');
    await expect(row).toContainText('replaces v1');
  });

  test('is in the sidebar, marked as the current page', async ({ page }) => {
    await page.goto('/approvals');

    const link = page.getByRole('navigation', { name: 'Main' }).getByRole('link', {
      name: 'Approvals',
    });

    await expect(link).toHaveAttribute('aria-current', 'page');
  });

  test('opens the review: the divergence and the consumers, each from the engine', async ({
    page,
  }) => {
    await page.goto('/approvals');

    const row = card(page, SHARED_HELD);
    await row.getByRole('button', { name: /review changes and impact/i }).click();

    await expect(row.getByText('required_field_added')).toBeVisible();
    await expect(row.getByText('e2e-billing')).toBeVisible();
    await expect(row.getByText('FOLLOWS LATEST')).toBeVisible();
  });

  test('keeps the review closed until asked, so nothing is fetched for unopened rows', async ({
    page,
  }) => {
    const impactRequests: string[] = [];
    page.on('request', (request) => {
      if (request.url().includes('/impact')) {
        impactRequests.push(request.url());
      }
    });

    await page.goto('/approvals');
    await expect(card(page, SHARED_HELD)).toBeVisible();

    expect(impactRequests).toEqual([]);
  });

  test('offers an owner both', async ({ page }) => {
    // The other half of the reader test below, and the reason its absence is worth asserting: a
    // button that is absent for everybody would satisfy that test while simply being broken.
    // Signed in as the owner already — that is the default for every test.
    await page.goto('/approvals');

    const row = card(page, SHARED_HELD);

    await expect(row.getByRole('button', { name: 'Approve' })).toBeVisible();
    await expect(row.getByRole('button', { name: 'Reject' })).toBeVisible();
  });

  test('does not decide on the first click, and Cancel leaves it as it was', async ({ page }) => {
    await page.goto('/approvals');

    const row = card(page, SHARED_HELD);
    await row.getByRole('button', { name: 'Approve' }).click();

    await expect(row.getByText(/make it latest\?/)).toBeVisible();

    await row.getByRole('button', { name: 'Cancel' }).click();

    await expect(row.getByRole('button', { name: 'Approve' })).toBeVisible();

    // Still held: the registry, not the page, is what says so.
    await page.goto(`/subjects/${SHARED_HELD}/versions/2`);
    await expect(page.getByText('AWAITING_APPROVAL')).toBeVisible();
  });

  test('asks the registry for the queue, and never for the subject list', async ({ page }) => {
    // The queue used to be built here from every subject with every version. A request for the
    // list on this page means it is being derived again, and the day that list is paged a held
    // version on its second page is missing from the one screen that exists to show them.
    const requests: string[] = [];
    page.on('request', (request) => {
      if (request.url().includes('/v1/')) {
        requests.push(new URL(request.url()).pathname);
      }
    });

    await page.goto('/approvals');
    await expect(card(page, SHARED_HELD)).toBeVisible();

    expect(requests.filter((path) => path === '/v1/environments/dev/approvals')).toHaveLength(1);
    expect(requests.filter((path) => path === '/v1/environments/dev/subjects')).toEqual([]);
  });

  test('says how much of a long queue it is showing, and shows more when asked', async ({
    page,
  }) => {
    // A page of one, so the queue the shared fixtures make (two held, and more on a database that
    // has been used) is longer than the screen has read. The registry still answers: only the
    // page size the screen asks for is changed, in the request.
    const credential = await ownerCredential();
    const whole = await readApprovalQueue(credential, 'dev');
    expect(whole.total).toBeGreaterThanOrEqual(2);

    await page.route('**/v1/environments/dev/approvals*', async (route) => {
      const url = new URL(route.request().url());
      url.searchParams.set('limit', '1');
      await route.continue({ url: url.toString() });
    });

    await page.goto('/approvals');

    const extent = page.getByTestId('queue-extent');
    await expect(page.getByRole('listitem').filter({ has: page.getByRole('link') })).toHaveCount(1);
    await expect(extent).toContainText(`Showing the 1 oldest of ${whole.total} waiting`);

    // Oldest first: what is on screen is what the registry says is first.
    await expect(card(page, whole.items[0]!.subject)).toBeVisible();

    await page.getByRole('button', { name: 'Show more' }).click();

    await expect(page.getByRole('listitem').filter({ has: page.getByRole('link') })).toHaveCount(2);
    await expect(card(page, whole.items[1]!.subject)).toBeVisible();

    // Both outcomes are the screen keeping its word. A database that has only the shared pair
    // held has now been read in full, and says nothing more about extent; a used one still has a
    // remainder to account for.
    if (whole.total > 2) {
      await expect(extent).toContainText(`Showing the 2 oldest of ${whole.total} waiting`);
    } else {
      await expect(extent).toHaveCount(0);
      await expect(page.getByRole('button', { name: 'Show more' })).toHaveCount(0);
    }
  });

  test('reports no console errors', async ({ page }) => {
    const errors: string[] = [];
    page.on('console', (message) => {
      if (message.type() === 'error') {
        errors.push(message.text());
      }
    });
    page.on('pageerror', (error) => errors.push(error.message));

    await page.goto('/approvals');
    await card(page, SHARED_HELD)
      .getByRole('button', { name: /review changes and impact/i })
      .click();
    await expect(page.getByText('required_field_added').first()).toBeVisible();

    expect(errors).toEqual([]);
  });
});

test.describe('deciding', () => {
  test('approving makes the version latest, and says so', async ({ page }) => {
    const subject = `acme.e2e.approve.${RUN}`;
    await ensureHeldVersion(await ownerCredential(), 'dev', subject);

    await page.goto('/approvals');
    await reveal(page, subject);

    const row = card(page, subject);
    await row.getByRole('button', { name: 'Approve' }).click();
    await row.getByRole('button', { name: 'Confirm' }).click();

    // Announced rather than just gone: a row that vanishes without a word reads as the page
    // having lost it.
    await expect(page.getByRole('status')).toContainText(`Approved ${subject} v2`);
    await expect(row).toHaveCount(0);

    // The registry agrees, which is the only thing that makes the announcement true.
    await page.goto(`/subjects/${subject}/versions/latest`);
    await expect(page.getByRole('heading', { name: 'v2' })).toBeVisible();
  });

  test('rejecting leaves latest where it was, and says it is final', async ({ page }) => {
    const subject = `acme.e2e.reject.${RUN}`;
    await ensureHeldVersion(await ownerCredential(), 'dev', subject);

    await page.goto('/approvals');
    await reveal(page, subject);

    const row = card(page, subject);
    await row.getByRole('button', { name: 'Reject' }).click();

    await expect(row.getByText(/cannot be undone/)).toBeVisible();

    await row.getByRole('button', { name: 'Confirm' }).click();

    await expect(page.getByRole('status')).toContainText(`Rejected ${subject} v2`);
    await expect(page.getByRole('status')).not.toContainText('latest');
    await expect(row).toHaveCount(0);

    await page.goto(`/subjects/${subject}/versions/2`);
    await expect(page.getByText('REJECTED')).toBeVisible();

    await page.goto(`/subjects/${subject}/versions/latest`);
    await expect(page.getByRole('heading', { name: 'v1' })).toBeVisible();
  });
});

test.describe('the approval queue, as a reader', () => {
  // Signed in once by global setup rather than in each test: sign-in is rate-limited, and a
  // reader's whole subject here is what they are offered, not the act of signing in.
  test.use({ storageState: READER_STORAGE_STATE });

  test('is offered the review and no decision', async ({ page }) => {
    await page.goto('/approvals');

    const row = card(page, SHARED_HELD);

    await expect(row.getByRole('button', { name: /review changes and impact/i })).toBeVisible();
    await expect(row.getByRole('button', { name: 'Approve' })).toHaveCount(0);
    await expect(row.getByRole('button', { name: 'Reject' })).toHaveCount(0);
  });
});
