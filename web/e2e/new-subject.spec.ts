import { expect, test, type Page, type Request } from '@playwright/test';
import {
  COMPATIBILITY_MODE_MEANING,
  COMPATIBILITY_MODES,
  COMPATIBILITY_SURFACE_MEANING,
  COMPATIBILITY_SURFACES,
} from '../src/app/domain/registry/compatibility';
import { expectNoSidewaysOverflow, openByKeyboard } from './compatibility-help';
import { ensureEnvironment, OWNER, ownerCredential, readSubject } from './registry';
import { setSchema, waitForSubject } from './schema-editor';

/**
 * The create-subject screen, against the real registry.
 *
 * M4's exit criterion is "create a subject, register a version, view a diff and an impact report,
 * and approve a pending breaking change — entirely in the UI", and the first step was the one
 * nothing did. The first test is that criterion's first half, end to end, with nothing arranged
 * over HTTP: a subject made on this screen and a version registered on the next.
 *
 * <b>Every subject made here is permanent</b> — there is no delete, a retired name stays taken —
 * so each is named for the run (`acme.e2e.created.<stem>.<timestamp>`) and a run leaves a few
 * behind in `dev`. That is the cost of testing the one action that cannot be undone, and
 * `README.md` says so. Nothing here ever creates a name another spec relies on, and the
 * existing-name cases create nothing at all: the registry answers 409 and holds the line.
 *
 * Name rules are not exercised here. `subject-name-parity.spec.ts` holds every one of them against
 * the registry; this file is about the screen — what it asks, what it sends, what it shows, and
 * that it never shows anything the registry has not said.
 */
const RUN = Date.now();
const ENVIRONMENT = 'dev';
const SHARED = 'acme.e2e.OrderCreated';
const EMPTY_ENVIRONMENT = 'e2e-empty';

const named = (stem: string) => `acme.e2e.created.${stem}.${RUN}`;

const BASE = '{"type":"object","properties":{"id":{"type":"string"}}}';

async function openForm(page: Page): Promise<void> {
  await page.goto('/subjects/new-subject');
  await expect(page.getByRole('heading', { name: 'New subject' })).toBeVisible();
}

/** Watches for the one request that creates a subject. */
function creates(page: Page): Request[] {
  const seen: Request[] = [];

  page.on('request', (request) => {
    if (request.method() === 'POST' && /\/v1\/environments\/[^/]+\/subjects$/.test(request.url())) {
      seen.push(request);
    }
  });

  return seen;
}

