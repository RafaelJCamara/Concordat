import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname } from 'node:path';

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

/** Where global setup leaves the owner's API credential, so specs need not sign in to arrange data. */
export const OWNER_CREDENTIAL_FILE = 'e2e/.auth/owner-credential.txt';

/** Saves the credential global setup obtained, for {@link ownerCredential}. */
export function rememberOwnerCredential(credential: string): void {
  mkdirSync(dirname(OWNER_CREDENTIAL_FILE), { recursive: true });
  writeFileSync(OWNER_CREDENTIAL_FILE, credential, 'utf8');
}

/**
 * The owner's API credential, for a spec that has to arrange data through the API.
 *
 * <b>Read from the file global setup wrote rather than obtained by signing in again.</b> Sign-in
 * is rate-limited to 20 attempts per five minutes per address, and every call to `ensureOwner`
 * is one: a suite that signed in whenever it wanted a credential spent its whole budget on
 * arranging fixtures and then failed an unrelated sign-in near the end, which is exactly what
 * happened when this suite passed ninety tests. Falls back to signing in only if the file is
 * absent, which means the spec is being run without global setup.
 */
export async function ownerCredential(): Promise<string> {
  return existsSync(OWNER_CREDENTIAL_FILE)
    ? readFileSync(OWNER_CREDENTIAL_FILE, 'utf8').trim()
    : ensureOwner();
}

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
  schemas: { base: string; proposal: string } = {
    base: BASE_SCHEMA,
    proposal: REQUIRED_FIELD_SCHEMA,
  },
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
    await register(ownerCredential, environment, subject, schemas.base);
  }

  await register(ownerCredential, environment, subject, schemas.proposal);
}

const BASE_SCHEMA = '{"type":"object","properties":{"id":{"type":"string"}}}';

/** BACKWARD-breaking (a new reader rejects old data) and FORWARD-safe (an old reader ignores it). */
const REQUIRED_FIELD_SCHEMA =
  '{"type":"object","properties":{"id":{"type":"string"},"total":{"type":"number"}},' +
  '"required":["total"]}';

/**
 * Breaking in *both* directions: `id` changes type, so an old reader cannot read what the new
 * schema writes. The one change an impact analysis can genuinely call BREAKS — a newly required
 * field, the clearest divergence to read, does not break a reader pinned to the old version.
 */
export const TYPE_CHANGE_SCHEMAS = {
  base: BASE_SCHEMA,
  proposal: '{"type":"object","properties":{"id":{"type":"integer"}}}',
} as const;

/**
 * Declares services that consume a subject, so impact analysis has something to judge.
 *
 * <b>An impact screen with no consumers only ever renders its empty state.</b> The answers
 * worth seeing are different in kind: a consumer pinned to a version, which the engine can
 * genuinely judge, and one that follows `latest`, which it cannot. Registering a service is an
 * upsert keyed on (environment, name), so this is idempotent without reading first.
 */
