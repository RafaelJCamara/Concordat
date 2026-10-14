# ADR-035: A contract's bindings have an identity, and can be changed and removed

- **Status:** Accepted
- **Date:** 2026-10-14
- **Deciders:** Rafael Camara

## Context

A contract's bindings could be **added and never touched again**. There was a `POST …/publishes` and a
`POST …/consumes` and nothing else, because a binding had no identity: it was a row in an owned collection
keyed by a number EF generated and nothing outside the table could name. The web app's first real overlap test
(2026-10-13) found what that costs, and [STATUS.md](../STATUS.md) recorded it as a hole:

1. Add `orders.#` carrying `acme.Fallback`, with no precedence.
2. Add `orders.created` carrying `acme.Created`, precedence `10`. The registry refuses it (`binding_conflict`):
   the patterns overlap, the subjects differ, and nothing separates them. Its own message says *"Give the two
   bindings different precedence values to say which wins."*
3. The first binding has none to change. A precedence only separates two bindings when **both** carry a value
   and the values differ, so the advice cannot be followed. The only way out is a new contract.

The same gap meant a wrong binding could not be corrected or taken away. The contract screen worked around it
by telling the person to *"set one up front"*, which is a workaround, not a fix.

## Decision

**Every binding has an identity: a `Guid` (version 7), its table's primary key (`binding_id`), returned as `id`
by every route that returns a contract.** It is stable for the life of the binding. `contract_id` stays as the
foreign key (now with an index of its own, since it no longer leads the key).

**Four routes, addressed by that id:**

| Route | What it does |
|---|---|
| `PUT …/contracts/{c}/publishes/{id}` | Replaces the binding's **subjects and precedence**. Send no precedence to clear it. |
| `DELETE …/contracts/{c}/publishes/{id}` | Removes the binding. |
| `PUT …/contracts/{c}/consumes/{id}` | Replaces the binding's **subjects**. |
| `DELETE …/contracts/{c}/consumes/{id}` | Removes the binding. |

All four answer `200` with the contract as it now stands (the shape the other contract routes already use) and
need `contract:write`. An unknown id, or an id of the other kind, is `404 binding_not_found` (a new code, in the
registry's catalogue and the web app's generated one); an unparseable id does not match the route and is `404`.

**What can change is only what is not the binding's definition.** The exchange, the pattern, the virtual host and
the broker are what the binding *is*: changing them is a different binding governing a different route, and an
audit entry for it would have to describe two things at once. That is a removal and an addition, each recorded as
what it is. A consume binding's queue and scope are the same. **A `PUT` replaces**, so an absent `precedence`
clears it; this is the same convention the policy `PUT` follows (nulls inherit), and the browser leaves a null
precedence off the body for it.

**The overlap invariant is run again on the way out.** An edit can create a conflict that adding never saw:
giving two overlapping bindings the same precedence, clearing the one that separated them, or pointing one at
subjects the other does not carry. The change is checked against every *other* binding with the same rule
`AddPublish` uses (and the same message, which names a key both patterns match), and a refused change leaves the
binding exactly as it was (`409 binding_conflict`). Without that, an edit would be the way round the rule. A
consume binding is checked the same way against the other bindings for its queue. Removing cannot conflict.

**A change that changes nothing writes nothing.** A `PUT` identical to what is stored answers `200`, records
nothing and saves nothing, the rule a policy change and an idempotent registration already follow. The same
subjects in another order are a change, because the order is stored and shown.

**Two new audit actions, in the same unit of work, as the authenticated caller:** `CONTRACT_BINDING_CHANGED`
(its detail names only what changed: `publish orders / orders.#: precedence none -> 1`,
`…: subjects a@latest -> b@2`, or both joined by `; `, and `consume q: subjects a@latest -> b@2`) and
`CONTRACT_BINDING_REMOVED` (the way the entry for adding it reads: `publish orders / orders.# -> a@latest`, so
the trail can be read as "this was added" and later "this was removed" about the same text). New actions rather
than reusing `CONTRACT_BINDING_ADDED`, for the reason [ADR-033](033-inherit-means-the-environments-default.md)
gave for the policy: an auditor filters by action to ask "who loosened this", and a filter that returns an
addition, a change and a removal under one name makes every reader open every row. It touches every place that
pins the audit vocabulary: `AuditAction`/`AuditTokens`, `WireTokenTests`, the web's `AUDIT_ACTIONS` and the audit
page's filter. Stored as the token in `audit_entry.action` (`varchar(64)`): no migration for it.