test.describe('creating a subject', () => {
  test('from the list, to a registered version, entirely in the browser', async ({ page }) => {
    const name = named('journey');
    const credential = await ownerCredential();
    const sent = creates(page);

    await page.goto('/subjects');
    await page.getByRole('link', { name: 'New subject' }).click();
    await expect(page).toHaveURL(/\/subjects\/new-subject$/);

    // The title suggests a name, and the name follows it...
    await page.getByLabel('Title').fill('Created in the browser');
    await expect(page.getByLabel('Subject name')).toHaveValue('Created_in_the_browser');
    await expect(page.getByText('Following the title')).toBeVisible();

    // ...until it is edited by hand, and then the title no longer moves it.
    await page.getByLabel('Subject name').fill(name);
    await page.getByLabel('Title').fill('Something else entirely');
    await expect(page.getByLabel('Subject name')).toHaveValue(name);
    await expect(page.getByRole('button', { name: /from the title instead/ })).toBeVisible();

    // The owner is the caller, until somebody says otherwise.
    await expect(page.getByLabel('Owner')).toHaveValue(OWNER.email);

    // The first click asks, and nothing exists yet: not on the screen, not in the registry.
    await page.getByRole('button', { name: 'Create subject' }).click();
    await expect(page.getByText(`Create ${name} in ${ENVIRONMENT}?`)).toBeVisible();
    await expect(page.getByText('The name is permanent.')).toBeVisible();
    await expect(
      page.getByText(/cannot be renamed, and a retired one keeps its name taken/),
    ).toBeVisible();
    await expect(page.getByText(/can be changed later, from the subject’s own page/)).toBeVisible();
    await expect(page.getByText(/Changing the policy takes an admin/)).toBeVisible();
    await expect(page.getByText(/only through the API/)).toHaveCount(0);
    expect(sent).toHaveLength(0);
    await expect(readSubject(credential, ENVIRONMENT, name)).rejects.toThrow(/404/);

    // The second creates it, and lands on the subject the registry answered with.
    await page.getByRole('button', { name: 'Yes, create it' }).click();
    await expect(page).toHaveURL(new RegExp(`/subjects/${name.replaceAll('.', '\\.')}$`));
    expect(sent).toHaveLength(1);

    // What the page says now is the registry's account, read afresh by the page it landed on:
    // an empty subject, with the register action one click away.
    await expect(page.getByText('No versions yet')).toBeVisible();
    const view = await readSubject(credential, ENVIRONMENT, name);

    expect(view).toMatchObject({
      name,
      format: 'json',
      owner: OWNER.email,
      lifecycle: 'ACTIVE',
      contentModel: 'OPEN',
      compatibilityPolicy: { mode: null, surface: null },
      latest: null,
    });
    expect(view.versions).toEqual([]);

    // The criterion's second step, from where creation left off.
    await page.getByRole('link', { name: 'New version' }).click();
    await waitForSubject(page);
    await setSchema(page, BASE);
    await page.getByRole('button', { name: 'Register', exact: true }).click();
    await page.getByRole('button', { name: 'Yes, register it' }).click();
    await expect(page.getByText('Registered', { exact: true })).toBeVisible();

    const registered = await readSubject(credential, ENVIRONMENT, name);

    expect(registered.versions).toHaveLength(1);
    expect(registered.versions[0]?.status).toBe('ACTIVE');
    expect(registered.latest).toBe(1);
  });

  test('sends exactly what was chosen, and shows what the registry stored', async ({ page }) => {
    const name = named('chosen');
    const credential = await ownerCredential();
    const sent = creates(page);

    await openForm(page);

    // Padding, because the registry trims before it judges and the page must go where it was told.
    await page.getByLabel('Subject name').fill(`  ${name}  `);
    await page.getByRole('radio', { name: 'Avro' }).check();
    await page.getByLabel('Owner').fill('team-e2e');
    await page.getByRole('radio', { name: /Closed/ }).check();
    await page.getByLabel('Mode').selectOption('FULL_TRANSITIVE');
    await page.getByLabel('Surface').selectOption('SOURCE');

    await page.getByRole('button', { name: 'Create subject' }).click();
    await expect(page.getByText('Avro, closed')).toBeVisible();
    await page.getByRole('button', { name: 'Yes, create it' }).click();

    // The trimmed name, which is the stored one: opening what was typed would be a 404.
    await expect(page).toHaveURL(new RegExp(`/subjects/${name.replaceAll('.', '\\.')}$`));

    expect(sent).toHaveLength(1);
    expect(sent[0]?.postDataJSON()).toEqual({
      name,
      format: 'avro',
      owner: 'team-e2e',
      contentModel: 'CLOSED',
      compatibilityMode: 'FULL_TRANSITIVE',
      compatibilitySurface: 'SOURCE',
    });

    expect(await readSubject(credential, ENVIRONMENT, name)).toMatchObject({
      name,
      format: 'avro',
      owner: 'team-e2e',
      contentModel: 'CLOSED',
      compatibilityPolicy: { mode: 'FULL_TRANSITIVE', surface: 'SOURCE' },
    });

    // And the subject page reads the same account back.
    await expect(page.getByText('FULL_TRANSITIVE', { exact: true })).toBeVisible();
    await expect(page.getByText('CLOSED', { exact: true })).toBeVisible();

    // The subject page explains the policy it shows, in the words the domain holds for exactly
    // these two tokens and for no others, and opening it does not add a second copy of either
    // token to a page that finds them by their exact text.
    const guide = page.getByRole('button', { name: 'What do these mean?' });
    await openByKeyboard(page, guide);
    const panel = page.locator(`#${await guide.getAttribute('aria-controls')}`);
    await expect(panel.locator('dt')).toHaveText(['FULL_TRANSITIVE', 'SOURCE']);
    await expect(panel.locator('dd')).toHaveText([
      COMPATIBILITY_MODE_MEANING.FULL_TRANSITIVE,
      COMPATIBILITY_SURFACE_MEANING.SOURCE,
    ]);
  });

  test('says what each policy choice means, under the select, and creates nothing', async ({
    page,
  }) => {
    // At the width a phone has, where the prototype's hover tooltip did not exist. The sentence
    // is a description of the select, not part of its name: `getByLabel('Mode')` still finds
    // exactly one select, which is how every test here reaches it.
    await page.setViewportSize({ width: 390, height: 844 });
    const sent = creates(page);
    await openForm(page);

    const mode = page.getByLabel('Mode');
    const surface = page.getByLabel('Surface');
    await expect(mode).toHaveCount(1);
    await expect(surface).toHaveCount(1);

    // Inherit says nothing of its own: it is not a mode.
    await expect(mode).toHaveAccessibleDescription('');

    await mode.selectOption('BACKWARD_TRANSITIVE');
    await surface.selectOption('WIRE');

    await expect(mode).toHaveAccessibleDescription(COMPATIBILITY_MODE_MEANING.BACKWARD_TRANSITIVE);
    await expect(surface).toHaveAccessibleDescription(COMPATIBILITY_SURFACE_MEANING.WIRE);

    // And back: the sentence follows the choice, it is not left behind by it.
    await mode.selectOption('');
    await expect(mode).toHaveAccessibleDescription('');

    // The whole vocabulary, from the keyboard, without leaving the form or scrolling sideways.
    const guide = page.getByRole('button', { name: 'What do these mean?' });
    await openByKeyboard(page, guide);
    const panel = page.locator(`#${await guide.getAttribute('aria-controls')}`);
    await expect(panel.locator('dt')).toHaveText([
      ...COMPATIBILITY_MODES,
      ...COMPATIBILITY_SURFACES,
    ]);
    await expectNoSidewaysOverflow(page, panel.locator('dt, dd'));

    expect(sent).toHaveLength(0);
  });

  test('withdraws the confirmation when what it named is edited', async ({ page }) => {
    const credential = await ownerCredential();
    const first = named('withdrawn');
    const sent = creates(page);

    await openForm(page);
    await page.getByLabel('Subject name').fill(first);
    await page.getByRole('button', { name: 'Create subject' }).click();
    await expect(page.getByRole('button', { name: 'Yes, create it' })).toBeVisible();

    await page.getByLabel('Subject name').fill(`${first}.edited`);
    await expect(page.getByRole('button', { name: 'Yes, create it' })).toHaveCount(0);
    expect(sent).toHaveLength(0);
    await expect(readSubject(credential, ENVIRONMENT, first)).rejects.toThrow(/404/);
  });

  test('says what is wrong with a name and sends nothing', async ({ page }) => {
    const sent = creates(page);

    await openForm(page);
    await page.getByLabel('Subject name').fill('order-created');

    await expect(page.getByText(/'-' \(U\+002D\) is not allowed/)).toBeVisible();
    await expect(page.getByRole('button', { name: 'Create subject' })).toBeDisabled();
    await expect(page.getByLabel('Subject name')).toHaveAttribute('aria-invalid', 'true');
    expect(sent).toHaveLength(0);
  });

  test('blocks a half-chosen compatibility policy, which the registry answers 400', async ({
    page,
  }) => {
    await openForm(page);
    await page.getByLabel('Subject name').fill(named('half'));
    await page.getByLabel('Mode').selectOption('BACKWARD');

    await expect(page.getByText(/Choose a surface as well as a mode/)).toBeVisible();
    await expect(page.getByRole('button', { name: 'Create subject' })).toBeDisabled();

    await page.getByLabel('Surface').selectOption('WIRE_JSON');
    await expect(page.getByRole('button', { name: 'Create subject' })).toBeEnabled();
  });

  test('warns that a name is taken, still lets the registry decide, and offers the way out', async ({
    page,
  }) => {
    const credential = await ownerCredential();
    const before = await readSubject(credential, ENVIRONMENT, SHARED);

    await openForm(page);
    await page.getByLabel('Subject name').fill(SHARED);

    // Advice from the list the page read: shown, and not a block.
    await expect(page.getByText(/already in this environment/)).toBeVisible();
    await expect(page.getByRole('button', { name: 'Create subject' })).toBeEnabled();

    // The registry is the authority, and it answers 409 for a name that is there.
    await page.getByRole('button', { name: 'Create subject' }).click();
    await page.getByRole('button', { name: 'Yes, create it' }).click();

    await expect(page.getByText('The registry refused this subject')).toBeVisible();
    await expect(page.getByText(/already exists/)).toBeVisible();

    // Nothing was created and nothing changed, and the way on is to open what is there.
    await page.getByRole('link', { name: new RegExp(`Open ${SHARED}`) }).click();
    await expect(page).toHaveURL(new RegExp(`/subjects/${SHARED.replaceAll('.', '\\.')}$`));
    expect(await readSubject(credential, ENVIRONMENT, SHARED)).toEqual(before);
  });

  test('notices a name that differs from an existing one only in case', async ({ page }) => {
    await openForm(page);
    await page.getByLabel('Subject name').fill(SHARED.toLowerCase());

    await expect(page.getByText(/differs from this name only in case/)).toBeVisible();
    await expect(page.getByText(/separate subject/)).toBeVisible();
    await expect(page.getByRole('button', { name: 'Create subject' })).toBeEnabled();
  });
});

