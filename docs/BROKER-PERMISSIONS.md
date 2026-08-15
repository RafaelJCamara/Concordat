# Broker permissions Concordat needs

**Read this before deploying.** Concordat's middleware needs one RabbitMQ permission most
applications already have and some estates deliberately withhold. Getting it wrong does not fail
at startup — it fails the first time a message is quarantined, which is the worst possible moment
to discover a topology gap.

---

## The requirement

**An application running `Concordat.RabbitMq` must be able to declare an exchange.**

In RabbitMQ's permission model that is `configure` on the quarantine exchange:

```
rabbitmqctl set_permissions -p / my-application \
  "^(concordat\.quarantine|my-app-.*)$" \
  ".*" \
  ".*"
```

The three regexes are `configure`, `write` and `read`. Only the first is at issue: Concordat
needs `configure` on `concordat.quarantine` and nothing else it does not already have.

## Why it is a requirement and not a preference

`ConcordatRabbitMqOptions.DeclareQuarantineExchange` defaults to **on**, so the middleware
declares `concordat.quarantine` itself the first time it needs it. The alternative — assume it
exists — means the first quarantine in production fails on a missing exchange.

Think about when that happens. Quarantine only runs when a message has already violated its
contract in an environment set to `ENFORCE`. So the exchange is needed for the first time during
an incident, in a code path nobody has exercised, and the failure is *a second failure stacked on
the one being handled*. Declaring on demand costs one idempotent `exchange.declare` per process
and removes that entirely.

**Declaration is idempotent and safe to repeat.** RabbitMQ's `exchange.declare` succeeds if the
exchange already exists with the same properties, so an application declaring one that
infrastructure-as-code already created is not a conflict.

## If your applications genuinely cannot declare

Some estates own topology in infrastructure-as-code and give applications no `configure`
permission at all — a defensible position, and Concordat supports it. Two things are then
required, and **both**:

```csharp
options.DeclareQuarantineExchange = false;
```

and the exchange provisioned ahead of time, as a durable fanout:

```
rabbitmqadmin declare exchange name=concordat.quarantine type=fanout durable=true
```

Turning the flag off without provisioning the exchange is the failure this page exists to
prevent. It looks fine until the first violation.

> **If you are unsure which applies to you, you need the permission.** The default assumes it,
> the quickstart assumes it, and the compose stack grants it.

## What Concordat never needs

Named so nobody grants more than necessary:

- **No `administrator` tag.** Nothing reads the management API at runtime.
- **No permission to delete anything.** Concordat declares; it never removes an exchange, a
  queue or a binding.
- **No access to virtual hosts it was not pointed at.** The registry connects only where a
  broker entry says to, and broker credentials are write-only over the API (ADR-012).
- **No `configure` on your own exchanges or queues.** The middleware decorates publishes and
  deliveries on topology your application already owns.

## The registry itself is different

The **registry** — the API process — connects to a broker only to run a health check, which
completes a real AMQP handshake against the specific virtual host and nothing more. It needs a
credential that can connect, and no `configure`, `write` or `read` beyond that.

A broker that is unreachable is recorded and returned, never raised as an error: a registry that
failed because a broker was down would have adopted somebody else's outage.

---

**Recorded as decision 14 in [DECISIONS-PENDING.md](DECISIONS-PENDING.md).**
