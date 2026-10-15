# ADR-048: An account can change its password, be reset, and be disabled, and each says what it does to the credentials

- **Status:** Accepted
- **Date:** 2026-10-15
- **Deciders:** Rafael Camara

## Context

[ADR-008](008-built-in-identity.md) gave Concordat its own accounts and left them with no lifecycle. There was no way for a member to
change a password, none for an administrator to reset one (a forgotten owner password was a database edit), and none to stop a departing
member: `User.Disabled` and `SetDisabled` existed in the domain, sign-in refused a disabled account, and **nothing ever set the flag**.

Reading the code before building it turned up two things that would have made the obvious change wrong:

- **A disabled account's credentials would have kept working.** Sign-in checked `Disabled`; authenticating an API key never looked at the
  user. A session is "a short-lived API key" (`ApiKey`), valid for twelve hours, and a standing key carries its issuer's authority and does
  not expire. Setting the flag would have stopped the login form and left every session and key the person held.
- **The last-owner guard counted disabled owners.** `ChangeMemberRoleHandler` refused an owner demoting themselves only when no *other*
  owner existed, and "exists" meant "has the role". With disabling possible, an organisation could hold one owner who could sign in and
  one who could not, and let the first demote themselves.

## Decision

**Three actions, each audited as the authenticated caller in the same unit of work as the change, none ever touching a password in a log,
an audit entry or a response.**

### Change your own password: `POST /v1/auth/password`

Any signed-in person, with no scope (a Reader changes theirs). The body is `currentPassword` and `newPassword`.

- **The current password is judged first.** A caller holding a credential and not the password (a session left open on a shared screen) learns
  nothing from the endpoint, not even that the new one was too short. A wrong one is the refusal sign-in gives, `401 unauthenticated`, and
  counts against the same limit: the route is `auth-attempt`, 20 per five minutes per address, shared with sign-in, because both are a way
  to guess the password.
- **The new one meets the claim's rule and one more.** At least 12 characters and no composition rule, and it must differ from the current
  one. The second is not a rule about passwords: without it, "replace the temporary password" could be satisfied by typing it again.
- **The person's other sessions end and the one they are using stays.** People change passwords because someone else might have one, and a
  session is what that someone would be using; ending the session the request is made with would sign the person out of their own change.
- **API keys are untouched.** A key is 256 bits from a CSPRNG and is not derived from the password; a pipeline that stopped on a password
  change would punish the thing we want people to do.

### Reset a member's password: `PUT /v1/members/{id}/password`

`org:admin` only. The administrator types a **temporary** password (12 characters, the same rule) and **the member must replace it**.

- **Why admin-typed and not generated and returned.** The requirement that a password is never returned, plus consistency: it is how an account
  is created today (`POST /v1/members` takes an initial password). A generated one would need a one-time-secret path in the response, which is
  the one place a password could leak from.