**Nobody is notified, and that is a decision, not an omission.** There is no `IOutbox` in these handlers and no
event for a binding change. It is arguably alert-worthy (removing a binding from an `ENFORCE` contract changes
what services refuse), but a new event is a new subscription vocabulary and, because an empty event set means
every event, a behaviour change for every unfiltered subscription ([ADR-034](034-a-policy-change-notifies-subscribers.md)
spent a whole section on that). Whether to add one is the maintainer's, and it is listed under what this does not
cover.

### The migration

`ContractBindingIds` changes the key of `publish_binding` and `consume_binding`. **The scaffolded migration was
wrong for any database with a row in it**: it altered the old integer key to `uuid`, which PostgreSQL refuses.
It is written by hand: add `binding_id` nullable, **give every existing binding a fresh random id**
(`gen_random_uuid()`, built in since PostgreSQL 13; this repository's images are 17), make it `NOT NULL`, drop the
integer, make `binding_id` the key and index `contract_id`. Nothing is lost but a number nobody could see; the
ids bindings now have did not exist before, so none is preserved.

Measured, not assumed, against the local compose database as it stood (57 publish bindings and 28 consume
bindings from earlier browser runs): the content of every binding (contract, scope, exchange, pattern, subjects,
precedence), hashed in a fixed order, was identical before and after, the counts were unchanged, every
`binding_id` was unique, and the key is `PK_publish_binding (binding_id)`. **`Down` was run on a scratch
database with rows in it** (up, down, up): the data survived and the integer identity came back.

### The web app

On the contract's page every binding row offers **Change** and **Remove** (absent without `contract:write`,
ADR-018). Pressing one opens a panel above the list, as the subject page does ([ADR-032](032-subject-settings.md)):
a form prefilled from what the binding carries for a change, and for a removal the question straight away. **Both
take two clicks, and the second says what it does, in the registry's terms**: that only the subjects and the
precedence change; that the registry answers with it from the next resolution and **a service that already
resolved the route keeps its answer until its cache expires** (the .NET SDK's `ContractTtl`, one minute by
default: read from the SDK's options, not measured against a running service); that an overlap is refused in the
registry's own words and leaves the binding as it was; that a removal stops *this binding* governing its route
and a route nothing else governs (another binding of this contract, or another contract) resolves as ungoverned
(`OFF`), with one more sentence when the contract is enforcing; that it cannot be undone but the same binding can
be added again with a new id; and that the audit entry is written and nobody is notified. A refusal returns the
person to the form with what they typed, because left on the confirmation the second click would resend the same
refused request.

`ChangeConfirmation` moved from `features/registry/ui` to `shared/ui` so this feature can use it without
importing another feature (the lint boundary), and gained a `declineLabel` (a removal's way back is "Keep it", not
"Keep editing"). The binding list's outputs are `changeRequested`/`removeRequested` (an output named `change`
shadows the DOM event, which the lint rule forbids). The add form no longer says a binding cannot be edited.

## Alternatives considered

- **Address a binding by its natural key** (exchange, pattern and scope). Rejected: it is not unique. Two
  overlapping bindings that carry the same subjects are allowed and need no precedence, and nothing stops the
  same exchange, pattern and scope being added twice with the same subjects, so the key would sometimes name two
  rows and the route would have to say which. It would also make "rename" look possible.
- **Address it by position in the list.** Rejected: not stable. Another caller's removal shifts every index, and a
  `DELETE …/publishes/2` that removes a different binding than the one on the person's screen is the worst
  failure a route like this can have.
