import { expect, test, type Page } from '@playwright/test';
import { dryRun, ownerCredential } from './registry';
import { caretLine, setSchema, waitForSubject } from './schema-editor';

/**
 * Validate and the sample message: the client-side check (`ajv`), in a real browser.
 *
 * <b>The first assertion is about the page's policy, not about ajv.</b> `ajv` compiles each
 * schema with `new Function`, and the app's policy is `script-src 'self'` with no
 * `unsafe-eval`. The check works only because it runs in a worker, whose policy is its own; if
 * somebody "fixed" a failure here by adding `unsafe-eval` to the page, the page would be able to
 * evaluate strings again and the first test below would say so. That is the whole reason this
 * suite does not stub the worker.
 *
 * Everything else is about what the check is allowed to be: advice. The registry registers a
 * schema ajv has opinions about, so none of it may block, and the test that proves it asks the
 * registry rather than assuming.
 */
const ENVIRONMENT = 'dev';
const SUBJECT = 'acme.e2e.OrderCreated';

async function open(page: Page): Promise<void> {
  await page.goto(`/subjects/${SUBJECT}/versions/new`);
  await waitForSubject(page);
}

const validate = (page: Page) => page.getByRole('button', { name: 'Validate' });
const sample = (page: Page) => page.getByLabel(/A message you expect to be valid/);
const register = (page: Page) => page.getByRole('button', { name: 'Register', exact: true });

test.describe('Validate', () => {
  test('checks a schema with ajv while the page itself still cannot evaluate strings', async ({
    page,
  }) => {
    const problems: string[] = [];
    page.on('console', (message) => {
      if (message.type() === 'error') {
        problems.push(message.text());
      }
    });
    page.on('pageerror', (error) => problems.push(error.message));

    await open(page);
    await setSchema(page, '{"type":"object","properties":{"id":{"type":"string"}}}');
    await validate(page).click();

    await expect(page.getByText(/Nothing to flag/)).toBeVisible();

    // The worker evaluated ajv's generated code, and the page could not have. The probe is a
    // script the page loads from its own origin, which `script-src 'self'` permits: running it
    // through `page.evaluate` would prove nothing, because the DevTools protocol is exempt from
    // the page's eval restriction and would report "allowed" under any policy.
    await page.route('**/csp-probe.js', (route) =>
      route.fulfill({
        contentType: 'text/javascript',
        body: 'window.__evalProbe = (() => { try { return new Function("return 1")() === 1 ? "allowed" : "odd"; } catch (e) { return e.name; } })();',
      }),
    );
    await page.addScriptTag({ url: '/csp-probe.js' });

    expect(
      await page.evaluate(() => (window as unknown as { __evalProbe: string }).__evalProbe),
    ).toBe('EvalError');
    expect(problems.filter((p) => !/Content Security Policy|Refused to evaluate/.test(p))).toEqual(
      [],
    );
  });

  test('flags what the metaschema objects to, where it is, and still lets the registry decide', async ({
    page,
  }) => {
    const schema = '{"type":"bogus"}';

    // The reason this is advice: the registry takes it.
    expect((await dryRun(await ownerCredential(), ENVIRONMENT, SUBJECT, schema)).status).toBe(200);

    await open(page);
    await setSchema(page, schema);
    await validate(page).click();

    const finding = page.locator('[data-severity="advice"]');

    await expect(finding).toHaveCount(1);
    await expect(finding).toContainText('/type');
    await expect(finding).toContainText('"object"');
    await expect(finding).toContainText('line 1, column 9');
    await expect(register(page)).toBeEnabled();
  });

  test('goes to the place a finding points at', async ({ page }) => {
    await open(page);
    await setSchema(
      page,
      '{\n  "type": "object",\n  "properties": {\n    "id": { "type": "bogus" }\n  }\n}',
    );
    await validate(page).click();

    await expect(page.locator('[data-severity="advice"]')).toHaveCount(1);

    // The bad value is on the fourth line, and the caret is wherever the typing left it.
    expect(await caretLine(page)).not.toBe(4);

    await page.locator('[data-severity="advice"]').getByRole('button').click();

    await expect.poll(() => caretLine(page)).toBe(4);
  });

  test('says it did not look, rather than that nothing was wrong, for another dialect', async ({
    page,
  }) => {
    await open(page);
    await setSchema(page, '{"$schema":"http://json-schema.org/draft-07/schema#","type":"object"}');
    await validate(page).click();

    // The registry refuses it, and ajv is not asked to guess at a dialect it was not given.
    await expect(page.locator('[data-severity="refusal"]')).toContainText('2020-12');
    await expect(page.getByText(/The schema was not checked/)).toBeVisible();
    await expect(page.getByText(/Nothing to flag/)).toHaveCount(0);
    await expect(register(page)).toBeDisabled();
  });

  test('hides an answer once the text has moved on', async ({ page }) => {
    await open(page);
    await setSchema(page, '{"type":"bogus"}');
    await validate(page).click();
    await expect(page.locator('[data-severity="advice"]')).toHaveCount(1);

    await setSchema(page, '{"type":"string"}');

    await expect(page.locator('[data-severity="advice"]')).toHaveCount(0);
    await expect(page.getByText(/changed since the last check/)).toBeVisible();
  });
});

