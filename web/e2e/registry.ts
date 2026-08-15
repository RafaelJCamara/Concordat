/**
 * Talking to the registry directly, so a browser test can arrange what it needs.
 *
 * Everything here goes over plain HTTP rather than through the UI. Arranging a reader account by
 * clicking through screens would mean a test about the scope guard failing whenever the members
 * screen changed — and the members screen does not exist yet.
 */
const REGISTRY = process.env.CONCORDAT_REGISTRY ?? 'http://localhost:5062';

/**
 * The owner this suite creates or signs in as.
 *
 * Overridable, because a registry can only be claimed once and it may already have been claimed
 * by a human poking at it. Point the suite at that account rather than making it guess.
 */
export const OWNER = {
  email: process.env.CONCORDAT_E2E_OWNER ?? 'e2e-owner@concordat.test',
  password: process.env.CONCORDAT_E2E_PASSWORD ?? 'correct horse battery staple',
} as const;

/** A reader, used to prove that write affordances are absent and write routes redirect. */
export const READER = {
  email: 'e2e-reader@concordat.test',
  password: process.env.CONCORDAT_E2E_PASSWORD ?? 'correct horse battery staple',
} as const;

/** Whether a registry URL points at this machine. */
function isLoopback(url: string): boolean {
  try {
    const host = new URL(url).hostname;
    return host === 'localhost' || host === '127.0.0.1' || host === '::1';
  } catch {
    return false;
  }
}

interface SignedIn {
  readonly credential: string;
  readonly actor: string;
  readonly scopes: readonly string[];
}

async function call(path: string, init?: RequestInit): Promise<Response> {
  return fetch(`${REGISTRY}${path}`, {
    ...init,
    headers: { 'content-type': 'application/json', ...(init?.headers ?? {}) },
  });
}

/** Whether the registry has any accounts. */
export async function isClaimed(): Promise<boolean> {
  const response = await call('/v1/auth/status');

  if (!response.ok) {
    throw new Error(
      `The registry at ${REGISTRY} answered ${response.status} for /v1/auth/status. ` +
        'Is it running? See e2e/README.md.',
    );
  }

  return ((await response.json()) as { claimed: boolean }).claimed;
}

/** Exchanges an email and password for a credential. */
export async function signIn(email: string, password: string): Promise<SignedIn> {
  const response = await call('/v1/auth/signin', {
    method: 'POST',
    body: JSON.stringify({ email, password }),
  });

  if (!response.ok) {
    throw new Error(`Signing in as ${email} failed with ${response.status}.`);
  }

  return (await response.json()) as SignedIn;
}

/**
 * Claims the instance if nobody has, and returns the owner's credential.
 *
 * <b>Idempotent, because claiming is not.</b> Bootstrap runs exactly once per registry, so a
 * suite that assumed a fresh database would pass once and fail every run after — the worst shape
 * for a test somebody runs locally twice.
 *
 * <b>"Claimed" and "claimed by us" are different conditions</b>, and conflating them produced a
 * bare 401 from the middle of setup on the first real run: the registry had been claimed by a
 * human poking at it, so bootstrap was skipped and the sign-in that followed had no account to
 * find. The failure now says which of the two happened and what to do about it.
 */
export async function ensureOwner(): Promise<string> {
  const claimed = await isClaimed();

  if (!claimed) {
    // Claiming is one-way and OWNER's password defaults to a value printed in this public
    // repo. A mistyped CONCORDAT_REGISTRY pointing this at a real, reachable instance would
    // otherwise seize it permanently under that password. A non-loopback target is only
    // trusted once an operator has deliberately set their own password.
    if (!isLoopback(REGISTRY) && !process.env.CONCORDAT_E2E_PASSWORD) {
      throw new Error(
        `Refusing to bootstrap ${REGISTRY}: it is not on this machine and ` +
          'CONCORDAT_E2E_PASSWORD is not set. Claiming an instance cannot be undone, and this ' +
          'suite ships a public default password -- set CONCORDAT_E2E_PASSWORD explicitly if ' +
          'you really mean to claim that registry.',
      );
    }

    const created = await call('/v1/auth/bootstrap', {
      method: 'POST',
      body: JSON.stringify({ email: OWNER.email, password: OWNER.password }),
    });

    if (!created.ok && created.status !== 409) {
      throw new Error(`Bootstrap failed with ${created.status}: ${await created.text()}`);
    }
  }

  try {
    return (await signIn(OWNER.email, OWNER.password)).credential;
  } catch (cause) {
    if (!claimed) {
      throw cause;
    }

    throw new Error(
      [
        `This registry is already claimed, and not by '${OWNER.email}'. Claiming happens`,
        'once and cannot be repeated, so the suite cannot create its own owner here.',
        '',
        'Either point it at the existing account:',
        '',
        '  CONCORDAT_E2E_OWNER=you@example.com CONCORDAT_E2E_PASSWORD=... npm run e2e',
        '',
        'or start from a clean database:',
        '',
        '  cd deploy/compose && docker compose --profile registry down -v',
        '  CONCORDAT_IMAGE=concordat/api:local docker compose --profile registry up -d',
      ].join(String.fromCharCode(10)),
      { cause },
    );
  }
}

