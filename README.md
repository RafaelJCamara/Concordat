# Concordat

A schema registry and contract-enforcement platform for RabbitMQ — what Confluent Schema
Registry is to Kafka, but built for AMQP 0-9-1 and usable from any language.

> **Status: building.** The registry, the compatibility engine, the .NET SDK, the CLI and the
> Azure deployment all run — see **[docs/STATUS.md](docs/STATUS.md)** for what works, what is
> missing, and what was last verified running rather than merely tested. The architecture and
> the reasoning behind it live in **[docs/DESIGN.md](docs/DESIGN.md)**.

## The problem

Kafka teams get schema governance for free: producers and consumers agree on a versioned
contract, and incompatible changes are rejected before they reach production. RabbitMQ
has no equivalent. Teams ship untyped JSON, find out about breaking changes at runtime,
and have no central answer to *"what flows through this broker, and who owns it?"*

Nothing on the market fills this gap. Every product doing real payload enforcement is
Kafka-protocol-only, and the two vendors owning commercial RabbitMQ ship no payload
governance at all.

## What Concordat does

- **Registry** — versioned schemas (JSON Schema, Avro, Protobuf) with content-addressed,
  environment-portable IDs.
- **Compatibility** — two axes rather than one: *who breaks* (backward/forward/full) and
  *what breaks* (wire/JSON/source), with exact JSON-Pointer paths on every finding.
- **Contracts** — bind subjects to RabbitMQ topology: `(vhost, exchange, routing key)` on
  publish, `(vhost, queue)` on consume. This is the part Kafka has no equivalent for.
- **Enforcement** — validate on publish and consume via RabbitMQ.Client middleware.
  Service-bus adapters (MassTransit, NServiceBus, EasyNetQ, Rebus, Wolverine) are
  deferred past v1.
- **CI gate** — a `concordat` CLI that fails the build when a change would break a
  registered consumer, shipped as a single native binary, a Docker image and a GitHub
  Action so non-.NET teams need no .NET installed.
- **Governance** — impact analysis ("who breaks if I change this?"), environment
  promotion, and an approval gate that lets a breaking schema register for review without
  moving the `latest` pointer.

Payloads are not mutated. Schema identity travels in AMQP headers, so a consumer without
a Concordat client still reads plain JSON and adoption can be incremental.

## Any language

The registry is a plain HTTP service with a committed OpenAPI 3.1 document; the server
being .NET is an implementation detail that the protocol never exposes. SDKs are ordinary
clients of that protocol with no privileged access — C# is simply the first, followed by
TypeScript/JavaScript, Python, Go and Java, all verified against one language-neutral
conformance corpus. The `concordat` CLI ships as a native binary, a Docker image and a
GitHub Action, so a non-.NET team needs no .NET installed at any point.

## Deployment

- **Self-hosted** — one container plus PostgreSQL; `docker compose up`.
- **Concordat Cloud** — the same image in multi-tenant mode, managed and subscription-billed.

Everything is Apache-2.0, including the Cloud code.

## Documentation

| | |
|---|---|
| [**docs/BROKER-PERMISSIONS.md**](docs/BROKER-PERMISSIONS.md) | **Read before deploying.** The one RabbitMQ permission the middleware needs, why it is a requirement rather than a preference, and what to do if your estate withholds it |
| [docs/QUICKSTART.md](docs/QUICKSTART.md) | Run the stack locally and publish through it, ending with a message refused for breaking its contract |
| [**docs/protocol/**](docs/protocol/README.md) | **The protocol.** The five normative artifacts, and everything needed to write a client in any language |
| [docs/DESIGN.md](docs/DESIGN.md) | Full architecture: domain model, envelope spec, API surface, SDK bindings, decisions |
| [docs/PLAN.md](docs/PLAN.md) | Delivery plan: milestones M0–M9 broken into numbered work packages with exit criteria |
| [docs/DECISIONS-PENDING.md](docs/DECISIONS-PENDING.md) | Open decisions, decisions taken on the owner's behalf, and a log of what is settled |
| [docs/STATUS.md](docs/STATUS.md) | **What is missing.** What runs today and was verified running, what is not built, what is half-built, and what is waiting on an account somebody has to open |
| [docs/adr/](docs/adr/README.md) | The 24 architecture decision records. Canonical — the table in DESIGN.md is a digest of them |

> **Writing a client?** Start at [docs/protocol/](docs/protocol/README.md), not DESIGN.md.
> ADR-019's acceptance test is that you never need to read a line of this repository's C#; if
> you do, that is a bug in those documents and worth an issue.

## License

Apache-2.0.
