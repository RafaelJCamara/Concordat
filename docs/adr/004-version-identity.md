# ADR-004: Version identity is an integer ordinal plus an optional semver label

- **Status:** Accepted
- **Date:** 2026-08-13
- **Deciders:** Rafael Camara

## Context

Users want semantic versions on schemas because semver communicates intent: MAJOR means
"this will break you". But semver is a claim made by a human, and humans mislabel breaking
changes as minor constantly. A registry that trusts the label inherits the lie.

Meanwhile the system needs an identifier that is immutable, totally ordered, and safe to
use as a cache key and a pointer target.

## Decision

Each version has an integer `Ordinal` — canonical, immutable, contiguous, monotonic — and
an optional `SemanticVersion` label. The ordinal is what the system uses. The label
carries human intent, and **Concordat verifies it**: registering a breaking change with a
MINOR or PATCH label is rejected.

## Alternatives considered

- **Semver as the only identity.** Rejected: it is user-supplied and therefore untrusted;
  it also admits gaps, re-ordering and duplicates, none of which a version pointer can
  tolerate.
- **Integer ordinal only, like Confluent.** Rejected: it discards intent. "Version 7"
  tells a consumer nothing about whether upgrading is safe.
- **Accept the semver label without checking it.** Rejected: an unverified label is worse
  than no label, because people act on it.

## Consequences

- **Positive:** the compatibility engine already computes whether a change is breaking, so
  verifying the label is nearly free and turns semver into a guarantee rather than a hope.
- **Positive:** `suggestedSemver` can be returned on every compatibility check.
- **Negative:** a team whose release process assigns versions upstream may find their
  chosen label rejected, which needs a clear error rather than a bare 409.

## References

- [DESIGN §4](../DESIGN.md#4-domain-model)
- [ADR-016](016-two-axis-compatibility.md) — what "breaking" means, on two axes
