import { expect, test, type Browser, type Page } from '@playwright/test';
import { READER_STORAGE_STATE } from './global-setup';
import {
  createMemberRaw,
  OWNER,
  ownerCredential,
  readAccountAudit,
  readMember,
  setDisabledRaw,
  statusOf,
  type MemberRow,
} from './registry';
import { signIn } from './sign-in';

/**
 * An account's life, in a real browser against a real registry (ADR-048): having a password reset by an
 * administrator and being made to replace it, changing it again by choice, and being disabled and enabled.
 *
 * <b>Each confirmation's claims are checked against the registry here, as the ADR says they were
 * measured.</b> The screens say: a reset leaves the member a session that can do nothing but change the
 * password; a disabling revokes what the member holds, at once; the audit log gets one entry naming the
 * account that did it and no password. Each is asked of the registry after the click, from outside the
 * browser, and a screen that says more or less than the registry did fails here. (That a change signs out
 * the member's OTHER sessions, that a reset signs out their sessions, and that API keys survive a change
 * and a reset and die with a disabling, is measured by the .NET HTTP suite, which can arrange a second
 * session and a key for nothing; this one would spend a sign-in on each, and the limiter allows 20.)
 *
 * <b>One throwaway member, for the whole story, and never the shared accounts.</b> The owner and reader
 * this suite signs in as are not touched: changing their passwords, disabling them or resetting one
 * would break every spec after. The member is an ADMIN, deliberately and never an OWNER: a second
 * enabled owner would let `identity.spec.ts`'s "will not let an owner demote themselves" demote the
 * shared owner for real. Members cannot be deleted, so the member is **disabled in `afterAll`**, which
 * leaves no working credential behind, and a run leaves one more disabled row in Settings → Members.
 *
 * <b>Sign-in budget:</b> one sign-in through the form (the forced one) and one refused password change,
 * against the limiter's 20 attempts per five minutes. The first version of this spec made six and the last
 * two tests of `session.spec.ts` failed with `429`; the second made four, which left no room for CI's
 * retry. See the README.
 */
const RUN = Date.now();
const MEMBER_EMAIL = `e2e-account-${RUN}@concordat.test`;
const FIRST = 'the first password 1';
const TEMPORARY = 'a temporary password 2';
const SECOND = 'the second password 3';
const THIRD = 'the third password 4';

let member: MemberRow;

/** A browser with nobody signed in, signed in as the throwaway member. */
async function memberPage(browser: Browser, password: string): Promise<Page> {
  const context = await browser.newContext();
  const page = await context.newPage();

  await signIn(page, { email: MEMBER_EMAIL, password });

  return page;
}

function memberRow(page: Page) {
  return page.getByRole('listitem').filter({ hasText: MEMBER_EMAIL });
}

async function notices(page: Page, text: string | RegExp): Promise<void> {
  await expect(page.getByRole('status')).toContainText(text);
}

test.describe('a reader', () => {
  test.use({ storageState: READER_STORAGE_STATE });

  test('has an Account tab and no Members tab, and a refused password change does not sign them out', async ({
    page,
  }) => {
    await page.goto('/settings/account');

    const tabs = page.getByRole('navigation', { name: 'Settings' });

    await expect(tabs.getByRole('link', { name: 'Account' })).toHaveAttribute(
      'aria-current',
      'page',
    );
    await expect(tabs.getByRole('link', { name: 'Members' })).toHaveCount(0);

    // A wrong current password, which the registry answers 401 exactly as it answers a wrong
    // sign-in. The page says so and the session stays: the reload is the proof.
    await page.getByLabel('Current password').fill('not the password at all');
    await page.getByLabel('New password', { exact: true }).fill('some new password 9');
    await page.getByLabel('Repeat the new password').fill('some new password 9');
    await page.getByRole('button', { name: 'Review change' }).click();
    await page.getByRole('button', { name: 'Yes, change my password' }).click();

    await expect(page.getByRole('alert')).toContainText('That is not your current password.');
    await expect(page.getByLabel('Current password')).toHaveValue('');

    await page.reload();
    await expect(
      page.locator('cd-app-sidebar').getByRole('button', { name: /sign out/i }),
    ).toBeVisible();
    await expect(page).toHaveURL(/\/settings\/account$/);
  });

  test('is never offered the account actions on the members screen, which is not theirs to open', async ({
    page,
  }) => {
    await page.goto('/settings/members');

    await expect(page).toHaveURL(/\/subjects$/);
    await expect(page.getByRole('button', { name: 'Reset password…' })).toHaveCount(0);
  });
});

