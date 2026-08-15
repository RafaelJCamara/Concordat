import { expect, test } from '@playwright/test';
import { OWNER } from './registry';
import { signIn } from './sign-in';

/**
 * The subject list, rendering what a real registry actually sent.
 *
 * <b>This suite exists because of the bug it would have caught.</b> `VersionStatus.Dismissed`
 * shipped with M7 and `wire-tokens.ts` never learned it. The web app's unknown-token guard is
 * strict by design — a token it does not recognise is a newer server, and guessing would be
 * worse — so one dismissed version in an environment failed the entire subject list with
 * *"the registry sent 'DISMISSED' for 'status', which this build does not recognise"*.
 *
 * Every unit test on both sides passed. 1,489 .NET tests, 187 Angular tests, and none of them
 * loaded a page against a running registry. The first browser to do so found it in ten seconds.
 */
test.describe('the subject list', () => {
  test('renders rows from the registry rather than an error', async ({ page }) => {
    await page.goto('/subjects');

    await expect(page.getByRole('heading', { name: 'Subjects' })).toBeVisible();

    // The guard renders this alert for any token it cannot parse. Asserting its ABSENCE is the
    // whole point: a vocabulary that drifts fails here and nowhere else.
    await expect(page.getByRole('heading', { name: /could not load subjects/i })).toHaveCount(0);
    await expect(page.getByRole('table')).toBeVisible();
  });

  test('reports no console errors while doing it', async ({ page }) => {
    // A page that renders while logging a failed request is a page that is quietly degraded --
    // /auth/resume 404ing against a stale API looked exactly like this, and looked fine.
    const errors: string[] = [];
    page.on('console', (message) => {
      if (message.type() === 'error') {
        errors.push(message.text());
      }
    });
    page.on('pageerror', (error) => errors.push(error.message));

    await page.goto('/subjects');
    await expect(page.getByRole('heading', { name: 'Subjects' })).toBeVisible();

    expect(errors).toEqual([]);
  });

  test('names the environment it is reading', async ({ page }) => {
    await page.goto('/subjects');

    // Cheap, and it catches a whole class of "which registry am I even looking at" confusion
    // that costs real time during an incident.
    await expect(page.getByText('dev').first()).toBeVisible();
  });
});

/**
 * The detail screens, against the same real registry, for the same reason.
 *
 * These are the two screens with the most mapping between them and the wire — a version's
 * status, its format, its schema document and every reference on it — so they are where the
 * next `DISMISSED` would surface. The assertions are deliberately about *absence of the guard's
 * alert* as much as presence of content.
 */
const SUBJECT = 'acme.e2e.OrderCreated';

test.describe('the subject detail screen', () => {
  test('opens from the list by clicking the subject', async ({ page }) => {
    // Through the link rather than by navigating directly: this is also the assertion that the
    // list builds a URL the router actually matches.
    await page.goto('/subjects');
    await page.getByRole('link', { name: SUBJECT }).click();

    await expect(page).toHaveURL(`/subjects/${SUBJECT}`);
    await expect(page.getByRole('heading', { name: SUBJECT })).toBeVisible();
  });

  test('renders the versions the registry sent', async ({ page }) => {
    await page.goto(`/subjects/${SUBJECT}`);

    await expect(page.getByRole('heading', { name: /could not load/i })).toHaveCount(0);
    await expect(page.getByRole('table')).toBeVisible();
    await expect(page.getByRole('link', { name: 'v1' })).toBeVisible();
  });
});

test.describe('the version detail screen', () => {
  test('shows the schema document, which is a second request', async ({ page }) => {
    // A version carries a schema id and never the text, so this page is the only one that
    // proves `/v1/schemas/{id}` is reachable and mapped.
    await page.goto(`/subjects/${SUBJECT}/versions/1`);

    await expect(page.getByRole('heading', { name: 'v1' })).toBeVisible();
    await expect(page.getByLabel(/schema document/i)).toBeVisible();
    await expect(page.getByRole('heading', { name: /could not load/i })).toHaveCount(0);
  });

  test('resolves the literal `latest` the way the API does', async ({ page }) => {
    // The pasted-link case. `latest` is the registry's gated pointer, not the highest
    // ordinal, and this is the only test that drives that end to end.
    await page.goto(`/subjects/${SUBJECT}/versions/latest`);

    await expect(page.getByRole('heading', { name: 'v1' })).toBeVisible();
    await expect(page.getByText('latest')).toBeVisible();
  });

  test('says so plainly when the ordinal does not exist', async ({ page }) => {
    await page.goto(`/subjects/${SUBJECT}/versions/999`);

    await expect(page.getByRole('heading', { name: /could not load this version/i })).toBeVisible();
  });

  test('reports no console errors across either screen', async ({ page }) => {
    const errors: string[] = [];
    page.on('console', (message) => {
      if (message.type() === 'error') {
        errors.push(message.text());
      }
    });
    page.on('pageerror', (error) => errors.push(error.message));

    await page.goto(`/subjects/${SUBJECT}`);
    await expect(page.getByRole('table')).toBeVisible();
    await page.goto(`/subjects/${SUBJECT}/versions/1`);
    await expect(page.getByLabel(/schema document/i)).toBeVisible();

    expect(errors).toEqual([]);
  });
});

