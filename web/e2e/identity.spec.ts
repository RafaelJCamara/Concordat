import { expect, test } from '@playwright/test';
import { READER_STORAGE_STATE } from './global-setup';
import { OWNER, ownerCredential, READER } from './registry';

/**
 * Members and API keys, against a registry that decides who may do what.
 *
 * <b>The credential is exercised, not just displayed.</b> A page can show a key's secret and be
 * entirely wrong about it, so after issuing one the test uses it to call the registry from
 * outside the browser — and after revoking it, uses it again to see that it stopped. A unit test
 * with a faked response agrees with whoever wrote it; this is the only place the registry says
 * whether the key is real.
 *
 * <b>Members cannot be removed</b> — there is no endpoint — so each run adds exactly one and
 * leaves it, named by the run. Keys are revoked, which leaves them listed as revoked. Both are
 * the honest price of testing something with a consequence, and neither touches an account any
 * other spec signs in as.
 */
const RUN = Date.now();
const EMAIL = `e2e-member-${RUN}@concordat.test`;
const LABEL = `e2e-key-${RUN}`;
const REGISTRY = process.env.CONCORDAT_REGISTRY ?? 'http://localhost:5062';

async function statusWith(secret: string): Promise<{ authenticated: boolean; scopes: string[] }> {
  const response = await fetch(`${REGISTRY}/v1/auth/status`, {
    headers: { authorization: `Bearer ${secret}` },
  });

  return (await response.json()) as { authenticated: boolean; scopes: string[] };
}

test.describe.serial('members', () => {
  test('lists the accounts this suite signs in as', async ({ page }) => {
    await page.goto('/settings/members');

    // Scoped to the list: the signed-in owner's own address is also in the sidebar footer.
    const rows = page.getByRole('listitem');

    await expect(rows.filter({ hasText: OWNER.email })).toBeVisible();
    await expect(rows.filter({ hasText: READER.email })).toBeVisible();
  });

  test('is in the settings tabs for an org admin, marked as the current page', async ({ page }) => {
    await page.goto('/settings/members');

    await expect(
      page.getByRole('navigation', { name: 'Settings' }).getByRole('link', { name: 'Members' }),
    ).toHaveAttribute('aria-current', 'page');
  });

  test('adds a member, who can then sign in as what they were made', async ({ page }) => {
    await page.goto('/settings/members');

    await page.getByLabel('Email').fill(EMAIL);
    await page.getByLabel('Initial password').fill('correct horse battery staple');
    await page.getByLabel('Role', { exact: true }).selectOption('ADMIN');
    await page.getByRole('button', { name: 'Add member' }).click();

    await expect(page.getByRole('status')).toContainText(`Added ${EMAIL} as ADMIN`);
    await expect(page.getByText(EMAIL).first()).toBeVisible();
    await expect(page.getByLabel('Email')).toHaveValue('');

    // Real: the new account's credentials work, and carry what an ADMIN is granted.
    const response = await fetch(`${REGISTRY}/v1/auth/signin`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ email: EMAIL, password: 'correct horse battery staple' }),
    });
    const body = (await response.json()) as { scopes: string[] };

    expect(response.status).toBe(200);
    expect(body.scopes).toContain('subject:write');
    expect(body.scopes).not.toContain('org:admin');
  });

  test('never puts the password on the page afterwards', async ({ page }) => {
    await page.goto('/settings/members');

    expect(await page.content()).not.toContain('correct horse battery staple');
  });

  test('changes a role, and the registry agrees after a reload', async ({ page }) => {
    await page.goto('/settings/members');

    const select = page.getByLabel(`Role for ${EMAIL}`);
    await select.selectOption('READER');

    await expect(select).toHaveValue('READER');
    await page.reload();
    await expect(page.getByLabel(`Role for ${EMAIL}`)).toHaveValue('READER');
  });

  test('refuses a password that is too short before anything is sent', async ({ page }) => {
    await page.goto('/settings/members');

    await page.getByLabel('Email').fill(`short-${RUN}@concordat.test`);
    await page.getByLabel('Initial password').fill('short');

    await expect(page.getByRole('button', { name: 'Add member' })).toBeDisabled();
    await expect(page.getByText('At least 12 characters')).toBeVisible();
  });

  test('is refused a duplicate address, in the registry’s own words', async ({ page }) => {
    await page.goto('/settings/members');

    await page.getByLabel('Email').fill(READER.email);
    await page.getByLabel('Initial password').fill('correct horse battery staple');
    await page.getByRole('button', { name: 'Add member' }).click();

    await expect(page.getByRole('heading', { name: /could not add the member/i })).toBeVisible();
    // The password is emptied whatever the answer; the address stays, to be edited.
    await expect(page.getByLabel('Initial password')).toHaveValue('');
    await expect(page.getByLabel('Email')).toHaveValue(READER.email);
  });

  test('will not let an owner demote themselves, and puts the selector back', async ({ page }) => {
    await page.goto('/settings/members');

    const select = page.getByLabel(`Role for ${OWNER.email}`);
    await select.selectOption('READER');

    // An instance whose last owner is a reader cannot be administered by anybody, so the
    // registry refuses — and a selector left saying READER would lie about it.
    await expect(page.getByRole('heading', { name: /could not change the role/i })).toBeVisible();
    await expect(select).toHaveValue('OWNER');

    await page.reload();
    await expect(page.getByLabel(`Role for ${OWNER.email}`)).toHaveValue('OWNER');
  });

  test('reports no console errors', async ({ page }) => {
    const errors: string[] = [];
    page.on('console', (message) => {
      if (message.type() === 'error' && !message.text().startsWith('Failed to load resource')) {
        errors.push(message.text());
      }
    });
    page.on('pageerror', (error) => errors.push(error.message));

    await page.goto('/settings/members');
    await expect(page.getByRole('listitem').filter({ hasText: OWNER.email })).toBeVisible();

    expect(errors).toEqual([]);
  });
});

