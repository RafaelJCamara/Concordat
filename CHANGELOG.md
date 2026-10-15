# Changelog

Notable changes, newest first. The detail behind each line is in [docs/STATUS.md](docs/STATUS.md) (what was
verified, and how) and the [architecture decision records](docs/adr/README.md) (why).

## 1.0.0 — self-hosted

The first release. Self-hosted only; the hosted Cloud edition and SDKs for languages other than .NET follow
([ADR-024](docs/adr/024-v1-ships-dotnet-only.md)).

### The registry

- **Versioned schemas** in JSON Schema (2020-12), Avro and Protobuf, with content-addressed, environment-portable
  ids; schemas are never deleted ([ADR-015](docs/adr/015-content-addressed-ids.md)).
- **Compatibility on two axes** — who breaks (backward, forward, full, and their transitive forms) and what breaks
  (wire, JSON, source) — with the exact JSON Pointer of every finding.
- **An approval gate** that lets a breaking version register for review without moving `latest`. A subject can name
  **who may decide** it ([ADR-043](docs/adr/043-a-subject-names-who-may-decide-its-held-versions.md)), and the
  approval queue is answered by the registry a page at a time.
- **Schema validity is enforced at registration:** a document that is not a valid schema, or whose `pattern` no
  engine accepts, is refused with the pointer to the defect; portability warnings (RE2, ECMA-262) are advice.
- **Subjects** are searched by name prefix, listed a page at a time, and can be **retired and, once retired,
  deleted for good** by an administrator, refused while a contract, a service or another schema refers to them
  ([ADR-046](docs/adr/046-a-retired-subject-can-be-deleted-for-good.md)).
- **Contracts** bind subjects to RabbitMQ topology (`vhost`, exchange and routing key on publish; queue on consume),
  with an enforcement mode per contract, and editable bindings.
- **Governance:** impact analysis, environment promotion, an audit trail of every change (as the authenticated
  caller), and notifications by webhook or email, with editable event filters.
- **Identity:** local accounts, roles, scoped API keys, sessions; read as well as write needs a credential.
- **A new instance answers nobody until it is claimed** ([ADR-047](docs/adr/047-a-new-instance-is-closed-until-it-is-claimed.md)).
  Set `CONCORDAT_BOOTSTRAP_TOKEN` on any host that is not your own machine.

### The web app

Dashboard, subjects (search, paging, create, retire, delete), versions, diff, impact analysis, approvals, contracts,
audit log and settings (environments, brokers, members, API keys, notifications), with a schema editor that
validates in a worker and a first-run setup screen.

### The .NET SDK

NuGet packages: `Concordat.RabbitMq` (enforcement on publish and consume for `RabbitMQ.Client`) and `Concordat.Client`
(the registry client, with fail-open/closed resolution), the format packages `Concordat.Formats.Json`, `.Avro` and
`.Protobuf`, `Concordat.Contracts` (a C# type as the source of truth for a subject, with drift checking at build
time) and `Concordat.Contracts.Testing`.

### The CLI and the CI gate

`concordat check | lint | push | promote | diff | impact | export | infer`, as a native binary per platform, a Docker
image and a GitHub Action, so a team that is not a .NET shop needs no .NET installed.

### Operations

Container images for the registry, the web app and the CLI (`linux/amd64` and `linux/arm64`); a compose file for
evaluation; an Azure Container Apps template; health probes; migrations as a separate process.

### Known limits

See [docs/STATUS.md](docs/STATUS.md#holes-in-what-is-already-built). The ones to know first: the registry never
sees message traffic, so "coverage" is what contracts declare; the dashboard's version counts are computed, not
maintained (about 100 ms at 200,000 versions); and Cloud billing, SSO and the other-language SDKs are not part of v1.
