# Source layout

Structure follows [DESIGN §8](../docs/DESIGN.md#8-backend-architecture-ddd--clean-architecture).
Only the projects [M1](../docs/plan/M1-registry-core.md) needs exist today; the rest are
created by the milestone that first has something to put in them, so the solution never
carries empty projects nobody builds.

| Path | Contains | Exists |
|---|---|---|
| `core/Concordat.Domain` | aggregates, value objects, invariants | ✅ |
| `core/Concordat.Application` | CQRS handlers, `Result<T>`, ports | ✅ |
| `core/Concordat.Infrastructure` | EF Core, PostgreSQL, outbox | ✅ |
| `formats/Concordat.Formats.Abstractions` | canonicalisation / validation / compatibility contracts | ✅ |
| `formats/Concordat.Formats.Json` | JSON Schema implementation | ✅ |
| `formats/Concordat.Formats.Avro` · `.Protobuf` | Avro and Protobuf | M5 |
| `hosts/Concordat.Api` | minimal-API host, `/v1` | ✅ |
| `hosts/Concordat.Migrator` | migration runner | ✅ |
| `clients/Concordat.Client` | HTTP client + cache | M2 |
| `clients/Concordat.Messaging.RabbitMq` | publish/consume middleware | M2 |
| `clients/Concordat.Contracts{,.MSBuild,.Testing}` | attributes, build-time drift check | M3 |
| `cloud/Concordat.Cloud.Tenancy` · `.Billing` | multi-tenancy, Stripe | M9 |
| `../tools/Concordat.Cli` | `concordat` CLI, NativeAOT | M3 |

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