test.describe.serial('a member, from a reset password to disabled', () => {
  /** The member's tab: the forced session first, then each session after it. */
  let signedInHere: Page;
  // What the tab held, taken while it was good, so that afterwards the registry can be asked about the
  // credential itself and not only about what the page does.
  let heldBeforeDisable = '';

  test.beforeAll(async () => {
    member = await createMemberRaw(await ownerCredential(), MEMBER_EMAIL, FIRST, 'ADMIN');
  });

  // The member is never left able to sign in, whatever happened above. Members cannot be deleted.
  test.afterAll(async () => {
    const credential = await ownerCredential();

    if (!(await readMember(credential, MEMBER_EMAIL)).disabled) {
      await setDisabledRaw(credential, member.userId, true);
    }
  });

  test('an administrator resets a password behind two clicks, and the screen says what that does', async ({
    page,
  }, testInfo) => {
    await page.goto('/settings/members');

    // Your own row offers neither, and points at the account screen.
    const own = page.getByRole('listitem').filter({ hasText: OWNER.email });
    await expect(own.getByRole('button', { name: 'Reset password…' })).toHaveCount(0);
    await expect(own.getByRole('button', { name: 'Disable…' })).toHaveCount(0);
    await expect(own).toContainText('This is you');

    const row = memberRow(page);
    await row.getByRole('button', { name: 'Reset password…' }).click();

    const field = row.getByLabel(`Temporary password for ${MEMBER_EMAIL}`);
    await expect(field).toHaveAttribute('autocomplete', 'new-password');

    // Under twelve characters is refused before anything is sent.
    await field.fill('too short');
    await expect(row.getByRole('button', { name: 'Review change' })).toBeDisabled();
    await expect(row).toContainText('at least 12 characters');

    await field.fill(TEMPORARY);
    await row.getByRole('button', { name: 'Review change' }).click();

    const review = row.getByRole('group', { name: `Reset the password for ${MEMBER_EMAIL}?` });
    await expect(review).toContainText('their session can do nothing but change it');
    await expect(review).toContainText('Every session they have is signed out now');
    await expect(review).toContainText('Their API keys are not touched');
    await expect(review).toContainText('does not send it');
    await expect(review).toContainText('MEMBER_PASSWORD_RESET');
    // The first click only asked.
    expect((await readMember(await ownerCredential(), MEMBER_EMAIL)).mustChangePassword).toBe(
      false,
    );
    await page.screenshot({ path: testInfo.outputPath('reset-review.png') });

    await review.getByRole('button', { name: 'Yes, reset it' }).click();

    await notices(page, `Reset the password for ${MEMBER_EMAIL}. 0 sessions were signed out`);
    await expect(row).toContainText('MUST CHANGE PASSWORD');
    expect(await page.content()).not.toContain(TEMPORARY);

    expect((await readMember(await ownerCredential(), MEMBER_EMAIL)).mustChangePassword).toBe(true);
  });

  test('the reset is audited as the administrator, without the password', async () => {
    const [entry, ...rest] = await readAccountAudit(
      await ownerCredential(),
      'MEMBER_PASSWORD_RESET',
      MEMBER_EMAIL,
    );

    expect(rest).toEqual([]);
    expect(entry?.actor).toBe(OWNER.email);
    expect(JSON.stringify(entry)).not.toContain(TEMPORARY);
  });

  test('signing in with the temporary password leaves them nowhere but the account screen', async ({
    browser,
  }, testInfo) => {
    const page = await memberPage(browser, TEMPORARY);

    // Whatever they ask for, they are sent to the one screen that works.
    await expect(page).toHaveURL(/\/settings\/account$/);
    await expect(page.getByRole('heading', { name: 'Choose a new password' })).toBeVisible();
    await expect(page.getByRole('alert').first()).toContainText(
      'An administrator reset your password',
    );
    await expect(page.getByRole('navigation', { name: 'Settings' })).toHaveCount(0);

    await page.goto('/subjects');
    await expect(page).toHaveURL(/\/settings\/account$/);

    // The registry says the same from outside the browser: authenticated, holding nothing.
    const status = await statusOf(await credentialOf(page));
    expect(status).toMatchObject({ authenticated: true, scopes: [], mustChangePassword: true });

    // A reload resumes the limited session and lands in the same place.
    await page.reload();
    await expect(page).toHaveURL(/\/settings\/account$/);
    await expect(page.getByRole('heading', { name: 'Choose a new password' })).toBeVisible();
    await page.screenshot({ path: testInfo.outputPath('forced-change.png') });

    // The one thing they can do.
    await page.getByLabel('Current password').fill(TEMPORARY);
    await page.getByLabel('New password', { exact: true }).fill(SECOND);
    await page.getByLabel('Repeat the new password').fill(SECOND);
    await page.getByRole('button', { name: 'Review change' }).click();

    const review = page.getByRole('group', { name: /Replace the temporary password for/ });
    await expect(review).toContainText('The temporary password stops working at once');
    await expect(review).toContainText('can do everything your role allows');

    await page.getByRole('button', { name: 'Yes, replace it' }).click();

    // On to the app, as what their role allows, with the same session.
    await expect(page).toHaveURL(/\/$/);
    await page.goto('/subjects');
    await expect(page).toHaveURL(/\/subjects$/);
    await expect(page.getByRole('heading', { name: 'Subjects' })).toBeVisible();

    const after = await statusOf(await credentialOf(page));
    expect(after.mustChangePassword).toBe(false);
    expect(after.scopes).toContain('subject:write');
    expect((await readMember(await ownerCredential(), MEMBER_EMAIL)).mustChangePassword).toBe(
      false,
    );

    signedInHere = page;
  });

  test('they change it again by choice, in two clicks, and the session they used stays', async ({}, testInfo) => {
    const page = signedInHere;

    await page.goto('/settings/account');
    await expect(
      page.getByRole('navigation', { name: 'Settings' }).getByRole('link', { name: 'Account' }),
    ).toHaveAttribute('aria-current', 'page');

    await page.getByLabel('Current password').fill(SECOND);
    await page.getByLabel('New password', { exact: true }).fill(THIRD);
    await page.getByLabel('Repeat the new password').fill(THIRD);
    await page.getByRole('button', { name: 'Review change' }).click();

    // The first click sends nothing and says what the second will do.
    const review = page.getByRole('group', { name: /Change the password for/ });
    await expect(review).toContainText('Every other session of yours is signed out');
    await expect(review).toContainText('This one stays signed in');
    await expect(review).toContainText('Any API key you issued keeps working');
    await expect(review).toContainText('PASSWORD_CHANGED');
    await expect(review).toContainText('Neither password is recorded');
    expect((await statusOf(await credentialOf(page))).authenticated).toBe(true);
    await page.screenshot({ path: testInfo.outputPath('account-review.png') });

    await page.getByRole('button', { name: 'Yes, change my password' }).click();

    // Said from the registry's own count, not worked out here.
    await expect(page.getByRole('status')).toContainText(
      'Your password was changed. You had no other session to sign out.',
    );
    // The fields were emptied the moment they went, and the password is on no page.
    await expect(page.getByLabel('Current password')).toHaveValue('');
    expect(await page.content()).not.toContain(THIRD);

    // This session is the one that stays.
    await page.reload();
    await expect(page.locator('cd-app-sidebar').getByText(MEMBER_EMAIL)).toBeVisible();
  });

  test('the change is audited as them, with no password anywhere in the trail', async () => {
    const credential = await ownerCredential();
    // Two: replacing the temporary password was a change too, and the newest comes first.
    const entries = await readAccountAudit(credential, 'PASSWORD_CHANGED', MEMBER_EMAIL);

    expect(entries).toHaveLength(2);
    for (const entry of entries) {
      expect(entry.actor).toBe(MEMBER_EMAIL);
      expect(entry.target).toBe(MEMBER_EMAIL);
      expect(entry.detail).toBe('0 other sessions signed out');
    }

    const trail = await (
      await fetch(
        `${process.env.CONCORDAT_REGISTRY ?? 'http://localhost:5062'}/v1/audit?limit=500`,
        { headers: { authorization: `Bearer ${credential}` } },
      )
    ).text();

    for (const password of [FIRST, TEMPORARY, SECOND, THIRD]) {
      expect(trail).not.toContain(password);
    }
  });

  test('an administrator disables them, and the session they held stops at once', async ({
    page,
  }, testInfo) => {
    await page.goto('/settings/members');
    const row = memberRow(page);
    await expect(row).not.toContainText('MUST CHANGE PASSWORD');

    await row.getByRole('button', { name: 'Disable…' }).click();

    const review = row.getByRole('group', { name: `Disable ${MEMBER_EMAIL}?` });
    await expect(review).toContainText('They can no longer sign in');
    await expect(review).toContainText('Every API key they issued is revoked for good');
    await expect(review).toContainText('MEMBER_DISABLED');
    await expect(review).toContainText('Nobody is notified');
    await page.screenshot({ path: testInfo.outputPath('disable-review.png') });

    // Not yet: the first click only asks.
    heldBeforeDisable = await credentialOf(signedInHere);
    expect((await readMember(await ownerCredential(), MEMBER_EMAIL)).disabled).toBe(false);

    await review.getByRole('button', { name: 'Yes, disable them' }).click();

    await expect(row).toContainText('DISABLED');
    await notices(page, `Disabled ${MEMBER_EMAIL}. 1 credential was revoked.`);
    await expect(row.getByRole('button', { name: 'Enable…' })).toBeVisible();

    expect((await readMember(await ownerCredential(), MEMBER_EMAIL)).disabled).toBe(true);

    // The session the member's browser was holding: refused, with nobody having touched it.
    expect((await statusOf(heldBeforeDisable)).authenticated).toBe(false);
    await signedInHere.reload();
    await expect(signedInHere).toHaveURL(/\/sign-in/);
  });

  test('the disabling is audited as the administrator', async () => {
    const [entry, ...rest] = await readAccountAudit(
      await ownerCredential(),
      'MEMBER_DISABLED',
      MEMBER_EMAIL,
    );

    expect(rest).toEqual([]);
    expect(entry?.actor).toBe(OWNER.email);
    expect(entry?.detail).toBe('1 credential revoked');
  });

  test('enabling says they can sign in again, and that nothing they held comes back', async ({
    page,
  }) => {
    await page.goto('/settings/members');
    const row = memberRow(page);

    await row.getByRole('button', { name: 'Enable…' }).click();

    const review = row.getByRole('group', { name: `Enable ${MEMBER_EMAIL}?` });
    await expect(review).toContainText('They can sign in again with the password they had');
    await expect(review).toContainText('Nothing that was revoked');
    await review.getByRole('button', { name: 'Yes, enable them' }).click();

    await notices(page, `Enabled ${MEMBER_EMAIL}. They can sign in again.`);
    await expect(row).not.toContainText('DISABLED');

    // What the registry says, from outside the browser: no longer disabled, and the session that
    // was revoked when they were disabled is still revoked.
    expect((await readMember(await ownerCredential(), MEMBER_EMAIL)).disabled).toBe(false);
    expect((await statusOf(heldBeforeDisable)).authenticated).toBe(false);

    const [entry, ...rest] = await readAccountAudit(
      await ownerCredential(),
      'MEMBER_ENABLED',
      MEMBER_EMAIL,
    );
    expect(rest).toEqual([]);
    expect(entry?.actor).toBe(OWNER.email);
  });

  test('a page that is out of date shows what the registry says, not what it expected', async ({
    page,
  }) => {
    await page.goto('/settings/members');
    const row = memberRow(page);
    await expect(row.getByRole('button', { name: 'Disable…' })).toBeVisible();

    // Somebody else disables them while this page is open.
    await setDisabledRaw(await ownerCredential(), member.userId, true);

    await row.getByRole('button', { name: 'Disable…' }).click();
    await row.getByRole('button', { name: 'Yes, disable them' }).click();

    await expect(row).toContainText('DISABLED');
    await notices(page, `Disabled ${MEMBER_EMAIL}. 0 credentials were revoked.`);
  });
});