/** Adds a reader, or leaves the existing one alone. */
export async function ensureReader(ownerCredential: string): Promise<void> {
  const created = await call('/v1/members', {
    method: 'POST',
    headers: { authorization: `Bearer ${ownerCredential}` },
    body: JSON.stringify({
      email: READER.email,
      password: READER.password,
      role: 'READER',
    }),
  });

  // 409 means a previous run created them, which is a success for our purposes.
  if (!created.ok && created.status !== 409) {
    throw new Error(`Creating the reader failed with ${created.status}: ${await created.text()}`);
  }
}

/** Makes sure an environment exists, so the subject list has somewhere to read from. */
export async function ensureEnvironment(ownerCredential: string, name: string): Promise<void> {
  const created = await call('/v1/environments', {
    method: 'POST',
    headers: { authorization: `Bearer ${ownerCredential}` },
    body: JSON.stringify({ name }),
  });

  if (!created.ok && created.status !== 409) {
    throw new Error(`Creating environment '${name}' failed with ${created.status}.`);
  }
}

/**
 * Makes sure the environment has at least one subject with a version.
 *
 * <b>An empty list would make the row assertions vacuous.</b> The bug this suite exists for —
 * an unrecognised `status` token failing the whole page — only shows when there is a row to
 * render, so a suite that asserted against an empty table would have been just as blind as the
 * unit tests were.
 */
export async function ensureSubject(
  ownerCredential: string,
  environment: string,
  subject: string,
): Promise<void> {
  const authorization = { authorization: `Bearer ${ownerCredential}` };

  const created = await call(`/v1/environments/${environment}/subjects`, {
    method: 'POST',
    headers: authorization,
    body: JSON.stringify({ name: subject, format: 'json', owner: 'e2e' }),
  });

  if (!created.ok && created.status !== 409) {
    throw new Error(`Creating subject '${subject}' failed with ${created.status}.`);
  }

  const version = await call(`/v1/environments/${environment}/subjects/${subject}/versions`, {
    method: 'POST',
    headers: authorization,
    body: JSON.stringify({
      schema: '{"type":"object","properties":{"id":{"type":"string"}}}',
      registeredBy: 'e2e',
    }),
  });

  // 409 is the idempotent case: the same schema is already at the tip.
  if (!version.ok && version.status !== 409) {
    throw new Error(`Registering a version failed with ${version.status}.`);
  }
}

/**
 * Makes sure a subject exists with a second version held at the approval gate.
 *
 * <b>`AWAITING_APPROVAL` is the state most like the one that broke this app.</b> `DISMISSED`
 * shipped server-side, the frontend vocabulary never learned it, and one such version failed
 * the entire subject list — with every unit test on both sides green, because nothing rendered
 * a real registry's answer. Every screen that shows a status therefore needs a real held
 * version to render, not a hand-built fixture that agrees with the frontend by construction.
 *
 * The breaking change is a newly-required field, which is the clearest one to read in a
 * divergence list: it names a path, an axis and a surface, and the message says what would
 * actually break.
 *
 * <b>It reads the subject before writing, and that is not defensiveness.</b> The obvious
 * implementation — call `ensureSubject`, then post the breaking schema, and treat 409 as
 * "already there" — grows the subject by two versions on every run, which the second run of
 * this suite caught. Re-posting the *base* schema while a breaking proposal is pending is a
 * revert, so the registry dismisses the pending version; re-posting the breaking schema then
 * opens a fresh one. Neither call answers 409, because both are legitimate state changes.
 * Idempotence here has to mean "look first", not "tolerate a conflict".
 */
export async function ensureHeldVersion(
  ownerCredential: string,
  environment: string,
  subject: string,
): Promise<void> {
  const authorization = { authorization: `Bearer ${ownerCredential}` };

  const created = await call(`/v1/environments/${environment}/subjects`, {
    method: 'POST',
    headers: authorization,
    body: JSON.stringify({ name: subject, format: 'json', owner: 'e2e' }),
  });

  if (!created.ok && created.status !== 409) {
    throw new Error(`Creating subject '${subject}' failed with ${created.status}.`);
  }

  const existing = await call(`/v1/environments/${environment}/subjects/${subject}`, {
    headers: authorization,
  });

  if (!existing.ok) {
    throw new Error(`Reading subject '${subject}' failed with ${existing.status}.`);
  }

  const versions = ((await existing.json()) as { versions: { status: string }[] }).versions;

  // Already arranged by an earlier run. Writing anything now is what grows the list.
  if (versions.some((version) => version.status === 'AWAITING_APPROVAL')) {
    return;
  }

  if (versions.length === 0) {
    await register(
      ownerCredential,
      environment,
      subject,
      '{"type":"object","properties":{"id":{"type":"string"}}}',
    );
  }

  await register(
    ownerCredential,
    environment,
    subject,
    '{"type":"object","properties":{"id":{"type":"string"},"total":{"type":"number"}},' +
      '"required":["total"]}',
  );
}

async function register(
  ownerCredential: string,
  environment: string,
  subject: string,
  schema: string,
): Promise<void> {
  const response = await call(`/v1/environments/${environment}/subjects/${subject}/versions`, {
    method: 'POST',
    headers: { authorization: `Bearer ${ownerCredential}` },
    body: JSON.stringify({ schema, registeredBy: 'e2e' }),
  });

  if (!response.ok) {
    throw new Error(`Registering against '${subject}' failed with ${response.status}.`);
  }
}