- **Why the member must replace it.** The administrator knows it. Until the member has changed it, the account is exactly as private as that
  administrator is trustworthy, and a flag (`User.MustChangePassword`) makes that window as short as the member's next sign-in. The
  member's **session** is limited, on every request, to proving who it is and changing the password: it answers `403
  password_change_required` to everything else, holds no scopes in `/v1/auth/status`, and holds them all again the moment the password
  changes. This is enforced by the authenticator, so it holds for the CLI and curl as much as for the web app. Creating a member with an
  initial password is **not** changed by this and is not forced; see "What it costs".
- **Their sessions end; their keys do not**, for the reason above. An administrator who wants a key gone revokes it, or disables the account.
  (The first version limited standing keys too, and the HTTP test for "a reset leaves keys alone" failed: a pipeline cannot change anybody's
  password and would have stopped with no way to say why.)
- **Not your own account** (`409 own_account`): it would set a password without proving the current one is known, and a stolen session could
  lock the real owner out. **Not a member who holds authority your credential does not** (`403 member_outranks_caller`), below.

### Disable or enable a member: `PUT /v1/members/{id}/disabled`

`org:admin` only; the body's `disabled` is **required** (a missing boolean reads as `false`, which is "enable", so a misspelt field would
enable the member it was meant to disable).

- **Disabling revokes every session and API key the member holds, in the same unit of work, and revocation is terminal.** Enabling lets them
  sign in again and brings nothing back; what they issued is issued again on purpose. The alternative, suspending credentials while the account
  is disabled and reviving them on enable, fails open: a key forgotten about for a year works again the day someone is re-enabled for
  another reason.
- **The authenticator refuses a key whose account is disabled, whatever else happened.** Revocation is one write at one moment; a sign-in in
  flight when the account was disabled, or a key issued a moment earlier, would otherwise outlive it. This costs one lookup of the account by
  primary key per request that authenticates with a key that has an owner, and is done before the key's use is recorded so a refused
  credential leaves no trace of having worked.
- **A request that changes nothing writes nothing.** Disabling a disabled member answers `200`, revokes nothing, and leaves no second audit
  entry.

### The guards

| Rule | Code | Why |
|---|---|---|
| The last owner who **can sign in** cannot be disabled or demoted | `409 last_owner` | An organisation with nobody able to manage members can only be repaired in the database. Disabled owners no longer count; the demotion guard now applies to anyone demoting them and not only to the owner demoting themselves, and its code moved from `role_invalid` (400) to this one. |
| You cannot disable yourself, or reset your own password | `409 own_account` | Locks you out; bypasses the current-password proof. |
| A credential cannot act on an account that holds scopes it does not | `403 member_outranks_caller` | See below. |

**How rank was decided.** `Role` is ordered (Reader 1, Admin 2, Owner 3) and the brief asked that an administrator not reset someone who
outranks them. But only an Owner holds `org:admin`, so for a signed-in person the rule can never fire: everyone who may reset is an Owner and
nobody outranks an Owner. The rule that does matter is on the *credential*: an API key carries a subset of its issuer's scopes, and a key
holding only `org:admin` can administer the organisation while holding none of what an Owner holds. So the rule is asked of **scopes**, the same
principle `IssueApiKeyHandler` applies ("a key may never grant more than its issuer holds"): `Roles.MayManage` is true when the caller holds
every scope the target's role grants. It is tested on exactly that key, and for an Owner caller it is always true.

### Audit

`PASSWORD_CHANGED` (the member, as themselves), `MEMBER_PASSWORD_RESET`, `MEMBER_DISABLED` and `MEMBER_ENABLED` (the target, as the
administrator). Each names the account in `target` as `MEMBER_ADDED` does, and the detail carries counts ("1 credential revoked") and never a
password. A refused request writes nothing, and the four tokens are pinned on both sides (`AuditTokens`, `WireTokenTests`, the web's
`AUDIT_ACTIONS` and the audit page's filter-count test). Nobody is notified by any of them: the handlers take no outbox, and an HTTP test
holds the outbox's depth unchanged across all four.

### On the web

**Settings → Account** for anyone signed in (the change-password form, in two clicks), and **Reset password…**, **Disable…** and **Enable…** on
each row of **Settings → Members**, drawn only with `org:admin` and not at all on your own row. A session that must replace a temporary password
is sent by every guard to the account screen, which says why first. The confirmations reuse `ChangeConfirmation`, and **every sentence in them
was measured against the registry** (the .NET HTTP suite and `e2e/account-lifecycle.spec.ts`), not read off the code.

A wrong current password is `401`, like sign-in, and the web app's auth interceptor signs a person out on a `401`, so the change request opts
out (`REFUSAL_IS_NOT_EXPIRY`) and the account store asks the registry who it thinks the caller is before treating the `401` as an expiry. This is
the one place the "same refusal as sign-in" instruction costs something; see "If you overturn it".

## Alternatives considered

- **Suspend credentials while disabled and restore them on enable.** Rejected: fails open (above).
- **Revoke on disable and nothing else.** Rejected: leaves the sign-in race, and a disabled account's key would authenticate if any path
  forgot to revoke it.
- **Check the account on every request and revoke nothing.** Rejected: enabling would resurrect everything the person ever held.
- **A server-generated temporary password returned once.** Rejected: puts a password in a response.
- **A reset that does not force a change**, as creating a member does not. Rejected as the default: the administrator who typed it can sign in
  as the member for as long as nobody notices. It is the safer default and recorded as a decision to confirm.
- **`403` for a wrong current password**, which would not need the interceptor exception and would not tell an API client its credential is bad
  when it is not. Rejected because the instruction was the same neutral refusal sign-in gives; recorded to confirm.
- **Rank by `Role` number.** Rejected: vacuous for people and wrong for keys.
- **Delete the member.** Rejected: audit entries and keys reference accounts.

## Consequences

- **Positive:** a departing member's web login, their sessions and their keys stop together. A forgotten owner password is recoverable
  without touching the database as long as another owner exists, and `docs/OPERATING.md` says what to do when none does.
- **Positive:** the last-owner rule now means something once disabling exists, and holds against the demoting path too.
- **Negative: one extra primary-key lookup per request that authenticates with an owned key.** Measured only as a count of queries (one), not
  as latency.
- **Negative: the last-owner check is check-then-write, not a lock.** Two owners disabling each other in the same instant could leave none.
  It is not guarded further because the repair is the same database edit that `OPERATING.md` documents, and the window is one request wide.
- **Negative: `Disabled` and the password belong to the account, not to a membership.** Self-hosted has exactly one membership per account
  and no route adds a second, so an administrator acting on a member acts on the account. The day an account can belong to two organisations,
  one organisation's administrator could disable or reset a person who belongs to another, and these actions must move to the membership or be
  refused for such accounts. Nothing does that today.
- **Negative: creating a member with an initial password is not forced to change.** The same argument applies to it and it is not applied, to
  keep this change to the lifecycle the brief named.
- **Found, not fixed: an API key holding only `org:admin` can create an Owner today.** `POST /v1/members` and the role route check `org:admin`
  and nothing about what the new role grants, so such a key mints a full-privilege account it holds the password to. Measured with a throwaway
  test (`201`). `member_outranks_caller` closes the same escalation for reset and disable and does not touch this.
- **Neutral:** a migration adds `app_user.must_change_password`, false for every existing account. Applied to a database that already held
  accounts and checked.

## If you overturn it

- *Do not force a change after a reset:* delete the flag's use in `Authenticator` and `RequireScopeFilter`; the column can stay.
- *Answer a wrong current password `403`:* give it its own code, and delete `REFUSAL_IS_NOT_EXPIRY`, the status probe in the account store, and the
  `401` expectation in the HTTP and browser tests.
- *Let enable restore keys:* stop revoking in `SetMemberDisabledHandler`, keep the authenticator's account check, and accept the fail-open.

## References

- [ADR-008](008-built-in-identity.md), [ADR-018](018-admin-only-schema-editing.md), [ADR-027](027-read-requires-authentication.md),
  [ADR-047](047-a-new-instance-is-closed-until-it-is-claimed.md)
- [docs/OPERATING.md](../OPERATING.md): recovering from a lost owner password
