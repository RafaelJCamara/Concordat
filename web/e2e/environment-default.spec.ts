import { expect, test } from '@playwright/test';
import {
  checkSchema,
  createSubjectRaw,
  ensureEnvironmentWithDefault,
  ownerCredential,
  readSubject,
  registerVersion,
  setEnvironmentDefault,
} from './registry';

/**
 * A subject with no policy of its own follows its environment's default, and the settings screen
 * is where that default is changed (ADR-033).
 *
 * <b>The registry used to ignore it.</b> Registration, the dry-run check and the diff judged an
 * inheriting subject by the registry's built-in `BACKWARD · WIRE_JSON` whatever its environment's
 * default was, so the settings screen's "takes effect at once for every subject that has not set
 * its own" was false at the one place it mattered, and a breaking version in an environment set to
 * `NONE` was held. This spec drives the real screen and then asks the real registry.
 *
 * <b>It changes nothing shared.</b> It owns an environment, `e2e-follows`, created once with a
 * default chosen at creation, and no other spec may point at it. The one default it moves is that
 * environment's own, and it puts it back before it starts, which is what makes a second run start
 * where the first one did. `dev`'s default (and `e2e-inherit`'s, which `subject-settings.spec.ts`
 * measures and must never move) is never touched. The two subjects a run makes are permanent, as
 * every subject is, and `README.md` counts them.
 *
 * <b>The environment settings screen edits the environment the app is aimed at</b>, so this aims it
 * by answering `config.json`, the one request `new-subject.spec.ts` stubs for the same reason.
 */
const ENVIRONMENT = 'e2e-follows';
const STRICT = { mode: 'BACKWARD', surface: 'WIRE_JSON' } as const;
const NEXT = { mode: 'NONE', surface: 'WIRE' } as const;
const RUN = Date.now();

const named = (stem: string) => `acme.e2e.follows.${stem}.${RUN}`;
const schema = (type: string) =>
  JSON.stringify({ type: 'object', properties: { id: { type } }, required: ['id'] });

const inheriting = named('inheriting');
const pinned = named('pinned');

test.describe('changing an environment’s default on the settings screen', () => {
  test.describe.configure({ mode: 'serial' });

  test.beforeAll(async () => {
    const credential = await ownerCredential();

    await ensureEnvironmentWithDefault(credential, ENVIRONMENT, STRICT);
    // A previous run left it on NONE. Its own environment, so this is arrangement and not a change
    // to anything shared.
    await setEnvironmentDefault(credential, ENVIRONMENT, STRICT);

    for (const [name, policy] of [
      [inheriting, {}],
      [pinned, { compatibilityMode: 'BACKWARD', compatibilitySurface: 'WIRE_JSON' }],
    ] as const) {
      const created = await createSubjectRaw(credential, ENVIRONMENT, {
        name,
        format: 'json',
        owner: 'arranged-by-e2e',
        ...policy,
      });

      expect(created.status, `arranging ${name}`).toBe(201);
      expect((await registerVersion(credential, ENVIRONMENT, name, schema('string'))).status).toBe(
        201,
      );
    }
  });

  test.beforeEach(async ({ page }) => {
    await page.route('**/config.json', (route) =>
      route.fulfill({
        contentType: 'application/json',
        body: JSON.stringify({ defaultEnvironment: ENVIRONMENT }),
      }),
    );
  });

  test('before it changes, a breaking version of either subject is held', async () => {
    const credential = await ownerCredential();

    for (const name of [inheriting, pinned]) {
      const held = await registerVersion(credential, ENVIRONMENT, name, schema('integer'));

      expect(held.body, name).toMatchObject({ status: 'AWAITING_APPROVAL' });
    }

    // And the dry run says the same about the next one, under the environment's own default.
    const check = await checkSchema(credential, ENVIRONMENT, inheriting, schema('boolean'));

    expect(check.compatible).toBe(false);
    expect(check.policy).toEqual(STRICT);
  });

  test('changing only one of the two members is refused, and the screen says so', async ({
    page,
  }) => {
    // A defect, pinned so it cannot go stale in silence: the form sends only the member that
    // changed, and the registry refuses half a policy (ADR-016). Nothing changes. When the form
    // sends the pair this goes red, and the test below should then pick one select only.
    await page.goto('/settings/environment');
    await page.getByLabel('Default compatibility mode').selectOption('FULL');
    await page.getByRole('button', { name: 'Save changes' }).click();

    await expect(page.getByText('Could not save the settings')).toBeVisible();
    await expect(page.getByText(/needs both a mode and a surface/)).toBeVisible();

    const check = await checkSchema(
      await ownerCredential(),
      ENVIRONMENT,
      inheriting,
      schema('boolean'),
    );

    expect(check.policy).toEqual(STRICT);
  });

  test('saves a new default, and the screen says what that does and does not touch', async ({
    page,
  }) => {
    const credential = await ownerCredential();

    await page.goto('/settings/environment');
    await expect(page.locator('dt', { hasText: 'Default compatibility' })).toBeVisible();
    await expect(page.getByLabel('Default compatibility mode')).toHaveValue('BACKWARD');

    // The sentence beside the control, which is what this spec makes true: the next version is
    // judged by the new default, and what is already registered or held is not.
    await expect(
      page.getByText('the next version registered, checked or compared is judged by it'),
    ).toBeVisible();
    await expect(
      page.getByText('Versions already registered, or held at the approval gate'),
    ).toBeVisible();

    // Both members, not the one the person means to change. The screen sends only what changed and
    // the registry refuses half a policy (see the next test), so a default is changed by choosing a
    // whole one. Moving to NONE × WIRE, which is also not the built-in surface, makes the policy
    // that judges the subject differ from the built-in one on both axes.
    await page.getByLabel('Default compatibility mode').selectOption('NONE');
    await page.getByLabel('Default compatibility surface').selectOption('WIRE');
    await page.getByRole('button', { name: 'Save changes' }).click();
    await expect(page.getByRole('status')).toContainText('Settings saved');

    await page.reload();
    await expect(page.getByLabel('Default compatibility mode')).toHaveValue('NONE');
    await expect(page.getByLabel('Default compatibility surface')).toHaveValue('WIRE');

    // The registry agrees, and it is the environment's own default that moved.
    const check = await checkSchema(credential, ENVIRONMENT, inheriting, schema('boolean'));

    expect(check.policy).toEqual(NEXT);
  });

  test('then the next version of an inheriting subject is judged by it, and a pinned one is not', async () => {
    const credential = await ownerCredential();

    // The dry run is a rehearsal of the registration, so it must agree with what follows.
    const check = await checkSchema(credential, ENVIRONMENT, inheriting, schema('boolean'));

    expect(check.compatible).toBe(true);

    const followed = await registerVersion(credential, ENVIRONMENT, inheriting, schema('boolean'));
    const kept = await registerVersion(credential, ENVIRONMENT, pinned, schema('boolean'));

    expect(followed.body).toMatchObject({ status: 'ACTIVE' });
    expect(kept.body).toMatchObject({ status: 'AWAITING_APPROVAL' });
  });

  test('and what was held before it changed is still held', async () => {
    const view = await readSubject(await ownerCredential(), ENVIRONMENT, inheriting);

    expect(view.versions.map((v) => [v.ordinal, v.status])).toEqual([
      [1, 'ACTIVE'],
      [2, 'AWAITING_APPROVAL'],
      [3, 'ACTIVE'],
    ]);
    expect(view.compatibilityPolicy).toEqual({ mode: null, surface: null });
  });
});
