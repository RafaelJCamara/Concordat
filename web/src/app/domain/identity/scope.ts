// The authorisation vocabulary (DESIGN §5, Context E).
//
// Listed here rather than in `core/auth/` because a scope is a domain concept the session
// happens to carry, not an Angular concern — and because M4.2's `canWriteSchemas`, the
// `*cdIfScope` directive and `scopeGuard` all need to agree on the spelling. One list.

/** Every scope the API can grant. */
export const SCOPES = [
  'subject:read',
  'subject:write',
  'subject:admin',
  'contract:read',
  'contract:write',
  'env:read',
  'env:write',
  'broker:read',
  'broker:write',
  'org:admin',
  // Not a permission — a marker saying "this credential belongs to a build pipeline". It grants
  // nothing on its own and is only ever consulted by an environment whose registration policy is
  // CI_ONLY, which is the one rule that has to tell a pipeline apart from a producer.
  'ci',
] as const;

/** A scope token as the API returns it. */
export type Scope = (typeof SCOPES)[number];

/**
 * The scopes that permit changing a contract (ADR-018).
 *
 * Schema writes are admin-only: creating subjects, registering versions, patching,
 * deleting, promoting, approving and rejecting. Anyone may read.
 *
 * **This list is not the security boundary.** Every mutating endpoint checks the same
 * scopes server-side and answers 403 regardless of caller, because the API is equally
 * reachable from the CLI, the SDKs and curl. What this list buys is that the UI does not
 * render an affordance the server will refuse.
 */
export const SCHEMA_WRITE_SCOPES: readonly Scope[] = ['subject:write', 'subject:admin'];

/**
 * The scopes that permit changing a contract: creating one, adding a binding, or moving its
 * enforcement mode (ADR-018). Separate from the schema write scopes because a contract is a
 * different resource with its own role grant, and a subject writer is not thereby a person who
 * should be able to start refusing the fleet's messages.
 */
export const CONTRACT_WRITE_SCOPES: readonly Scope[] = ['contract:write'];

/** The scopes that permit changing an environment's own settings (ADR-018). */
export const ENV_WRITE_SCOPES: readonly Scope[] = ['env:write'];

/** The scopes that permit adding, checking or removing a broker, and setting its credentials. */
export const BROKER_WRITE_SCOPES: readonly Scope[] = ['broker:write'];

/**
 * The scope that admits someone to members and API keys at all. Unlike the lists above it gates
 * *reading* too — the whole group is `org:admin` — so a screen behind it is absent to everyone
 * else, and a route to it redirects rather than rendering an empty page.
 */
export const ORG_ADMIN_SCOPES: readonly Scope[] = ['org:admin'];

/**
 * The scopes that permit approving or rejecting a held version.
 *
 * Narrower than {@link SCHEMA_WRITE_SCOPES} on purpose: a writer may register the change,
 * and only an admin may decide whether it goes live. Letting the author of a breaking change
 * be the one to approve it is the arrangement the gate (ADR-017) exists to prevent, and it is
 * why this is a separate list rather than a reuse of the write one.
 */
export const APPROVAL_SCOPES: readonly Scope[] = ['subject:admin'];

/** Whether a granted set includes any of the scopes an action requires. */
export function grants(granted: readonly Scope[], required: readonly Scope[]): boolean {
  return required.some((scope) => granted.includes(scope));
}
