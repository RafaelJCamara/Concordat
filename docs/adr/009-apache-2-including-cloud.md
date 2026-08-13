# ADR-009: Apache-2.0 for everything, including the Cloud code

- **Status:** Accepted
- **Date:** 2026-08-13
- **Deciders:** Rafael Camara

## Context

The default commercial pattern is open-core: an OSS core plus a proprietary or
source-available layer holding the features enterprises pay for. Confluent does exactly
this — its server is under the Confluent Community License, and **all authorization, RBAC
and subject ACLs, is separately Enterprise-licensed.**

That leaves free-tier Confluent Schema Registry with no authorization whatsoever: anyone
holding credentials can mutate or delete any subject.

## Decision

Everything Concordat ships is Apache-2.0, including the multi-tenancy and billing code
that runs Concordat Cloud.

## Alternatives considered

- **Open core, tenancy and RBAC proprietary.** Rejected: it would reproduce the exact gap
  that makes Confluent's free tier unusable for governance, in a product whose entire
  pitch is governance.
- **AGPL or BUSL.** Rejected: both deter corporate adoption, and adoption is the scarce
  resource for a new entrant in an empty category. BUSL in particular reads as
  "will be rug-pulled later" to the audience most likely to try it.

## Consequences

- **Positive:** shipping RBAC, API-key scopes and audit under a permissive licence is a
  concrete, checkable differentiator rather than a marketing claim.
- **Positive, and load-bearing on dependencies:** the licence forbids depending on
  libraries that have relicensed. MediatR is out — CQRS uses a hand-rolled dispatcher —
  and MassTransit v9 is commercial, so the deferred adapter would target
  `MassTransit.Abstractions` 8.5.x, the last Apache-2.0 release.
- **Negative, accepted:** a third party can host the same code as a competing service.
  Cloud competes on managed upgrades, backups, HA, SLA and support, not on withheld
  source.
- **Negative:** no obvious paid feature to withhold, so revenue depends entirely on
  operating the service well.

## References

- [DESIGN §10](../DESIGN.md#10-deployment-flavours)
- [ADR-020](020-rabbitmq-client-only.md) — the MassTransit licensing consequence
