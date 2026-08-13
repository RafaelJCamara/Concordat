# Signet — Delivery Plan

Work breakdown for the architecture in **[DESIGN.md](DESIGN.md)**. Every item traces to a
decision or section there; where it does, the reference is given. This index changes
rarely; the per-milestone files change often. **DESIGN.md should not change at all** as
work progresses — if delivery reveals a decision was wrong, that is an ADR amendment, not
a plan edit.

## Milestones

| M | Theme | Depends on |
|---|---|---|
| [M0](plan/M0-foundations.md) | Foundations | — |
| [M1](plan/M1-registry-core.md) | Registry core, JSON Schema only | M0 |
| [M2](plan/M2-dotnet-client.md) | .NET client + RabbitMQ.Client middleware | M1 |
| [M3](plan/M3-cli.md) | CLI, CI gate, build-time packages | M1 (M2 for runtime bits) |
| [M4](plan/M4-web-app.md) | Angular web app | M1 |
| [M5](plan/M5-formats.md) | Avro + Protobuf | M1 |
| [M6](plan/M6-sdks.md) | Tier 2 SDKs — TS/JS → Python → Go → Java | M5 |
| [M7](plan/M7-governance.md) | Environments, brokers, governance | M1 |
| [M8](plan/M8-identity.md) | Identity, RBAC, API keys | M7 |
| [M9](plan/M9-cloud.md) | Signet Cloud | M8 |

**Critical path: M0 → M1 → M2 → M3.** That sequence is the smallest genuinely useful
product — register a JSON Schema, block a breaking change in CI, enforce it at runtime
from .NET. M4 (web) and M5 (formats) can slip without invalidating it.

## Where work is tracked

Once seeded, **GitHub issues are the live tracker** — one issue per work package, one
GitHub milestone per M. Tick boxes there, not here. These files stay the *reference*: why
a package exists, what it must satisfy, and which decision it implements.

`scripts/seed-github.ps1` creates the milestones, labels and 48 issues, and is idempotent
— re-run it after adding a package here and it creates only what is missing.

> Accept a little duplication between these files and the issue bodies. The alternative —
> issues that only link here — makes the GitHub board useless at a glance, which is the
> one thing a tracker has to be.

## Conventions

- Work packages are numbered `M<milestone>.<package>` and items within them are
  checkboxes. Quote the package id in issues and branches (`M1.3-compat-engine`).
- **Exit** is the milestone's definition of done. A milestone is not finished because its
  boxes are ticked; it is finished when the exit criterion demonstrably holds.
- 🔴 marks the heaviest packages — schedule them first within their milestone and protect
  them from being squeezed.

## Heaviest packages

Where the risk concentrates. If any of these is going badly, the milestone is going badly.

| Package | Why |
|---|---|
| [M0.1](plan/M0-foundations.md#m01-name-availability--blocking-do-first) Name availability | Blocking. An afternoon that invalidates weeks if skipped |
| [M1.2](plan/M1-registry-core.md#m12-canonicalisation-and-identity--adr-015) Canonicalisation | Get the hash envelope wrong and schemas collide across reference sets |
| [M1.3](plan/M1-registry-core.md#m13-compatibility-engine--adr-016-design-7) Compatibility engine 🔴🔴 | The correctness heart. A wrong verdict blocks safe changes or waves breaking ones through |
| [M1.7](plan/M1-registry-core.md#m17-conformance-corpus-v0--adr-019) Conformance corpus | Written late, it only ratifies whatever .NET already did |
| [M2.5](plan/M2-dotnet-client.md#m25-header-survival-experiments--design-2) Header survival | Empirical, no code deliverable; the result *is* the Mode A vs Mode B guidance |
| [M6.1](plan/M6-sdks.md#m61-protocol-freeze-and-interop-prerequisites-) Protocol freeze | Five JSON Schema validators disagree at the edges; this is where that becomes a CI failure instead of a support ticket |

## Two things built early on purpose

Both are cheap now and expensive as retrofits:

- **[M1.5](plan/M1-registry-core.md#m15-persistence) wires `ITenantContext` and EF global query filters** with a single implicit
  tenant, so M9's multi-tenancy is a config swap rather than surgery.
- **[M4.2](plan/M4-web-app.md#m42-access-control-adr-018) builds the admin gate against a stub**, four milestones before real roles
  exist in M8. Retrofitting an authorization check across finished screens is how a write
  path gets missed.

## Not scheduled

Deliberately deferred ([DESIGN §13](DESIGN.md#13-deliberately-deferred--decide-during-implementation)) — decide during implementation, not before:
registry HA and leader election, backup/restore, API rate limiting, Signet's own
observability, SLO targets for the validate path, the versioning and deprecation policy
for Signet's own REST API, community scaffolding.

Deferred with research preserved ([DESIGN Appendix A](DESIGN.md#appendix-a--framework-adapter-research-deferred-adr-020)): .NET service-bus adapters,
Spring AMQP, Celery, NestJS microservices. **Spring AMQP outranks all of them** on estate
share and should be first if adapters resume.
