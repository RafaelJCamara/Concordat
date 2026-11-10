# The demo — watch contracts and messages in a running system

[QUICKSTART.md](QUICKSTART.md) publishes two messages and stops. **This demo keeps going**: four small services
send orders and payments to each other over RabbitMQ, a few of those messages are wrong on purpose, and the
registry's web app fills up with the contracts that govern them, the violations that were reported and a breaking
change waiting for somebody to decide on it. It is the thing to run when you want to *see* Concordat rather than
read about it.

```
checkout --demo.orders / orders.placed-------->  billing     contract demo-orders    ENFORCE
gateway  --demo.payments / payments.captured-->  ledger      contract demo-payments  MONITOR
```

| Service | What it is | Registers with the registry as |
|---|---|---|
| `checkout` | Publishes `OrderPlaced` | producer of `demo.orders.OrderPlaced` |
| `billing` | Consumes `OrderPlaced` from `demo.billing.orders` | consumer of `demo.orders.OrderPlaced@>=1` |
| `gateway` | Publishes `PaymentCaptured` | producer of `demo.payments.PaymentCaptured` |
| `ledger` | Consumes `PaymentCaptured` from `demo.ledger.payments` | consumer of `demo.payments.PaymentCaptured@>=1` |

All four run in one process, `samples/Demo`, each with its own registry client and name, which is what lets the
registry say *who* reported a violation.

## What you need

- Docker, and the .NET SDK (the version in [`global.json`](../global.json)), because the demo is a .NET program.
- The registry, its web app and RabbitMQ running. Step 1 does it.
- About five minutes.

## 1. Start the registry, the web app and RabbitMQ

The compose file runs all three. From the repository root:

```bash
docker compose -f deploy/compose/docker-compose.yml --profile registry up -d
```

