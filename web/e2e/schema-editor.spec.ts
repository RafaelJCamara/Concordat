import { expect, test } from '@playwright/test';
import {
  ownerCredential,
  readSchemaText,
  readSubject,
  registerVersion,
  schemaIdOf,
  ensureEmptySubject,
} from './registry';
import { schemaText, setSchema, waitForSubject } from './schema-editor';

/**
 * The schema editor's own behaviour, against the real registry.
 *
 * Every assertion here is about something the registry can confirm or contradict: the text
 * that is loaded is the text the registry serves, a formatted document gets the id the
 * unformatted one did, a label is accepted or refused by the registry's own rule, and "the
 * first click does not register" is checked by counting versions through the API rather than by
 * looking at the screen.
 *
 * Read-only against the shared fixtures. Anything that registers does so against a subject
 * created for the run (`acme.e2e.editor.<timestamp>`), because a version cannot be undone.
 */
const RUN = Date.now();
const ENVIRONMENT = 'dev';
const SHARED = 'acme.e2e.OrderCreated';
const EMPTY = 'acme.e2e.parity.json';
const OWN = `acme.e2e.editor.${RUN}`;

const BASE = '{"type":"object","properties":{"id":{"type":"string"}}}';
const BREAKING =
  '{"type":"object","properties":{"id":{"type":"string"},"total":{"type":"number"}},"required":["total"]}';
const COMPATIBLE =
  '{"type":"object","properties":{"id":{"type":"string"},"note":{"type":"string"}}}';

test.describe('starting from an earlier version', () => {
  test('says there are no versions yet, and offers nothing to load', async ({ page }) => {
    await ensureEmptySubject(await ownerCredential(), ENVIRONMENT, EMPTY, 'json');
    await page.goto(`/subjects/${EMPTY}/versions/new`);
    await waitForSubject(page);

    await expect(page.getByText('No versions yet')).toBeVisible();
    await expect(page.getByText(/this will be/i)).toContainText('v1');
    await expect(page.getByLabel('Start from')).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Load into the editor' })).toHaveCount(0);
  });

  test('loads the current version exactly as the registry serves it', async ({ page }) => {
    const credential = await ownerCredential();
    const subject = await readSubject(credential, ENVIRONMENT, SHARED);

    await page.goto(`/subjects/${SHARED}/versions/new`);
    await waitForSubject(page);

    await expect(page.getByLabel('Start from')).toHaveValue(String(subject.latest));

    await page.getByRole('button', { name: 'Load into the editor' }).click();

    // Not the canonical form and not a re-serialisation: the registry's own text.
    const version = subject.versions.find((v) => v.ordinal === subject.latest)!;
    const served = await readSchemaText(credential, version.schemaId);

    await expect.poll(() => schemaText(page)).toBe(served);
  });

  test('asks before replacing what has been written, and keeps it if declined', async ({
    page,
  }) => {
    await page.goto(`/subjects/${SHARED}/versions/new`);
    await waitForSubject(page);

    await setSchema(page, '{"mine":true}');
    await page.getByRole('button', { name: 'Load into the editor' }).click();

    await expect(page.getByText('This replaces what you have written')).toBeVisible();
    expect(await schemaText(page)).toBe('{"mine":true}');

    await page.getByRole('button', { name: 'Keep mine' }).click();

    await expect(page.getByText('This replaces what you have written')).toHaveCount(0);
    expect(await schemaText(page)).toBe('{"mine":true}');
  });

  test('replaces it on the second click', async ({ page }) => {
    await page.goto(`/subjects/${SHARED}/versions/new`);
    await waitForSubject(page);

    await setSchema(page, '{"mine":true}');
    await page.getByRole('button', { name: 'Load into the editor' }).click();
    await page.getByRole('button', { name: 'Replace', exact: true }).click();

    await expect.poll(() => schemaText(page)).not.toBe('{"mine":true}');
    expect(await schemaText(page)).toContain('"properties"');
  });
});

test.describe('Format', () => {
  test('lays a document out without changing the schema it is', async ({ page }) => {
    // The registry hashes number literals as written, so `1.0` and `1` are different schemas
    // and a value past 2^53 does not survive a double. A Format built on JSON.stringify(
    // JSON.parse(x)) would silently mint a different one — and the only way to know it did not
    // is to ask the registry what id each text gets.
    const credential = await ownerCredential();
    const compact =
      '{"type":"object","properties":{"price":{"type":"number","maximum":1.0,"minimum":-0,"multipleOf":0.10}},"maxLength":12345678901234567890}';

    await page.goto(`/subjects/${SHARED}/versions/new`);
    await waitForSubject(page);
    await setSchema(page, compact);
    await page.getByRole('button', { name: 'Format' }).click();

    // The editor takes the new text on the next render, not synchronously with the click.
    await expect.poll(() => schemaText(page)).not.toBe(compact);

    const formatted = await schemaText(page);

    expect(formatted.split('\n').length).toBeGreaterThan(5);
    for (const literal of ['1.0', '-0', '0.10', '12345678901234567890']) {
      expect(formatted).toContain(literal);
    }

    // Formatting only moves whitespace, so the registry — which canonicalises — must agree the two are one schema.
    expect(await schemaIdOf(credential, ENVIRONMENT, SHARED, formatted)).toBe(
      await schemaIdOf(credential, ENVIRONMENT, SHARED, compact),
    );
  });

  test('says why it did nothing for a document that does not read, and leaves it alone', async ({
    page,
  }) => {
    await page.goto(`/subjects/${SHARED}/versions/new`);
    await waitForSubject(page);

    await setSchema(page, '{"type": ');
    await page.getByRole('button', { name: 'Format' }).click();

    await expect(page.getByText(/nothing to lay out/i)).toBeVisible();
    expect(await schemaText(page)).toBe('{"type": ');
  });
});