- **Let the exchange, pattern and scope be edited too.** Rejected, above: it is a different binding, and the audit
  entry would describe two things at once. A removal and an addition are each recorded as what they are.
- **`PATCH` with optional fields instead of `PUT` replacing both.** Rejected: "absent" would have to mean both "do
  not touch" and, for precedence, "clear", which is the ambiguity `PUT` avoids. The caller says the whole of what
  it wants; the registry says whether that is a change.
- **Reuse `CONTRACT_BINDING_ADDED` for a change and a removal, with a detail.** Rejected, above.
- **Keep the integer key and add `binding_id` beside it.** Considered: a smaller migration. Rejected because the
  integer was only ever an EF artefact, and a table with two identities invites a route addressing the wrong one.
- **A tombstone instead of a removal.** Rejected: the audit entry holds what was removed, in the text it was added
  with, which is what a person reading the trail needs, and a tombstone would stay in every resolution's way.
- **Notify on a binding change.** Not done, above.

## Consequences

- **Positive:** the trap closes. A binding added without a precedence can be given one, and the overlapping binding
  that was refused is then accepted (`ThePrecedenceTrap_…` in the domain, `ABindingAddedWithoutAPrecedenceCanBeGivenOneAfterwards`
  over HTTP, and the first test of `contract-bindings.spec.ts` in a browser, which reproduces the refusal and then
  closes it). A wrong binding can be corrected or removed, and the trail says who did it and what changed.
- **Positive:** the overlap invariant holds for edits as for additions.
- **Negative: a key change on two tables**, with a hand-written migration, above. The old ids are gone (there were
  none anyone could name).
- **Negative: rolling the registry back past this change breaks the audit page** for as long as a
  `CONTRACT_BINDING_CHANGED` or `…_REMOVED` row exists: `AuditEntryConfiguration` throws on a stored action the
  build does not know, which is [ADR-033](033-inherit-means-the-environments-default.md)'s cost, again, and the
  fix that made the notification pump tolerant of an unknown token does not reach the audit table. A rollback to a
  build from before this change also meets `binding_id` as a key it does not map; run the `Down` migration first.
- **Negative: last write wins.** There is no optimistic concurrency on a contract (adding has none either): two
  people changing the same binding at once will both be answered `200` and the later write stands. The overlap
  check is run against what each saw, so two *different* edits made at the same moment could together produce an
  overlap neither was refused for.
- **Negative: a service keeps what it resolved until its cache expires**, so a change or removal is not instant for
  a running service. The confirmation says so.
- **Not covered:** a change or removal made by the `concordat` CLI, the SDKs or the GitHub Action (none of them
  manages bindings, and none was run against this); whether to notify; deleting a whole contract (there is no
  route for it, and this does not add one); the cache sentence against a live SDK (it is read from the SDK's
  options); two editors at once.

## References

- [ADR-018](018-admin-only-schema-editing.md) (the convention the code cites for controls absent without the scope), [ADR-032](032-subject-settings.md) (the
  panel and the two clicks), [ADR-033](033-inherit-means-the-environments-default.md) (the audit action and the
  no-op rule), [ADR-034](034-a-policy-change-notifies-subscribers.md) (why a notification is a decision)
- [M7.3 Contracts](../plan/M7-governance.md), [STATUS.md](../STATUS.md), [DECISIONS-PENDING.md](../DECISIONS-PENDING.md)
- `src/core/Concordat.Domain/Contracts/Contract.cs` (`ReplacePublish`, `RemovePublish`, `ReplaceConsume`,
  `RemoveConsume`), `src/core/Concordat.Application/Registry/ContractCommands.cs`,
  `src/hosts/Concordat.Api/ContractEndpoints.cs`,
  `src/core/Concordat.Infrastructure/Persistence/Migrations/20261014102811_ContractBindingIds.cs`,
  `tests/Concordat.Domain.Tests/ContractTests.cs`, `tests/Concordat.Application.Tests/ContractBindingHandlerTests.cs`,
  `tests/Concordat.Api.IntegrationTests/ContractBindingApiTests.cs`, `web/e2e/contract-bindings.spec.ts`