/**
 * A version the registry is actually holding at the approval gate.
 *
 * <b>Not a hand-built fixture.</b> `global-setup.ts` registers a genuinely breaking change
 * against `acme.e2e.PaymentTaken`, so the token, the divergence and the unmoved `latest`
 * pointer all come from the compatibility engine rather than from a test author who already
 * agrees with the frontend. That distinction is the entire lesson of `DISMISSED`: every unit
 * test on both sides passed while the two disagreed about a status token.
 */
const HELD_SUBJECT = 'acme.e2e.PaymentTaken';

test.describe('a version held at the approval gate', () => {
  test('is flagged on the subject list rather than looking ordinary', async ({ page }) => {
    await page.goto('/subjects');

    const row = page.getByRole('row', { name: new RegExp(HELD_SUBJECT) });
    await expect(row).toContainText(/awaiting approval/i);
  });

  test('does not move the latest pointer', async ({ page }) => {
    // The whole point of the gate. v2 exists and v1 is still what consumers resolve, so the
    // table must badge v1 as `latest` and not the higher ordinal.
    await page.goto(`/subjects/${HELD_SUBJECT}`);

    const v1 = page.getByRole('row', { name: /v1/ });
    const v2 = page.getByRole('row', { name: /v2/ });

    await expect(v1).toContainText('latest');
    await expect(v2).not.toContainText('latest');
    await expect(v2).toContainText('AWAITING_APPROVAL');
  });

  test('resolves `latest` to v1, not to the newest ordinal', async ({ page }) => {
    // Same property, through the URL people paste. A screen that derived `latest` as
    // `max(ordinal)` would show an unapproved schema as the current contract.
    await page.goto(`/subjects/${HELD_SUBJECT}/versions/latest`);

    await expect(page.getByRole('heading', { name: 'v1' })).toBeVisible();
  });

  test('names the status on the version itself', async ({ page }) => {
    await page.goto(`/subjects/${HELD_SUBJECT}/versions/2`);

    await expect(page.getByText('AWAITING_APPROVAL')).toBeVisible();
    await expect(page.getByRole('heading', { name: /could not load/i })).toHaveCount(0);
  });
});

/**
 * The write path, driven through the form, against the real registry.
 *
 * <b>Idempotent by construction rather than by cleanup.</b> It submits the document that is
 * already at the tip — the one `global-setup.ts` registered as v1 — so the registry
 * deduplicates by content, allocates no ordinal and answers 200 rather than 201. The suite
 * can therefore run any number of times without growing the version list, and it still
 * exercises every part of the path: the form, the POST, the response mapping and the outcome
 * panel.
 *
 * It also lands on the branch that is easiest to get wrong. 200 and 201 are both successes
 * and mean different things, and a client that treats them alike double-counts versions —
 * which is exactly the mistake that would look like a working screen.
 */
const TIP_SCHEMA = '{"type":"object","properties":{"id":{"type":"string"}}}';

test.describe('registering a version', () => {
  test('reports an unchanged document as unchanged, not as a new version', async ({ page }) => {
    await signIn(page, OWNER);
    await page.goto(`/subjects/${SUBJECT}/versions/new`);

    await page.getByRole('textbox', { name: 'Schema document' }).fill(TIP_SCHEMA);
    await page.getByRole('button', { name: 'Register' }).click();

    await expect(page.getByText('Unchanged')).toBeVisible();
    await expect(page.getByText(/no ordinal was allocated/i)).toBeVisible();
  });

  test('leaves the version list exactly as it was', async ({ page }) => {
    // The assertion that makes the previous test safe to keep running: if a re-registration
    // ever did allocate an ordinal, this is where it would show up rather than as a slowly
    // growing table nobody was watching.
    await page.goto(`/subjects/${SUBJECT}`);

    await expect(page.getByRole('link', { name: 'v1' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'v2' })).toHaveCount(0);
  });

  test('refuses to submit a document that is not JSON', async ({ page }) => {
    // Caught in the browser, before a request is spent on a certain 400.
    await signIn(page, OWNER);
    await page.goto(`/subjects/${SUBJECT}/versions/new`);

    await page.getByRole('textbox', { name: 'Schema document' }).fill('{"type": ');

    await expect(page.getByText(/not valid json/i)).toBeVisible();
    await expect(page.getByRole('button', { name: 'Register' })).toBeDisabled();
  });
});