// The empty states, which no shared fixture can show: `dev` is never empty. This points the app at
// an environment that is, by answering `config.json` — the one request stubbed, and the one the
// deployment itself supplies — and everything else is the real registry. Nothing here creates a
// subject: `e2e-empty` stays empty, which is the only reason it can be used.
test.describe('an empty environment', () => {
  test.beforeAll(async () => {
    await ensureEnvironment(await ownerCredential(), EMPTY_ENVIRONMENT);
  });

  test.beforeEach(async ({ page }) => {
    await page.route('**/config.json', (route) =>
      route.fulfill({
        contentType: 'application/json',
        body: JSON.stringify({ defaultEnvironment: EMPTY_ENVIRONMENT }),
      }),
    );
  });

  test('the subject list offers the first subject', async ({ page }) => {
    await page.goto('/subjects');

    await expect(page.getByText('No subjects yet')).toBeVisible();
    await page.getByRole('link', { name: 'Create the first subject' }).click();
    await expect(page).toHaveURL(/\/subjects\/new-subject$/);
    await expect(page.getByText(EMPTY_ENVIRONMENT, { exact: true }).first()).toBeVisible();
  });

  test('so does the dashboard', async ({ page }) => {
    await page.goto('/');

    await expect(page.getByText('No subjects yet')).toBeVisible();
    await page.getByRole('link', { name: 'Create the first subject' }).click();
    await expect(page).toHaveURL(/\/subjects\/new-subject$/);
  });

  test('the form names the environment it would create in', async ({ page }) => {
    await openForm(page);
    await page.getByLabel('Subject name').fill(named('nevercreated'));
    await page.getByRole('button', { name: 'Create subject' }).click();

    // Asked, and not answered: this environment has to stay empty.
    await expect(
      page.getByText(`Create ${named('nevercreated')} in ${EMPTY_ENVIRONMENT}?`),
    ).toBeVisible();
  });
});
