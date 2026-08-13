# ADR-017: Breaking changes register, but gate the `latest` label

- **Status:** Accepted
- **Date:** 2026-08-13
- **Deciders:** Rafael Camara

## Context

If a registry rejects a breaking change outright, CI wedges: the developer has a schema
that will not register, no artifact to review, and no path forward except editing until
the tool stops complaining. Nothing records what was proposed or why it was refused.

Separately, Confluent's `latest` is a mutable, globally-shared pointer to whatever has the
highest ordinal. With `use.latest.version=true`, a *third party* registering a version
silently changes what your producer serialises with — at runtime, with no deploy on your
side.

Buf solved the first problem by letting the change land as a reviewable artifact.

## Decision

A breaking registration **succeeds** with `Status = AwaitingApproval` and **does not
advance `latest`**. `LatestPointer` is an explicit, gated pointer on the Subject, not
"whichever ordinal is highest". Approval advances it; rejection does not. Borrowing Buf's
refinement, **an approval is automatically dismissed if the change is reverted.**

## Alternatives considered

- **Reject breaking changes outright.** Rejected: wedges CI and produces no artifact to
  discuss.
- **Accept them and move `latest`.** Rejected: that is the Confluent behaviour where a
  third party changes your producer's serialisation at runtime.
- **Accept, but require a force flag.** Rejected: force flags get pasted into CI scripts
  and stop meaning anything within a month.

## Consequences

- **Positive:** CI never wedges. The proposed schema is a concrete artifact with a diff and
  an impact report attached, which is a far better review than a failed build log.
- **Positive:** consumers pinned to `latest` are unaffected until a human approves.
- **Negative:** introduces an approval workflow — reviewers, notifications, an approvals
  page — which is real product surface (M7).
- **Negative:** a subject can accumulate unapproved versions, so the UI must make pending
  state obvious or it becomes a graveyard.
- **Guidance to document:** production contracts should `Pin` or use a `Range`, not track
  `latest` at all.

## References

- [DESIGN §4, Context A](../DESIGN.md#context-a--registry-core)
- [ADR-018](018-admin-only-schema-editing.md) — who may approve