test.describe('a sample message', () => {
  const SCHEMA =
    '{"type":"object","required":["id"],"properties":{"id":{"type":"string"},"qty":{"type":"integer","minimum":1}}}';

  test('conforms', async ({ page }) => {
    await open(page);
    await setSchema(page, SCHEMA);
    await page.getByText('Check a sample message').click();
    await sample(page).fill('{"id":"a","qty":2}');
    await validate(page).click();

    await expect(page.getByText('The sample message conforms to the schema.')).toBeVisible();
  });

  test('does not, and says where', async ({ page }) => {
    await open(page);
    await setSchema(page, SCHEMA);
    await page.getByText('Check a sample message').click();
    await sample(page).fill('{"qty":0}');
    await validate(page).click();

    await expect(page.getByText('The sample message does not conform:')).toBeVisible();
    await expect(page.getByText("must have required property 'id'")).toBeVisible();
    await expect(page.getByText('must be >= 1')).toBeVisible();
  });

  test('is not JSON', async ({ page }) => {
    await open(page);
    await setSchema(page, SCHEMA);
    await page.getByText('Check a sample message').click();
    await sample(page).fill('{"id":');
    await validate(page).click();

    await expect(page.getByText(/sample message is not well-formed JSON/)).toBeVisible();
  });

  test('cannot be judged against a reference only the registry resolves', async ({ page }) => {
    await open(page);
    await setSchema(page, '{"$ref":"concordat://dev/acme.Address/1"}');
    await page.getByText('Check a sample message').click();
    await sample(page).fill('{"street":"x"}');
    await validate(page).click();

    await expect(page.getByText(/could not be checked/)).toContainText(
      'concordat://dev/acme.Address/1',
    );
    await expect(page.getByText('The sample message conforms to the schema.')).toHaveCount(0);
  });

  test('is not part of what gets registered, and is not sent to the registry', async ({ page }) => {
    const bodies: string[] = [];
    page.on('request', (request) => {
      if (request.method() === 'POST') {
        bodies.push(request.postData() ?? '');
      }
    });

    await open(page);
    await setSchema(page, SCHEMA);
    await page.getByText('Check a sample message').click();
    await sample(page).fill('{"id":"super-secret-sample"}');
    await validate(page).click();
    await expect(page.getByText('The sample message conforms to the schema.')).toBeVisible();

    // Checking is entirely local: not one request leaves the browser for it.
    expect(bodies.filter((b) => b.includes('super-secret-sample'))).toEqual([]);
  });
});

test.describe('a schema that would hang the check', () => {
  test('is stopped, says so, and leaves the page and the next check working', async ({ page }) => {
    // `^(a+)+$` against a long run of `a` followed by a `b` backtracks exponentially. On the
    // page's thread that freezes the tab; in the worker it is something a timeout can end.
    await open(page);
    await setSchema(page, '{"type":"string","pattern":"^(a+)+$"}');
    await page.getByText('Check a sample message').click();
    await sample(page).fill('"' + 'a'.repeat(60) + 'b"');
    await validate(page).click();

    await expect(page.getByText(/Could not check here/)).toBeVisible({ timeout: 15_000 });
    await expect(page.getByText(/took too long/)).toBeVisible();
    await expect(page.getByText(/The registry will still judge the schema/)).toBeVisible();

    // The page never stopped answering: it can still be typed into...
    await setSchema(page, '{"type":"string"}');
    await sample(page).fill('"fine"');

    // ...and the next check gets a fresh worker rather than queueing behind the hung one.
    await validate(page).click();

    await expect(page.getByText('The sample message conforms to the schema.')).toBeVisible();
  });
});