The registry image is built locally until the published ones exist: [RUNNING.md §7](RUNNING.md#7-or-run-all-of-it-from-containers)
has the two `docker build` commands and the `CONCORDAT_IMAGE` / `CONCORDAT_WEB_IMAGE` variables that point compose at them.
When it is up, the web app is on <http://localhost:5063>, the registry on <http://localhost:5062> and RabbitMQ's management
UI on <http://localhost:15672> (`guest` / `guest`).

> **The developer loop works too.** [QUICKSTART.md steps 1–3](QUICKSTART.md#1-start-the-dependencies) start the same
> things with `dotnet run`, and `npm start` serves the web app on <http://localhost:4200>.

## 2. Create an owner and an API key

A new registry answers nobody until it is claimed ([ADR-047](adr/047-a-new-instance-is-closed-until-it-is-claimed.md)),
and the demo does not claim an instance on your behalf.

1. Open the web app. It opens on **Set up Concordat**: create the first owner.
   The first time you are signed in, a short tour of the dashboard, subjects, approvals, contracts, settings and API keys
   starts by itself: skip it with **Escape** or **Skip tour**, and **Take the tour** in the sidebar plays it again.
2. **Settings → API keys → create a key.** Give it a label and tick **`subject:write`** and **`contract:write`**. Those
   two are all the demo needs. Copy the secret now; it is shown once.

([RUNNING.md step 4](RUNNING.md#4-claim-the-instance--read-this-before-anything-else) has the same thing with `curl`, and the
opt-in for trying it on a machine only you can reach with no accounts at all.)

## 3. Run it

**bash**

```bash
CONCORDAT_API_KEY=<the key> dotnet run --project samples/Demo
```

**PowerShell**

```powershell
$env:CONCORDAT_API_KEY = "<the key>"
dotnet run --project samples/Demo
```

It seeds the registry, then sends one order and one payment a second until you press **Ctrl+C**:

```
Seeding the 'dev' environment (nothing is overwritten if it is already there)
  + demo.orders.OrderPlaced v1: first version
  + demo.orders.OrderPlaced v2: optional coupon
  + demo.orders.OrderPlaced v3: currency becomes required   <- held at the approval gate
  + demo.payments.PaymentCaptured v1: first version
  + contract demo-orders [ENFORCE]: demo.orders / orders.* and demo.billing.orders carry demo.orders.OrderPlaced
  + contract demo-payments [MONITOR]: demo.payments / payments.* and demo.ledger.payments carry demo.payments.PaymentCaptured

08:12:24 checkout  → order ord-0001 published
08:12:24 gateway   → payment pay-0001 published
08:12:24 billing   ← order ord-0001 received  schema 4647e959 (v2)
08:12:24 ledger    ← payment pay-0001 received  schema e246a2d5 (v1)
08:12:25 gateway   ! payment pay-0002 breaks its contract but the mode is MONITOR, so it was published anyway: #/amount: NumberTooSmall ...
08:12:25 ledger    ! payment pay-0002 received, but it breaks the contract on demo.ledger.payments: #/amount: NumberTooSmall ...
08:12:25 checkout  ✗ order ord-0003 REFUSED before it reached the broker: #/total: NumberExpected at #/total (property 'total')
```

Read the three kinds of line, because they are the product:

| Line | What happened |
|---|---|
| `→ … published`, `← … received` | The message conformed. The consumer's line carries the **schema id and version** the publisher stamped, so it knows which contract validated the message without asking anyone |
| `✗ … REFUSED` | The contract is `ENFORCE` and the order broke it: the publish **threw before the broker saw anything**, and the error names the exact JSON Pointer |
| `! … published anyway` / `! … received, but it breaks the contract` | The contract is `MONITOR`: the message went out and was counted. Both ends noticed, so the one bad payment is reported twice, as a `PUBLISH` and a `CONSUME` violation |

Every fourth payment is wrong and every sixth order is. A violation shows up within a few seconds of starting.

## 4. Look at the web app

Leave the demo running and open <http://localhost:5063> (sign in with the owner you made).

| Page | What the demo put there |
|---|---|
| **Dashboard** | 2 subjects, 4 versions, **1 awaiting approval**, and the **violations** counter climbing to 3: `checkout`'s publish, `gateway`'s publish and `ledger`'s consume. *Enforcement coverage* shows one `ENFORCE` and one `MONITOR` contract |
| **Contracts** | `demo-orders` (`ENFORCE`) and `demo-payments` (`MONITOR`); open one for its publish binding (exchange and routing key) and consume binding (queue), with the subject and version each accepts, and where its mode is changed |
| **Approvals** | Version 3 of `demo.orders.OrderPlaced`, which adds a **required** `currency`. The registry did not refuse it and did not publish it: it is held at the approval gate, and `latest` is still version 2. Arriving from the Subjects pill, the page is narrowed to this subject (*Showing changes waiting on `demo.orders.OrderPlaced`*, with **Show all**). Open its review and choose **Detailed** to read the schema as a line diff, with the added `currency` property marked `+` and the registry's `required_field_added` finding under the `required` line, or **Summary** for the list of findings alone. The choice is remembered in this browser |
| **Subjects** | `demo.payments.PaymentCaptured` and `demo.orders.OrderPlaced` (three versions), **newest first** by the date each subject was created, which is shown in the *Created* column. `OrderPlaced` carries a **1 awaiting approval** pill that is a link: it opens the Approvals page on that held version, with its review already open. Each subject has its history and a diff between versions |
| **Audit log** | Everything the seed did (subjects, versions, contracts, bindings) attributed to the API key, each service's declaration, and later every decision and mode change you make, attributed to you |

**What it cannot show you, and says so on the Dashboard:** the registry never sees a message. It knows what the
contracts *declare* and what the services *report*; a violation counter of zero means none were reported, not that none
happened. The individual violations (which service, which route, how many times, the latest detail) are one request away:

```bash
curl -H "Authorization: Bearer <the key>" http://localhost:5062/v1/environments/dev/violations
```

The services the demo declared are at `/v1/environments/dev/services`, and the messages themselves are in RabbitMQ's
management UI (<http://localhost:15672>, **Queues**: `demo.billing.orders`, `demo.ledger.payments`).

## 5. Now change something

Each of these was run against the demo while this page was written, and what is described is what happened.

| Do this | What you will see |
|---|---|
| **Approvals → approve version 3** of `OrderPlaced` | Within about ten seconds **every order `checkout` sends is refused**, on `#/currency: PropertyRequired`: the change was breaking, and the demo's checkout does not send `currency`. This is the approval gate doing its job, and the reason it exists. `billing` starts reporting `(v3)` on whatever does get through |
| …then stop the demo and run it with `--currency` | `checkout` is the "fixed" deployment that sends `currency`, and orders are accepted again against version 3 |
| **Contracts → `demo-payments` → set enforcement to `ENFORCE`** | Within seconds `gateway`'s bad payments stop being published and are `REFUSED` instead, as the orders were. The registry changed the behaviour of a running service **with no deploy**: the mode lives in the contract, not in the code |
| Set a contract to `OFF` | Its route stops being checked at all, and the services say so (`published unchecked`) |
| **Reject** version 3 instead of approving it | `latest` stays at version 2 and nothing changes for `checkout`; the Dashboard's *Breaking changes* shows it as rejected |

The demo's clients re-read contracts and the latest version every **five seconds**, so that you do not have to wait. A real
service should keep the defaults (30 seconds and a minute).

## Options and settings

```text
dotnet run --project samples/Demo -- [options]

  --interval <ms>   time between rounds (default 1500)
  --count <n>       stop after n rounds instead of running until Ctrl+C
  --seed-only       prepare the registry and exit
  --currency        send "currency" on every order (the checkout that survives version 3)
```

| Variable | Default | |
|---|---|---|
| `CONCORDAT_API_KEY` | none | The key from step 2. Leave it unset only against a registry started with the anonymous opt-in |
| `CONCORDAT_URL` | `http://localhost:5062` | The registry |
| `CONCORDAT_ENVIRONMENT` | `dev` | Where the subjects and contracts go, and what the web app opens on |
| `RABBITMQ_HOST`, `RABBITMQ_PORT` | `localhost`, `5672` | The broker |
| `RABBITMQ_USER`, `RABBITMQ_PASSWORD` | `guest`, `guest` | |

On Ctrl+C it sends the last violations to the registry, prints what each service saw, and exits. It is safe to run again:
every seed step looks first and leaves what you did to it alone (an approved version stays approved).

## Starting over

The demo writes to the environment you name, so **a clean start is a new name**:

```bash
CONCORDAT_ENVIRONMENT=demo2 CONCORDAT_API_KEY=<the key> dotnet run --project samples/Demo
```

then switch the web app to `demo2` (the environment at the bottom of the sidebar). To wipe everything instead,
`docker compose -f deploy/compose/docker-compose.yml --profile registry down -v` drops the database and the broker, and
the registry is unclaimed again ([RUNNING.md, Starting over](RUNNING.md#starting-over)).

## How it is built

`samples/Demo` is an ordinary client of the published surface. Nothing in it reaches into Concordat's internals, which is
ADR-019's point and the reason it is worth reading before you write your own.

| File | What to learn from it |
|---|---|
| `Seed.cs` | What a pipeline does (`concordat push` in a real repository): create subjects, register versions, and bind a contract to an exchange, a routing key and a queue. The breaking version is registered like any other and is held, not refused |
| `Service.cs` | What a service does at startup: a `ConcordatClient` with a **service name** (so the registry can name it), an **enforcing channel** wrapped around an ordinary RabbitMQ channel, and a **timer that flushes violations**. The SDK counts violations on the delivery path and reports them in bulk, but *nothing calls the flush for you*, so a host that wants them on the dashboard runs a loop like the one here |
| `Traffic.cs` | The messages, and the ways they are wrong |
| `Program.cs` | The topology (a contract names an exchange and a queue, it does not create them), the four services and the loop |

For the SDK's behaviour in detail, [BROKER-PERMISSIONS.md](BROKER-PERMISSIONS.md) (the `configure` permission the middleware
needs) and [DESIGN.md](DESIGN.md) §5 are the places to go next.

## Troubleshooting

| You see | Because |
|---|---|
| `CONCORDAT_API_KEY is not set, and this registry will not answer without a credential` | A new registry is closed until claimed. Step 2 |
| `Could not … the registry answered 401` or `403` | The key is wrong, revoked, or lacks `subject:write` / `contract:write` |
| `The registry is not answering at …` | It is not running, or it is on another port: set `CONCORDAT_URL` |
| `Could not connect to RabbitMQ at …` | The broker is not running, or something else holds 5672; set `RABBITMQ_HOST` / `RABBITMQ_PORT` |
| compose fails with *port is already allocated* on 5672 or 15672 | Another RabbitMQ is on the machine. Stop it, or run your own broker elsewhere and point the demo at it with the variables above |
| `The Concordat client could not start: Warm-up failed` | The registry answered the demo's clients with an error (usually the key). The demo asks its clients to refuse to start unenforced, so it stops rather than run with nothing checked |
| The Dashboard shows no violations | They are reported every four seconds and the first bad message is about three rounds in; refresh. If the demo was stopped before then, none were reported |
| Nothing is refused, everything is published | Check the contract's mode on the **Contracts** page: `MONITOR` publishes violations, `OFF` does not check |
