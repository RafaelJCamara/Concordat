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

/**
 * Comparing two versions, against the engine's real answer.
 *
 * <b>Run against the held proposal for the same reason the approval-gate tests are.</b> The
 * divergence comes from the compatibility engine reading a genuinely breaking schema, and the
 * `AWAITING_APPROVAL` label from the subject — two separate requests the screen has to join.
 * A unit test with a fixture for each agrees with the frontend by construction; this is the
 * only place the two are produced by the registry.
 */
test.describe('comparing two versions', () => {
  test('shows the divergence the engine found, and labels the held end', async ({ page }) => {
    await page.goto(`/subjects/${HELD_SUBJECT}/versions/1/diff/2`);

    await expect(page.getByRole('heading', { level: 1 })).toContainText('v1');
    await expect(page.getByRole('heading', { level: 1 })).toContainText('v2');
    await expect(page.getByText('required_field_added')).toBeVisible();
    await expect(page.getByText('AWAITING_APPROVAL')).toBeVisible();
  });

  test('is reached from the later version, baseline first', async ({ page }) => {
    await page.goto(`/subjects/${HELD_SUBJECT}/versions/2`);
    await page.getByRole('link', { name: 'Compare with v1' }).click();

    await expect(page).toHaveURL(new RegExp(`/subjects/${HELD_SUBJECT}/versions/1/diff/2$`));
    await expect(page.getByText('required_field_added')).toBeVisible();
  });

  test('offers no comparison on the first version', async ({ page }) => {
    await page.goto(`/subjects/${HELD_SUBJECT}/versions/1`);

    await expect(page.getByRole('heading', { name: 'v1' })).toBeVisible();
    await expect(page.getByRole('link', { name: /compare with/i })).toHaveCount(0);
  });

  test('swaps direction rather than inventing a second screen', async ({ page }) => {
    await page.goto(`/subjects/${HELD_SUBJECT}/versions/1/diff/2`);
    await page.getByRole('link', { name: 'Swap direction' }).click();

    await expect(page).toHaveURL(new RegExp(`/versions/2/diff/1$`));
    await expect(page.getByText('Baseline')).toBeVisible();
  });

  test('says so plainly when a version does not exist', async ({ page }) => {
    await page.goto(`/subjects/${HELD_SUBJECT}/versions/1/diff/99`);

    await expect(page.getByRole('heading', { name: /could not compare/i })).toBeVisible();
  });

  test('says so plainly for a malformed address, without a request', async ({ page }) => {
    await page.goto(`/subjects/${HELD_SUBJECT}/versions/latest/diff/2`);

    await expect(page.getByRole('heading', { name: /could not compare/i })).toBeVisible();
  });

  test('reports no console errors', async ({ page }) => {
    const errors: string[] = [];
    page.on('console', (message) => {
      if (message.type() === 'error') {
        errors.push(message.text());
      }
    });
    page.on('pageerror', (error) => errors.push(error.message));

    await page.goto(`/subjects/${HELD_SUBJECT}/versions/1/diff/2`);
    await expect(page.getByText('required_field_added')).toBeVisible();

    expect(errors).toEqual([]);
  });
});

/**
 * Impact analysis, against consumers the registry was actually told about.
 *
 * <b>The verdicts are the engine's, and the first draft of this block was wrong about one.</b>
 * It expected a consumer pinned to v1 to BREAK under a proposal that adds a required field.
 * It does not: impact is judged FORWARD — can the old reader read what the new schema writes —
 * and an old reader simply ignores an extra property. The engine was right and the fixture was
 * not, which is the argument for asserting against a real registry rather than a hand-built
 * answer that agrees with whoever wrote it.
 *
 * So two subjects. `PaymentTaken` adds a required field: its v1 reader is COMPATIBLE and its
 * `latest` follower cannot be judged. `LedgerPosted` changes a property's type, which does
 * break an old reader, with a reason to read.
 */
const BREAKING_SUBJECT = 'acme.e2e.LedgerPosted';

test.describe('impact analysis', () => {
  test('names the consumer the engine says breaks, and why', async ({ page }) => {
    await page.goto(`/subjects/${BREAKING_SUBJECT}/versions/2/impact`);

    await expect(page.getByRole('heading', { level: 1 })).toContainText('v2');
    const reader = page.getByRole('listitem').filter({ hasText: 'e2e-ledger-reader' });

    await expect(reader).toContainText('BREAKS');
    await expect(reader).toContainText('declares 1');
    await expect(page.getByText('1 breaking')).toBeVisible();
    await expect(page.getByText('1 of 1 registered consumer breaks.')).toBeVisible();
  });

  test('judges FORWARD: a newly required field does not break a reader pinned to v1', async ({
    page,
  }) => {
    await page.goto(`/subjects/${HELD_SUBJECT}/versions/2/impact`);

    const billing = page.getByRole('listitem').filter({ hasText: 'e2e-billing' });

    await expect(billing).toContainText('COMPATIBLE');
    await expect(billing).not.toContainText('BREAKS');
  });

  test('does not call a consumer that follows latest compatible', async ({ page }) => {
    await page.goto(`/subjects/${HELD_SUBJECT}/versions/2/impact`);

    const dashboard = page.getByRole('listitem').filter({ hasText: 'e2e-dashboard' });

    await expect(dashboard).toContainText('FOLLOWS LATEST');
    await expect(dashboard).not.toContainText('COMPATIBLE');
  });

  test('counts only what it could judge', async ({ page }) => {
    await page.goto(`/subjects/${HELD_SUBJECT}/versions/2/impact`);

    await expect(page.getByText('0 of 2 registered consumers break.')).toBeVisible();
    await expect(page.getByText(/1 could not be judged/)).toBeVisible();
  });

  test('is reached from the version screen', async ({ page }) => {
    await page.goto(`/subjects/${HELD_SUBJECT}/versions/2`);
    await page.getByRole('link', { name: 'Who is affected' }).click();

    await expect(page).toHaveURL(new RegExp(`/subjects/${HELD_SUBJECT}/versions/2/impact$`));
    await expect(page.getByText('e2e-billing')).toBeVisible();
  });

  test('resolves `latest` and names the version it meant', async ({ page }) => {
    // `latest` is the gated pointer, which is v1 here; the heading says so rather than echoing
    // the word in the address.
    await page.goto(`/subjects/${HELD_SUBJECT}/versions/latest/impact`);

    await expect(page.getByRole('heading', { level: 1 })).toContainText('v1');
  });

  test('says an empty list is not a finding of safety', async ({ page }) => {
    await page.goto('/subjects/acme.e2e.OrderCreated/versions/1/impact');

    await expect(page.getByText('No registered consumers')).toBeVisible();
    await expect(page.getByText(/not a finding that nobody is affected/)).toBeVisible();
  });

  test('says so plainly when the version does not exist', async ({ page }) => {
    await page.goto(`/subjects/${HELD_SUBJECT}/versions/99/impact`);

    await expect(page.getByRole('heading', { name: /could not analyse/i })).toBeVisible();
  });

  test('reports no console errors', async ({ page }) => {
    const errors: string[] = [];
    page.on('console', (message) => {
      if (message.type() === 'error') {
        errors.push(message.text());
      }
    });
    page.on('pageerror', (error) => errors.push(error.message));

    await page.goto(`/subjects/${HELD_SUBJECT}/versions/2/impact`);
    await expect(page.getByText('e2e-billing')).toBeVisible();

    expect(errors).toEqual([]);
  });
});