test.describe('registering', () => {
  test.beforeAll(async () => {
    // v1 current at 1.0.0; v2 breaking, held at the gate, and labelled 2.0.0. `latest` says
    // 1.0.0 and the registry compares against 2.0.0 — the case where a suggestion seeded from
    // the pointer would be refused.
    const credential = await ownerCredential();

    await ensureEmptySubject(credential, ENVIRONMENT, OWN, 'json');
    expect((await registerVersion(credential, ENVIRONMENT, OWN, BASE, '1.0.0')).status).toBe(201);
    expect((await registerVersion(credential, ENVIRONMENT, OWN, BREAKING, '2.0.0')).status).toBe(
      201,
    );
  });

  test('seeds its label suggestions from the highest live label, not from `latest`', async ({
    page,
  }) => {
    const credential = await ownerCredential();
    const before = await readSubject(credential, ENVIRONMENT, OWN);

    // The premise, read from the registry rather than assumed: the pointer is on v1 and v2 is held.
    expect(before.latest).toBe(1);
    expect(before.versions.find((v) => v.ordinal === 2)?.status).toBe('AWAITING_APPROVAL');

    await page.goto(`/subjects/${OWN}/versions/new`);
    await waitForSubject(page);

    await expect(page.getByText('Highest live label')).toContainText('2.0.0');
    for (const next of ['2.0.1', '2.1.0', '3.0.0']) {
      await expect(page.getByRole('button', { name: next })).toBeVisible();
    }
    await expect(page.getByRole('button', { name: '1.0.1' })).toHaveCount(0);
  });

  test('the first click registers nothing', async ({ page }) => {
    const credential = await ownerCredential();
    const before = await readSubject(credential, ENVIRONMENT, OWN);

    await page.goto(`/subjects/${OWN}/versions/new`);
    await waitForSubject(page);
    await setSchema(page, COMPATIBLE);
    await page.getByRole('button', { name: 'Register', exact: true }).click();

    await expect(page.getByText(/register this as/i)).toContainText(
      `v${before.versions.length + 1}`,
    );
    await expect(page.getByText(/permanent/i)).toBeVisible();

    // Counted at the registry, not read off the screen: the claim is that nothing was sent.
    expect((await readSubject(credential, ENVIRONMENT, OWN)).versions).toHaveLength(
      before.versions.length,
    );

    await page.getByRole('button', { name: 'Keep editing' }).click();

    await expect(page.getByRole('button', { name: 'Register', exact: true })).toBeVisible();
    expect((await readSubject(credential, ENVIRONMENT, OWN)).versions).toHaveLength(
      before.versions.length,
    );
  });

  test('warns, then lets the registry refuse, a label that does not increase on a held one', async ({
    page,
  }) => {
    await page.goto(`/subjects/${OWN}/versions/new`);
    await waitForSubject(page);
    await setSchema(page, COMPATIBLE);
    await page.getByLabel(/Semantic version/).fill('1.0.1');

    // The client's advice, which does not block...
    await expect(page.getByText(/does not increase on 2\.0\.0/)).toBeVisible();
    await expect(page.getByRole('button', { name: 'Register', exact: true })).toBeEnabled();

    // ...and the registry's answer, which is the authority.
    await page.getByRole('button', { name: 'Register', exact: true }).click();
    await page.getByRole('button', { name: 'Yes, register it' }).click();

    await expect(page.getByRole('heading', { name: /registry refused/i })).toBeVisible();
    await expect(page.getByText(/does not increase on 2\.0\.0/).first()).toBeVisible();
  });

  test("registers with a suggested label, and comes back with the registry's next ordinal", async ({
    page,
  }) => {
    const credential = await ownerCredential();

    await page.goto(`/subjects/${OWN}/versions/new`);
    await waitForSubject(page);
    await setSchema(page, COMPATIBLE);
    await page.getByRole('button', { name: '2.0.1' }).click();
    await page.getByRole('button', { name: 'Register', exact: true }).click();
    await page.getByRole('button', { name: 'Yes, register it' }).click();

    await expect(page.getByText('Registered', { exact: true })).toBeVisible();

    const after = await readSubject(credential, ENVIRONMENT, OWN);
    const registered = after.versions.find((v) => v.semanticVersion === '2.0.1');

    expect(registered).toBeDefined();

    // "Register another" comes back with what the registry now says, not arithmetic on the
    // last answer: the next ordinal, and suggestions from the label that was just registered.
    await page.getByRole('button', { name: 'Register another' }).click();
    await waitForSubject(page);

    await expect(page.getByText(/next ordinal/i)).toContainText(`v${after.versions.length + 1}`);
    await expect(page.getByText('Highest live label')).toContainText('2.0.1');
    await expect(page.getByRole('button', { name: '2.0.2' })).toBeVisible();
    // The document stays, to iterate on; the label and changelog belonged to the version just
    // registered, and a label cannot be used twice.
    expect(await schemaText(page)).toBe(COMPATIBLE);
    await expect(page.getByLabel(/Semantic version/)).toHaveValue('');
  });
});
