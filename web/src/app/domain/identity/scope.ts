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

/** Whether a granted set includes any of the scopes an action requires. */
export function grants(granted: readonly Scope[], required: readonly Scope[]): boolean {
  return required.some((scope) => granted.includes(scope));
}