test.describe.serial('API keys', () => {
  let secret = '';

  // Whatever happened above, this run's key does not outlive it. The revoke test is where it
  // normally ends, but a failure earlier in the block would stop before reaching it and leave a
  // live credential in the registry — which is exactly what the first run of this file did.
  test.afterAll(async () => {
    const headers = { authorization: `Bearer ${await ownerCredential()}` };
    const listed = await fetch(`${REGISTRY}/v1/api-keys`, { headers });

    if (!listed.ok) {
      return;
    }

    const keys = (await listed.json()) as { id: string; label: string; revokedAt: string | null }[];

    for (const key of keys.filter((k) => k.label === LABEL && k.revokedAt === null)) {
      await fetch(`${REGISTRY}/v1/api-keys/${key.id}`, { method: 'DELETE', headers });
    }
  });

  test('issues one, shows its secret once, and the secret really authenticates', async ({
    page,
  }) => {
    await page.goto('/settings/api-keys');

    await page.getByLabel('Label').fill(LABEL);
    await page.getByLabel('subject:read').check();
    await page.getByRole('button', { name: 'Issue key' }).click();

    const field = page.getByLabel('API key secret');

    await expect(page.getByText(/will not be shown again/)).toBeVisible();
    secret = await field.inputValue();

    expect(secret).toMatch(/^cdt_/);

    // Outside the browser, with only the secret: it is a real credential and carries only what
    // was ticked — least privilege is what nothing pre-ticked is for.
    const status = await statusWith(secret);

    expect(status.authenticated).toBe(true);
    expect(status.scopes).toEqual(['subject:read']);
  });

  test('ticks no scope for you, so the form starts unable to issue anything', async ({ page }) => {
    await page.goto('/settings/api-keys');

    await expect(page.getByRole('button', { name: 'Issue key' })).toBeDisabled();
    await expect(page.locator('fieldset input:checked')).toHaveCount(0);
  });

  test('lists the key with its label, scope and never the secret', async ({ page }) => {
    await page.goto('/settings/api-keys');

    const row = page.getByRole('listitem').filter({ hasText: LABEL });

    await expect(row).toContainText('subject:read');
    expect(await page.content()).not.toContain(secret);
  });

  test('records that the key was used', async ({ page }) => {
    await page.goto('/settings/api-keys');

    // `statusWith` above authenticated once, so the registry has seen it.
    await expect(page.getByRole('listitem').filter({ hasText: LABEL })).not.toContainText(
      'never used',
    );
  });

  test('revokes in two clicks, and the secret stops working', async ({ page }) => {
    await page.goto('/settings/api-keys');

    const row = page.getByRole('listitem').filter({ hasText: LABEL });
    await row.getByRole('button', { name: 'Revoke' }).click();

    await expect(row.getByText(/stops working on its next request/)).toBeVisible();

    await row.getByRole('button', { name: 'Confirm' }).click();

    // Kept, marked: a key that stopped working is evidence about why a pipeline did.
    await expect(row).toContainText('REVOKED');
    await expect(row.getByRole('button', { name: 'Revoke' })).toHaveCount(0);

    expect((await statusWith(secret)).authenticated).toBe(false);
  });

  test('reports no console errors', async ({ page }) => {
    const errors: string[] = [];
    page.on('console', (message) => {
      if (message.type() === 'error' && !message.text().startsWith('Failed to load resource')) {
        errors.push(message.text());
      }
    });
    page.on('pageerror', (error) => errors.push(error.message));

    await page.goto('/settings/api-keys');
    await expect(page.getByRole('button', { name: 'Issue key' })).toBeVisible();

    expect(errors).toEqual([]);
  });
});

test.describe('without org:admin', () => {
  test.use({ storageState: READER_STORAGE_STATE });

  test('a reader’s settings tabs omit members and API keys', async ({ page }) => {
    await page.goto('/settings/environment');

    const tabs = page.getByRole('navigation', { name: 'Settings' });

    await expect(tabs.getByRole('link', { name: 'Environment' })).toBeVisible();
    await expect(tabs.getByRole('link', { name: 'Members' })).toHaveCount(0);
    await expect(tabs.getByRole('link', { name: 'API keys' })).toHaveCount(0);
  });

  test('a reader who pastes the members address is sent to the read surface', async ({ page }) => {
    await page.goto('/settings/members');

    await expect(page).toHaveURL(/\/subjects$/);
  });

  test('and the API keys address likewise', async ({ page }) => {
    await page.goto('/settings/api-keys');

    await expect(page).toHaveURL(/\/subjects$/);
  });
});