export async function ensureConsumers(
  ownerCredential: string,
  environment: string,
  subject: string,
  consumers: readonly (readonly [name: string, selector: string])[],
): Promise<void> {
  const declare = async (name: string, selector: string): Promise<void> => {
    const response = await call(`/v1/environments/${environment}/services`, {
      method: 'POST',
      headers: { authorization: `Bearer ${ownerCredential}` },
      body: JSON.stringify({
        name,
        consumes: [{ subject, selector }],
        reportedBy: 'e2e',
      }),
    });

    if (!response.ok) {
      throw new Error(`Declaring service '${name}' failed with ${response.status}.`);
    }
  };

  for (const [name, selector] of consumers) {
    await declare(name, selector);
  }
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

/**
 * Creates a subject and registers nothing against it.
 *
 * For tests that only ever *ask* the registry about a document — the dry-run endpoint writes
 * nothing — and need a subject of a given format to ask against. Idempotent: 409 is a subject
 * a previous run made, which is exactly what is wanted.
 */
export async function ensureEmptySubject(
  ownerCredential: string,
  environment: string,
  subject: string,
  format: 'json' | 'avro' | 'protobuf',
): Promise<void> {
  const created = await call(`/v1/environments/${environment}/subjects`, {
    method: 'POST',
    headers: { authorization: `Bearer ${ownerCredential}` },
    body: JSON.stringify({ name: subject, format, owner: 'e2e' }),
  });

  if (!created.ok && created.status !== 409) {
    throw new Error(`Creating subject '${subject}' failed with ${created.status}.`);
  }
}

/** What the registry said about a document, reduced to what a comparison needs. */
export interface DryRunVerdict {
  readonly status: number;
  /** The registry's `concordatCode` when it refused, else null. */
  readonly code: string | null;
}

/**
 * Asks the registry whether it would take a document, without registering anything.
 *
 * `POST …/subjects/{subject}/compatibility` is documented as a dry run that never writes, and
 * it runs the same canonicalisation and portability checks registration does. That makes it the
 * oracle for "is the client stricter or looser than the registry": the client's own opinion is
 * compared with this, never with what its author believes the registry does. (Three times in
 * this suite an engine answer disproved a fixture written to agree with its author.)
 */
export async function dryRun(
  ownerCredential: string,
  environment: string,
  subject: string,
  schema: string,
): Promise<DryRunVerdict> {
  const response = await call(`/v1/environments/${environment}/subjects/${subject}/compatibility`, {
    method: 'POST',
    headers: { authorization: `Bearer ${ownerCredential}` },
    body: JSON.stringify({ schema }),
  });

  if (response.ok) {
    return { status: response.status, code: null };
  }

  try {
    const body = (await response.json()) as { concordatCode?: string };

    return { status: response.status, code: body.concordatCode ?? null };
  } catch {
    // A 500 has no Problem Details body. Report it as what it is rather than throwing, so a
    // comparison can say "the registry crashed" and not "the test failed".
    return { status: response.status, code: null };
  }
}

/**
 * Registers a version over the API and returns what the registry answered.
 *
 * Unlike `register`, which throws on anything but success, this hands back the status, because
 * a test that is about a refusal wants to read it.
 */
export async function registerVersion(
  ownerCredential: string,
  environment: string,
  subject: string,
  schema: string,
  semanticVersion: string | null = null,
): Promise<{ status: number; body: unknown }> {
  const response = await call(`/v1/environments/${environment}/subjects/${subject}/versions`, {
    method: 'POST',
    headers: { authorization: `Bearer ${ownerCredential}` },
    body: JSON.stringify({ schema, semanticVersion, registeredBy: 'e2e' }),
  });

  return { status: response.status, body: await response.json().catch(() => null) };
}

/** A subject as the registry reports it, reduced to what a comparison needs. */
export interface SubjectView {
  readonly name: string;
  readonly format: string;
  readonly owner: string;
  readonly lifecycle: string;
  readonly contentModel: string;
  readonly compatibilityPolicy: { readonly mode: string | null; readonly surface: string | null };
  readonly latest: number | null;
  readonly versions: readonly {
    ordinal: number;
    schemaId: string;
    status: string;
    semanticVersion: string | null;
  }[];
}

/** Reads a subject, for a test that wants to know what really happened to it. */
export async function readSubject(
  ownerCredential: string,
  environment: string,
  subject: string,
): Promise<SubjectView> {
  const response = await call(`/v1/environments/${environment}/subjects/${subject}`, {
    headers: { authorization: `Bearer ${ownerCredential}` },
  });

  if (!response.ok) {
    throw new Error(`Reading subject '${subject}' failed with ${response.status}.`);
  }

  return (await response.json()) as SubjectView;
}

/** The registered text of a schema, by id, exactly as the registry serves it. */
export async function readSchemaText(ownerCredential: string, schemaId: string): Promise<string> {
  const response = await call(`/v1/schemas/${schemaId}`, {
    headers: { authorization: `Bearer ${ownerCredential}` },
  });

  if (!response.ok) {
    throw new Error(`Reading schema '${schemaId}' failed with ${response.status}.`);
  }

  return ((await response.json()) as { schema: string }).schema;
}

/** The id the registry would give a document, from the dry run. */
export async function schemaIdOf(
  ownerCredential: string,
  environment: string,
  subject: string,
  schema: string,
): Promise<string> {
  const response = await call(`/v1/environments/${environment}/subjects/${subject}/compatibility`, {
    method: 'POST',
    headers: { authorization: `Bearer ${ownerCredential}` },
    body: JSON.stringify({ schema }),
  });

  if (!response.ok) {
    throw new Error(`The dry run refused the document with ${response.status}.`);
  }

  return ((await response.json()) as { schemaId: string }).schemaId;
}

/** What the registry said to a create, reduced to what a comparison needs. */
export interface CreateVerdict {
  readonly status: number;
  /** The registry's `concordatCode` when it refused, else null. */
  readonly code: string | null;
  /**
   * The name the registry holds the subject under, or null when it refused for another reason.
   *
   * From the 201 body, or — when the subject already existed, which a re-run of a fixed table
   * makes the ordinary case — from the 409's own message (`Subject 'x' already exists`), which
   * names what the registry made of the name after its trim. Either way it is the registry's
   * account, not the request's.
   */
  readonly name: string | null;
  readonly detail: string | null;
}

/**
 * Sends a create exactly as given and reports what the registry made of it.
 *
 * <b>There is no dry run for a create, and an accepted one is permanent</b> — so a parity table
 * built on it names its subjects and runs against an environment of its own (`e2e-names`), and
 * counts both a 201 and a 409 `subject_already_exists` as "the registry accepted this name": the
 * 409 is only reached once the name has passed validation, which is the thing being asked.
 */
export async function createSubjectRaw(
  ownerCredential: string,
  environment: string,
  body: Record<string, unknown>,
): Promise<CreateVerdict> {
  const response = await call(`/v1/environments/${environment}/subjects`, {
    method: 'POST',
    headers: { authorization: `Bearer ${ownerCredential}` },
    body: JSON.stringify(body),
  });

  let parsed: { concordatCode?: string; detail?: string; name?: string } = {};

  try {
    parsed = (await response.json()) as typeof parsed;
  } catch {
    // A 500 may have no body. Reported as what it is, so a comparison says "crashed".
  }

  const taken = /^Subject '(.*)' already exists/su.exec(parsed.detail ?? '');

  return {
    status: response.status,
    code: parsed.concordatCode ?? null,
    name: response.ok ? (parsed.name ?? null) : (taken?.[1] ?? null),
    detail: parsed.detail ?? null,
  };
}

/** Whether a verdict means the registry took the name: created, or already there under it. */
export function accepted(verdict: CreateVerdict): boolean {
  return verdict.status === 201 || verdict.code === 'subject_already_exists';
}

/** What the registry said to a change of a subject, reduced to what a comparison needs. */
export interface ChangeVerdict {
  readonly status: number;
  /** The registry's `concordatCode` when it refused, else null. */
  readonly code: string | null;
  readonly detail: string | null;
}

async function verdictOf(response: Response): Promise<ChangeVerdict> {
  let parsed: { concordatCode?: string; detail?: string } = {};

  try {
    parsed = (await response.json()) as typeof parsed;
  } catch {
    // A 500 may have no body, and so may a 400 for a request the registry could not read at all.
    // Reported as what it is, so a comparison says "crashed" or "unreadable" and not "failed".
  }

  return {
    status: response.status,
    code: parsed.concordatCode ?? null,
    detail: parsed.detail ?? null,
  };
}

/**
 * Sends a `PATCH` to a subject exactly as given.
 *
 * The body is a string so a case can be a body no client would build (`{}`, a number for the
 * owner); `JSON.stringify` of an object is the ordinary way to call it. Which credential is the
 * caller's, so a scope can be tried: the default is the suite's owner.
 */
export async function patchSubjectRaw(
  credential: string,
  environment: string,
  subject: string,
  body: unknown,
): Promise<ChangeVerdict> {
  return verdictOf(
    await call(`/v1/environments/${environment}/subjects/${subject}`, {
      method: 'PATCH',
      headers: { authorization: `Bearer ${credential}` },
      body: typeof body === 'string' ? body : JSON.stringify(body),
    }),
  );
}

/** Sends a `PUT` to a subject's compatibility policy exactly as given. */
export async function putPolicyRaw(
  credential: string,
  environment: string,
  subject: string,
  body: unknown,
): Promise<
  ChangeVerdict & { readonly policy: { mode: string | null; surface: string | null } | null }
> {
  const response = await call(
    `/v1/environments/${environment}/subjects/${subject}/compatibility-policy`,
    {
      method: 'PUT',
      headers: { authorization: `Bearer ${credential}` },
      body: typeof body === 'string' ? body : JSON.stringify(body),
    },
  );
  const copy = response.clone();
  const verdict = await verdictOf(response);

  return {
    ...verdict,
    policy: response.ok
      ? ((await copy.json()) as { mode: string | null; surface: string | null })
      : null,
  };
}

/** Retires a subject. Terminal: the name stays taken, and the subject accepts no further change. */
export async function retireSubject(
  credential: string,
  environment: string,
  subject: string,
): Promise<void> {
  const response = await call(`/v1/environments/${environment}/subjects/${subject}`, {
    method: 'DELETE',
    headers: { authorization: `Bearer ${credential}` },
  });

  if (!response.ok) {
    throw new Error(`Retiring '${subject}' failed with ${response.status}.`);
  }
}

/** Issues an API key holding exactly these scopes, for trying what a narrower credential may do. */
export async function issueKey(
  credential: string,
  label: string,
  scopes: readonly string[],
): Promise<{ readonly id: string; readonly secret: string }> {
  const response = await call('/v1/api-keys', {
    method: 'POST',
    headers: { authorization: `Bearer ${credential}` },
    body: JSON.stringify({ label, scopes }),
  });

  if (!response.ok) {
    throw new Error(`Issuing a key for ${scopes.join(',')} failed with ${response.status}.`);
  }

  const issued = (await response.json()) as { key: { id: string }; secret: string };

  return { id: issued.key.id, secret: issued.secret };
}

/** Revokes a key a test issued, so a run does not leave working credentials behind. */
export async function revokeKey(credential: string, id: string): Promise<void> {
  await call(`/v1/api-keys/${id}`, {
    method: 'DELETE',
    headers: { authorization: `Bearer ${credential}` },
  });
}

/** One line of the audit trail, as the registry serves it. */
export interface AuditRow {
  readonly action: string;
  readonly actor: string;
  readonly target: string;
  readonly detail: string | null;
}

/** The audit trail for one target in one environment, newest first. */
export async function readAudit(
  credential: string,
  environment: string,
  target: string,
): Promise<readonly AuditRow[]> {
  const response = await call(
    `/v1/audit?env=${environment}&target=${encodeURIComponent(target)}&limit=200`,
    { headers: { authorization: `Bearer ${credential}` } },
  );

  if (!response.ok) {
    throw new Error(`Reading the audit trail failed with ${response.status}.`);
  }

  return (await response.json()) as AuditRow[];
}

/**
 * Makes sure an environment exists with a given compatibility default.
 *
 * Idempotent like `ensureEnvironment`, and for a different reason to have its own: a default can
 * only be chosen when an environment is created without changing it afterwards, and these
 * fixtures must never be moved — an environment whose default had been changed by a run is a
 * fixture the next run cannot trust.
 */
export async function ensureEnvironmentWithDefault(
  credential: string,
  name: string,
  policy: { mode: string; surface: string },
): Promise<void> {
  const created = await call('/v1/environments', {
    method: 'POST',
    headers: { authorization: `Bearer ${credential}` },
    body: JSON.stringify({
      name,
      compatibilityMode: policy.mode,
      compatibilitySurface: policy.surface,
    }),
  });

  if (!created.ok && created.status !== 409) {
    throw new Error(`Creating environment '${name}' failed with ${created.status}.`);
  }
}

/**
 * Sets an environment's default compatibility policy over the API.
 *
 * <b>Only for an environment a spec owns outright.</b> A default decides what every inheriting
 * subject in the environment is judged by, so moving `dev`'s, or `e2e-inherit`'s, would change what
 * other specs measure. `environment-default.spec.ts` owns `e2e-follows` and puts its default back
 * before it starts, which is what makes it repeatable.
 */
export async function setEnvironmentDefault(
  credential: string,
  name: string,
  policy: { mode: string; surface: string },
): Promise<void> {
  const response = await call(`/v1/environments/${name}`, {
    method: 'PATCH',
    headers: { authorization: `Bearer ${credential}` },
    body: JSON.stringify({ compatibilityMode: policy.mode, compatibilitySurface: policy.surface }),
  });

  if (!response.ok) {
    throw new Error(`Setting the default of '${name}' failed with ${response.status}.`);
  }
}

/** What the dry run said about a document, and under which policy it said it. */
export interface CheckVerdict {
  readonly compatible: boolean;
  readonly policy: { readonly mode: string | null; readonly surface: string | null };
}

/** Asks the registry whether a schema would be accepted, and which policy judged it. */
export async function checkSchema(
  credential: string,
  environment: string,
  subject: string,
  schema: string,
): Promise<CheckVerdict> {
  const response = await call(`/v1/environments/${environment}/subjects/${subject}/compatibility`, {
    method: 'POST',
    headers: { authorization: `Bearer ${credential}` },
    body: JSON.stringify({ schema }),
  });

  if (!response.ok) {
    throw new Error(`The dry run on '${subject}' answered ${response.status}.`);
  }

  return (await response.json()) as CheckVerdict;
}
