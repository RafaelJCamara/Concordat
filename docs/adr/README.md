# Architecture decision records

One file per decision. **These are canonical.** The table in
[DESIGN.md](../DESIGN.md#decisions) is a digest of them — if the two ever disagree, the
ADR is right and the digest needs updating.

What lives here that the digest cannot hold: alternatives rejected and *why*, consequences
including the bad ones, and status over time. Without the rejected-alternatives section,
someone re-proposes a settled question every few months — see
[ADR-022](022-project-name-concordat.md), which exists precisely so "why not Signet?" is
answered once.

New decisions start from [TEMPLATE.md](TEMPLATE.md). Never edit an accepted ADR to reverse
it; write a new one and mark the old **Superseded by**.

| # | Decision | Status |
|---|---|---|
| [001](001-native-api-only.md) | Native API only, no Confluent wire compatibility | Accepted |
| [002](002-three-formats-in-v1.md) | Three schema formats in v1 | Accepted |
| [003](003-monorepo.md) | Monorepo | Accepted |
| [004](004-version-identity.md) | Version identity is an integer ordinal plus optional semver | Accepted |
| [005](005-enforcement-location.md) | Enforcement lives in client middleware and CI | Accepted |
| [006](006-angular-port-strategy.md) | Angular port keeps the design system, rebuilds the code | Accepted |
| [007](007-postgresql-ef-core.md) | PostgreSQL with EF Core | Accepted |
| [008](008-built-in-identity.md) | Built-in identity, scoped API keys, OIDC optional | Accepted |
| [009](009-apache-2-including-cloud.md) | Apache-2.0 for everything, including Cloud | Accepted |
| [010](010-header-envelope.md) | Header envelope: no `x-` prefix, UTF-8 strings | Accepted |
| [011](011-subject-is-message-type.md) | Subject is the message type, via `ISubjectResolver` | Accepted |
| [012](012-environment-over-brokers.md) | Environment is a logical label over registered brokers | Accepted |
| [013](013-amqp-091-only.md) | AMQP 0-9-1 only in v1, designed to survive 1.0 | Accepted |
| [014](014-infer-for-brownfield.md) | `concordat infer` for brownfield onboarding | Accepted |
| [015](015-content-addressed-ids.md) | Content-addressed schema IDs | Accepted |
| [016](016-two-axis-compatibility.md) | Two-axis compatibility | Accepted |
| [017](017-gated-latest-pointer.md) | Breaking changes register but gate `latest` | Accepted |
| [018](018-admin-only-schema-editing.md) | Schema editing in the web app is admin-only | Accepted |
| [019](019-language-neutral-protocol.md) | The registry is a language-neutral HTTP protocol | Accepted |
| [020](020-rabbitmq-client-only.md) | v1 ships one .NET SDK, over RabbitMQ.Client only | Accepted |
| [021](021-tier-2-sdk-set.md) | Tier 2 SDKs: TypeScript/JavaScript, Python, Go, Java | Accepted |
| [022](022-project-name-concordat.md) | The project is named Concordat | Accepted |
| [023](023-no-cross-subject-references-avro-protobuf.md) | No cross-subject references for Avro and Protobuf in v1 | Accepted |
| [024](024-v1-ships-dotnet-only.md) | v1 ships the .NET SDK only; Tier 2 SDKs are deferred | Accepted |
| [025](025-generic-subject-spelling.md) | A closed generic type is spelled `Outer_of_Arg`, defined over names rather than CLR syntax | Accepted |
| [026](026-self-hosted-web-fonts.md) | The web app serves its own fonts | Accepted |
| [027](027-read-requires-authentication.md) | Reading the registry requires authentication | Accepted |