test.describe('the screens', () => {
  test('report no console errors', async ({ page }) => {
    const errors: string[] = [];
    page.on('console', (message) => {
      if (message.type() === 'error' && !message.text().startsWith('Failed to load resource')) {
        errors.push(message.text());
      }
    });
    page.on('pageerror', (error) => errors.push(error.message));

    await page.goto('/settings/members');
    await expect(page.getByRole('listitem').filter({ hasText: OWNER.email })).toBeVisible();
    await page.goto('/settings/account');
    await expect(page.getByRole('button', { name: 'Review change' })).toBeVisible();

    expect(errors).toEqual([]);
  });

  test('fit a phone, with no horizontal scroll', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 800 });

    for (const path of ['/settings/members', '/settings/account']) {
      await page.goto(path);
      await expect(page.getByRole('heading', { name: 'Settings' })).toBeVisible();

      const overflow = await page.evaluate(
        () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
      );

      expect(overflow, `${path} overflows by ${overflow}px`).toBeLessThanOrEqual(0);
    }
  });
});

/**
 * The credential a browser tab holds, which the app keeps in memory and nowhere script can read it from
 * by design. It is recovered the way the app does, through the httpOnly cookie the registry set: the
 * resume route trades the cookie back for it.
 */
async function credentialOf(page: Page): Promise<string> {
  const response = await page.request.post(
    `${process.env.CONCORDAT_WEB_URL ?? 'http://localhost:4300'}/v1/auth/resume`,
  );

  if (response.status() === 204) {
    return 'cdt_none_none';
  }

  if (!response.ok()) {
    return 'cdt_dead_dead';
  }

  return ((await response.json()) as { credential: string }).credential;
}
