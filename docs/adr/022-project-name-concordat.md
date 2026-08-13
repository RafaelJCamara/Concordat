# ADR-022: The project is named Concordat

- **Status:** Accepted
- **Date:** 2026-08-13
- **Deciders:** Rafael Camara

## Context

The project was originally called **Signet**. M0.1 was scheduled first, and marked
blocking, precisely to test that name against every registry the plan depends on before any
code existed. It failed.

`Signet.Client` on NuGet and `signet-client` on PyPI are both published by an **active**
project — `bytepunx/signet-proto`, with a PyPI upload dated 2026-08-08. Those are the exact
two package names [ADR-021](021-tier-2-sdk-set.md) depends on, in the same registries, for
the same polyglot-client shape. Separately, NuGet's `signet` ID belongs to **SigNET**
(7,452 downloads, and NuGet IDs are case-insensitive), npm `signet` is a type library, and
`signet.dev` is registered to a domain reseller.

## Decision

The project is **Concordat**. A concordat is a formal agreement between two parties, which
is what the product enforces and requires no metaphor to explain.

Availability: unclaimed on NuGet, PyPI, npm — unscoped *and* the `@concordat` scope — and
`.dev`, `.io`, `.sh` are all free. The highest-starred GitHub repository bearing the name
has zero stars. `crates.io/concordat` is taken and irrelevant: ADR-021 ships no Rust SDK.
Maven uses `io.github.rafaeljcamara`, so no domain sits on the critical path.

## Alternatives considered

| Name | Why not |
|---|---|
| **Signet** | Active collision, above |
| **Hutch** | `ruby-amqp/hutch`, 878 stars: *"a system for processing messages from RabbitMQ."* Same ecosystem — strictly worse than Signet |
| **Syngraph** / **Chirograph** | Clean on every registry, but `-graph` reads as graph database or GraphQL to this audience |
| **Stipula** | The best metaphor — the straw broken in two to seal a bargain — but `stipula-language/stipula` is a DSL for legal contracts |
| **Warrenty** | 160 GitHub repositories, every one a zero-star misspelling of "warranty" |
| **Indenture** | Available, but nine characters, archaic, and `indenture.dev` was gone |
| **Vadium** | Genuinely unclaimed and short, but semantically opaque — reads like *vanadium* |

The whole rabbit-metaphor family was ruled out as a class: Bunny, Hutch, Warren, Hare and
Burrow are all taken by messaging projects, precisely because everyone reaches for it.

## Consequences

- **Positive:** every registry and domain the plan needs is available under one name, and
  the name states what the product does.
- **Positive, and the transferable lesson:** **package-registry availability is not the
  test — ecosystem collision is.** Signet and Hutch were both free on NuGet *and* PyPI.
  What disqualified them appeared only on GitHub and RubyGems. Any future naming check must
  include those.
- **Negative:** nine characters is long for a CLI verb. An `cdt` alias is worth adding in
  M3.3.
- **Negative:** availability is not reservation. `concordat.dev` and the `@concordat` npm
  org are unclaimed but unheld; package IDs are only claimed on first publish, at M2, M3
  and M6.
- **Neutral:** the GitHub repository is still named `Signet` at time of writing, and
  `RepositoryUrl` in `Directory.Build.props` points there. That should be renamed before
  M2 publishes anything.

## References

- [M0.1 — Name availability](../plan/M0-foundations.md#m01-name-availability)
