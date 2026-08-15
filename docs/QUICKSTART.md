# Quickstart — run Concordat and publish through it

> **Before you deploy this anywhere real:** the middleware declares its own quarantine exchange,
> so your application needs `configure` on `concordat.quarantine`. That is a requirement, not a
> preference — see [BROKER-PERMISSIONS.md](BROKER-PERMISSIONS.md). The compose stack below grants
> it, so the quickstart works as written.


Ten minutes, ending with a message refused because it broke its contract.

Everything here uses the published REST surface and the .NET SDK. Nothing reaches into
Concordat's internals, which is the point: [ADR-019](adr/019-language-neutral-protocol.md)
says every SDK is an ordinary client of a documented protocol, and a quickstart that cheated
would hide the places that is not yet true.

## 1. Start the dependencies

```bash
docker compose -f deploy/compose/docker-compose.yml up -d
```

PostgreSQL and RabbitMQ, nothing else. **Postgres is published on 55432, not 5432** — a
developer machine very often already has one on the default port, and "port is already
allocated" is a discouraging first thing to meet.

RabbitMQ's management UI is at <http://localhost:15672> (`guest`/`guest`). Worth having open:
it is where you look when a message does not arrive.

## 2. Create the database schema

```bash
ConnectionStrings__Concordat="Host=localhost;Port=55432;Database=concordat;Username=postgres;Password=postgres" \
  dotnet run --project src/hosts/Concordat.Migrator
```

Migrations run as a separate process rather than on API startup, so two API instances starting
together cannot race each other into the same migration.

## 3. Start the registry

```bash
ConnectionStrings__Concordat="Host=localhost;Port=55432;Database=concordat;Username=postgres;Password=postgres" \
  dotnet run --project src/hosts/Concordat.Api
```

It listens on **<http://localhost:5062>** — from `launchSettings.json`, which takes precedence
over `ASPNETCORE_URLS` under `dotnet run`. Pass `--no-launch-profile` if you want to choose the
port yourself.

Check it:

```bash
curl http://localhost:5062/health/ready
```

`/health/live` and `/health/ready` are separate, and liveness deliberately ignores the
database: a registry that cannot reach Postgres is not ready, but restarting it will not help.

## 4. Run the sample

```bash
dotnet run --project samples/Quickstart
```

```
→ registering acme.orders.OrderCreated in 'dev'
  schema id dfbde3d3e90093a4ae7a20c98fb62a3f
→ warmed up: warm since ...: 2 subjects, 2 schemas, 0 unenforced, 0 stale

→ publishing a valid order
  accepted

→ publishing an invalid order
  refused: ... #/total: NumberExpected at #/total (property 'total')

→ draining the queue
  {"orderId":"ord-1","total":42.5}
    schema dfbde3d3e90093a4ae7a20c98fb62a3f

Done. Nothing invalid reached the queue.
```

Four things in that output are worth more than they look:

- **The invalid message was refused before it reached the broker.** Under
  `EnforcementMode.Enforce` the publish throws rather than emitting; the bad message never
  exists, so nothing downstream has to cope with it.
- **The error names `#/total`.** An exact JSON-Pointer path, not "validation failed". This is
  where Confluent is weakest and where Concordat should be unambiguously better.
- **The delivered message carries `concordat-schema-id`.** A consumer knows exactly which
  schema validated it without asking anyone, because the id is content-addressed — the same
  bytes always produce the same id, in any implementation, offline.
- **`0 unenforced`.** If the registry had been unreachable, the client would have kept
  delivering and counted it here. Fail-open without a signal is how enforcement dies quietly
  and nobody notices for a quarter.

## 5. Now change something

`samples/Quickstart/Program.cs` is meant to be edited. Things that teach you something:

| Change | What you should see |
|---|---|
| `Mode = EnforcementMode.Monitor` | The invalid message is **published**, not refused. Monitor is the default, because adding a package reference must never start rejecting production traffic |
| Remove `total` from the payload | Accepted — it is not in `required`. Adding and removing optional fields is fully compatible, the change Confluent's JSON Schema defaults block |
| Add `"required": ["orderId", "total"]` and re-register | The registration **succeeds** with `AWAITING_APPROVAL` and does not move `latest`. A breaking change is a reviewable artifact, not an error, so CI never wedges |
| Add `"pattern": "^(?=.*[A-Z]).+$"` to `orderId` | A `portability` warning appears: Go's RE2 cannot compile lookahead **at all**, so that payload check would be lost entirely on the Go SDK |
| Stop the registry, then publish | With `FailOpen`, delivery continues and `status` counts it. Switch to `FailClosed` and the publish throws instead |
| Publish with `properties.Type` unset | No subject can be resolved. The subject is the message type ([ADR-011](adr/011-subject-is-message-type.md)), because a publisher knows `(exchange, routing key)` and a consumer knows `(queue)` — only the type is known to both |

## Where the registry's own contract lives

- `docs/api/openapi.v1.json` — every endpoint, request and response, generated from the code
  and gated in CI so it cannot drift
- `src/core/Concordat.Domain/Results/ConcordatCodes.cs` — the `concordatCode` catalogue clients
  branch on
- `tests/Concordat.Conformance/corpus/` — the normative fixtures every SDK must pass, as plain
  JSON so another language's runner reads the same files

> These five artifacts are named in ADR-019 as the protocol. Publishing them as one coherent,
> navigable set is the open item in [M6.1](plan/M6-sdks.md) — today you have to know where to
> look, which is exactly what an SDK author cannot be expected to do.

## Stopping

```bash
docker compose -f deploy/compose/docker-compose.yml down        # keeps the data
docker compose -f deploy/compose/docker-compose.yml down -v     # discards it
```
