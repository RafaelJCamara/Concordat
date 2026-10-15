import { expect, test, type Page } from '@playwright/test';
import {
  createSubjectRaw,
  ensureEnvironment,
  ensureSubject,
  ownerCredential,
  purgeSubjectRaw,
  readAudit,
  readSubject,
  retireSubject,
} from './registry';

/**
 * Deleting a retired subject for good, against the real registry (ADR-046).
 *
 * <b>There is no screen that deletes</b>, so this is not a test of a button. It is the part of the
 * deletion a browser can show: the subject is gone from the list the page reads, the name is free again,
 * and the audit page names who removed it and how many versions went. Everything that decides whether the
 * deletion is allowed (the retired rule, contracts, services, references) is proved over real HTTP in the
 * .NET suite, where it can be arranged without a broker.
 *
 * <b>It leaves nothing behind but the audit trail</b>, which is the point of the trail: every subject it
 * makes is deleted again, so the permanent-subject count in `README.md` is unchanged, and the entries it
 * writes are named for the run.
 */
const RUN = Date.now();
const ENVIRONMENT = 'dev';
const NAMESPACE = `acme.e2e.purge.${RUN}`;

const GONE = `${NAMESPACE}.Gone`;
const ACTIVE = `${NAMESPACE}.StillActive`;

const searchBox = (page: Page) => page.getByRole('searchbox', { name: /Search subjects/ });

test.describe.configure({ mode: 'serial' });

test.describe('deleting a retired subject for good', () => {
  let credential = '';

  test.beforeAll(async () => {
    credential = await ownerCredential();

    await ensureEnvironment(credential, ENVIRONMENT);
    await ensureSubject(credential, ENVIRONMENT, GONE);
    await ensureSubject(credential, ENVIRONMENT, ACTIVE);
    await retireSubject(credential, ENVIRONMENT, GONE);
  });

  test.afterAll(async () => {
    // The subject that was never meant to go, retired and deleted so the run leaves none behind. A
    // failed test must not leave it, and a re-run of the same name is impossible, so this is cleanup and
    // not setup.
    const view = await readSubject(credential, ENVIRONMENT, ACTIVE).catch(() => null);

    if (view !== null) {
      if (view.lifecycle !== 'RETIRED') {
        await retireSubject(credential, ENVIRONMENT, ACTIVE);
      }

      await purgeSubjectRaw(credential, ENVIRONMENT, ACTIVE);
    }

    await purgeSubjectRaw(credential, ENVIRONMENT, GONE);
  });

  test('refuses without a flag that says it is meant, and keeps the subject', async ({ page }) => {
    for (const force of [null, 'false', 'maybe']) {
      const answer = await purgeSubjectRaw(credential, ENVIRONMENT, GONE, force);

      expect(answer.status, `force=${String(force)}`).toBe(400);
    }

    const absent = await purgeSubjectRaw(credential, ENVIRONMENT, GONE, null);
    expect(absent.code).toBe('purge_requires_force');

    // Still there: a retired subject is listed, as it always was.
    await page.goto('/subjects');
    await searchBox(page).fill(GONE);
    await expect(page.getByRole('link', { name: GONE, exact: true })).toBeVisible();
  });

  test('refuses a subject that is not retired, and keeps it', async () => {
    const answer = await purgeSubjectRaw(credential, ENVIRONMENT, ACTIVE);

    expect(answer.status).toBe(409);
    expect(answer.code).toBe('subject_not_retired');
    expect((await readSubject(credential, ENVIRONMENT, ACTIVE)).lifecycle).toBe('ACTIVE');
  });

  test('deletes the retired subject, and the list the page reads no longer has it', async ({
    page,
  }) => {
    const answer = await purgeSubjectRaw(credential, ENVIRONMENT, GONE);
    expect(answer.status).toBe(200);

    await page.goto('/subjects');
    await searchBox(page).fill(GONE);

    await expect(page.getByText('No subject names start with')).toBeVisible();
    await expect(page.getByRole('link', { name: GONE, exact: true })).toHaveCount(0);

    // A page that was open on it answers as for any name that does not exist.
    await page.goto(`/subjects/${GONE}`);
    await expect(page.getByRole('heading', { name: `Could not load ${GONE}` })).toBeVisible();
  });

  test('says so in the audit log, with who and how many versions, and the history is still there', async ({
    page,
  }) => {
    const rows = await readAudit(credential, ENVIRONMENT, GONE);
    const purged = rows.filter((r) => r.action === 'SUBJECT_PURGED');

    expect(purged).toHaveLength(1);
    expect(purged[0]!.detail).toBe('1 version removed');
    // What it removed is still on record: created and retired before.
    expect(rows.map((r) => r.action)).toEqual(
      expect.arrayContaining(['SUBJECT_CREATED', 'SUBJECT_RETIRED']),
    );

    await page.goto('/audit');
    await page.getByLabel('Target').fill(GONE);
    await page.getByLabel('Action').selectOption('SUBJECT_PURGED');
    await page.getByRole('button', { name: 'Apply' }).click();

    const row = page.getByRole('row').filter({ hasText: GONE });
    await expect(row).toHaveCount(1);
    await expect(row).toContainText('SUBJECT_PURGED');
    await expect(row).toContainText('1 version removed');
  });

  test('frees the name, and a subject made with it starts again', async () => {
    const again = await createSubjectRaw(credential, ENVIRONMENT, {
      name: GONE,
      format: 'json',
      owner: 'e2e',
    });

    expect(again.status).toBe(201);

    const view = await readSubject(credential, ENVIRONMENT, GONE);
    expect(view.versions).toHaveLength(0);
    expect(view.lifecycle).toBe('ACTIVE');
  });

  test('a subject that is gone is not found a second time', async () => {
    // Retire what the previous test made, and delete it: afterAll tries once more, which must be harmless.
    await retireSubject(credential, ENVIRONMENT, GONE);
    expect((await purgeSubjectRaw(credential, ENVIRONMENT, GONE)).status).toBe(200);

    const second = await purgeSubjectRaw(credential, ENVIRONMENT, GONE);

    expect(second.status).toBe(404);
    expect(second.code).toBe('subject_not_found');
  });
});
