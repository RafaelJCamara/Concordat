# Source layout

Structure follows [DESIGN §8](../docs/DESIGN.md#8-backend-architecture-ddd--clean-architecture).
A project is created by the milestone that first has something to put in it, so the solution
never carries empty projects nobody builds. The **Since** column is that milestone; every
project listed exists on disk today.

| Path | Contains | Since |
|---|---|---|
| `core/Concordat.Domain` | aggregates, value objects, invariants | M1 |
| `core/Concordat.Application` | CQRS handlers, `Result<T>`, ports | M1 |
| `core/Concordat.Infrastructure` | EF Core, PostgreSQL, outbox, tenancy, metering | M1 |
| `formats/Concordat.Formats.Abstractions` | canonicalisation / validation / compatibility contracts | M1 |
| `formats/Concordat.Formats.Json` | JSON Schema implementation | M1 |
| `formats/Concordat.Formats.Avro` · `.Protobuf` | Avro and Protobuf | M5 |
| `hosts/Concordat.Api` | minimal-API host, `/v1` | M1 |
| `hosts/Concordat.Migrator` | migration runner | M1 |
| `clients/Concordat.Client` | HTTP client + cache | M2 |
| `clients/Concordat.RabbitMq` | publish/consume middleware | M2 |
| `contracts/Concordat.Contracts` | `[ConcordatContract]`, and the package the generator travels in | M3 |
| `contracts/Concordat.Contracts.Generators` | Roslyn generator: a schema per attributed type, diffed against checked-in `contracts/` | M3 |
| `contracts/Concordat.Contracts.Testing` | `ConcordatAssert` — drift from what a registry is actually serving | M3 |
| `tools/Concordat.Cli` | `concordat` CLI, NativeAOT | M3 |

Two entries this table used to carry are gone rather than pending.
**`Concordat.Contracts.MSBuild`** was never built: M3.4 shipped a Roslyn source generator
instead, so there is no MSBuild task and no assembly loading at build time.
**`cloud/Concordat.Cloud.Tenancy` · `.Billing`** were never built either — what M9 scheduled
for them landed in the existing projects: tenancy in `Concordat.Domain/Identity`,
`Concordat.Infrastructure/ConcordatProfile.cs` and the query filters in `ConcordatDbContext`
(the seam wired from M1.5), billing in `Concordat.Domain/Billing`,
`Concordat.Infrastructure/Billing` and `Concordat.Api/BillingEndpoints.cs`. Cloud is a profile
chosen at the composition root, as DESIGN §8 prescribes, not a separate source tree.
*(Ledger brought to the real tree 2026-09-24.)*

## The dependency rule

```
Domain  <-  Application            <-  Infrastructure
        <-  Formats.Abstractions   <-  Api
                ^
                |
            Formats.Json
```

Domain references **nothing** — not Application, not Infrastructure, not a NuGet package
beyond the BCL. Application depends on Domain and on `Formats.Abstractions`, never on a
concrete format. Concrete formats are wired at the composition root in `Api`.

`Formats.Abstractions` references Domain (added in M1.2): the format layer speaks in
`SchemaId`, `Reference` and `SchemaFormat`, and duplicating those as a parallel type set
would be worse than the dependency. Domain remains the root and the graph stays acyclic.

`DependencyRuleTests` asserts the Domain half of this as a build failure. The rest is still
review-enforced; if it drifts, add
[NetArchTest](https://github.com/BenMorris/NetArchTest) assertions rather than relying on
discipline.
