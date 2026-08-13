# ADR-018: Schema editing in the web app is admin-only

- **Status:** Accepted
- **Date:** 2026-08-13
- **Deciders:** Rafael Camara

## Context

Registering a schema version is not a routine edit. It changes the contract that every
registered consumer of that subject depends on, and under [ADR-017](017-gated-latest-pointer.md)
approving a breaking version moves the pointer other services follow.

The people who need to *read* the registry — anyone integrating with a message type — vastly
outnumber the people who should be able to change it.

## Decision

In the web application, schema writes are admin-only: creating subjects, registering
versions, patching, deleting, promoting, and approving or rejecting. Non-admins get the
complete read surface — browse, diff, impact analysis, audit, export — with no write
affordance rendered.

**The UI is not the security boundary.** It reflects a server-side scope check:
`subject:write` and `subject:admin` are granted to admin roles only, and every mutating
endpoint returns `403 insufficient_scope` regardless of caller.

## Alternatives considered

- **Everyone can write; rely on the approval gate.** Rejected: the gate only catches
  *breaking* changes. A non-breaking but wrong schema would register and move `latest`
  with no review at all.
- **UI-only restriction.** Rejected outright: the same endpoints are reachable from the
  CLI, the SDKs and curl. A hidden button is not authorization.
- **Per-subject ownership instead of a global role.** Deferred, not rejected — a reasonable
  future refinement once there are enough subjects for it to matter. `Subject.Owner` already
  exists in the domain model.

## Consequences

- **Positive:** the blast radius of a mistaken write is bounded by a small set of people,
  and the audit trail is meaningful.
- **Positive:** approve/reject being admin-only stops the author of a breaking change from
  waving it through themselves.
- **Negative:** admins become a bottleneck on legitimate schema evolution. Mitigated by CI
  being the primary path — `concordat push` from a reviewed pull request, not hand-editing
  in a web form.
- **Negative, and a sequencing hazard:** the web app is M4 and real roles are M8. The gate
  is therefore built in M4 against a stub that returns admin in single-user self-hosted
  mode. Retrofitting the check across finished screens in M8 is how a write path gets
  missed.

## References

- [DESIGN §9](../DESIGN.md#9-frontend-architecture-angular), [Context E](../DESIGN.md#context-e--identity--access)
- [M4.2](../plan/M4-web-app.md#m42-access-control-adr-018), [M8.2](../plan/M8-identity.md#m82-authorization-adr-018)
