# ADR-012: An environment is a logical label over registered brokers

- **Status:** Accepted
- **Date:** 2026-08-13
- **Deciders:** Rafael Camara

## Context

Teams model environments in incompatible ways. Some run a separate RabbitMQ cluster per
environment. Some run one cluster with a vhost per environment. Some run several clusters
per environment across regions. Any design that assumes one of these excludes the others.

Confluent's answer, "contexts", was bolted on late and is a common complaint.

## Decision

`Environment` is a first-class aggregate — a logical label with a name, a default
compatibility policy, and a collection of `BrokerConnection` entities. A broker connection
is `(uri, vhost, credentials, TLS settings)`, so the same physical broker can appear in an
environment more than once under different vhosts.

Subject names are unique per `(Tenant, Environment)`, making the environment a genuine
isolation boundary rather than a naming convention.

## Alternatives considered

- **Environment = cluster.** Rejected: breaks the vhost-per-environment topology outright.
- **Environment = vhost.** Rejected: breaks multi-cluster and multi-region.
- **Environment as a prefix on subject names.** Rejected: it makes promotion a rename, and
  renaming a subject invalidates every reference to it.

## Consequences

- **Positive:** both common topologies work without configuration gymnastics, and
  multi-region is expressible.
- **Positive:** promotion `dev → staging → prod` becomes a first-class operation that
  re-checks compatibility in the target — something Confluent lacks. Because schema IDs
  are content-addressed ([ADR-015](015-content-addressed-ids.md)), a promoted version keeps
  its ID, so in-flight messages stay valid across the promotion.
- **Negative:** broker credentials must be stored and encrypted, which is a security
  surface the product would not otherwise have. They are write-only over the API — reads
  return a `hasCredentials` boolean.
- **Negative:** environment-scoped uniqueness means the same subject in two environments
  is two rows, and impact analysis must be careful about which it means.

## References

- [DESIGN §4, Context C](../DESIGN.md#context-c--environments--brokers-adr-012)
- [M7 — Environments, brokers, governance](../plan/M7-governance.md)
