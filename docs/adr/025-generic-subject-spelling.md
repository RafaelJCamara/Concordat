# ADR-025: A closed generic type is spelled `Outer_of_Arg`

**Status:** Accepted · 2026-08-15 · Supersedes the refusal recorded in M2.3

## Context

`MessageTypeSubjectResolver` refused a generic type name outright. A .NET publisher sending
`Envelope<OrderCreated>` got `subject_name_invalid` and could not enforce anything, and the
recorded reason was sound: **any spelling becomes a rule five SDKs must reproduce character for
character**, and `List\`1[[Acme.Order, Asm, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null]]`
is CLR syntax that Go and Python have no way to reproduce.

That reasoning refuses the wrong thing. It refuses *generics* when what it needed to refuse is
*deriving the spelling from one language's type system*.

**The failure to avoid is not an ugly subject name. It is two SDKs deriving different subject
names for the same logical contract** — a .NET publisher registering one string and a Go consumer
looking up another, each convinced it is correct. That is not a documented limitation; it is a
silent interop break, and it is exactly what ADR-019's conformance corpus exists to prevent.

## Decision

**A closed generic subject is `{outer}_of_{arg}`, with `_and_` between further arguments.**

```
Acme.Envelope<Acme.OrderCreated>          → Acme.Envelope_of_Acme.OrderCreated
Acme.Pair<Acme.A, Acme.B>                 → Acme.Pair_of_Acme.A_and_Acme.B
Acme.Envelope<Acme.List<Acme.Order>>      → Acme.Envelope_of_Acme.List_of_Acme.Order
```

**The spelling is defined over names, not over syntax.** Its inputs are the outer type's
normalised name and the type arguments' normalised names, in declaration order — which every
language with generics can produce. It is *not* defined over backticks, arity markers or
assembly-qualified brackets, which only .NET has.

An implementation reads its own generic type, takes those names, and joins them. `Envelope[Order]`
in Go, `Envelope[Order]` in Python, `Envelope<Order>` in Java and `Envelope<Order>` in C# all
arrive at the same subject.

**Arity needs no marker.** `X<Y<Z>>` is `X_of_Y_of_Z` and `X<Y, Z>` is `X_of_Y_and_Z`; the
structure is recoverable from the separators.

**An open generic is still refused.** `Envelope<T>` names no contract — there is nothing to
validate a payload against — so it fails with a message naming the closed form.

## Alternatives considered

- **Keep refusing generics.** Honest, and a hard stop for any team whose publishers send an
  envelope type. It also pushes them to set `properties.type` by hand, which works and is exactly
  the manual reproduction of a spelling this ADR makes normative — so the rule exists either way,
  just undocumented and per-team.
- **Let each SDK derive its own spelling and document the differences.** The option that reads as
  reasonable and is the actual trap: the same contract becomes a different subject per language,
  and the break is silent.
- **Require an explicit subject for every generic type.** Safe and correct, and it makes the
  common case ceremony. Still available: an explicit `properties.type` always wins.

## Consequences

- **Positive:** a generic envelope type — a common shape in .NET messaging — is enforceable
  without hand-written subjects.
- **Positive:** the rule is language-neutral by construction, so the M6 SDKs have something to
  implement rather than something to invent.
- **Negative:** a type literally named `Envelope_of_Order` collides with the generic
  `Envelope<Order>`. Rare, immediately visible in the registry's subject list, and the same trade
  [decision 11](../DECISIONS-PENDING.md) accepted for nested types.
- **Negative:** the subject is longer and less pretty than a hand-picked name. A team that
  dislikes it sets `properties.type` explicitly.
- **Neutral:** nothing is stored differently. This is a client-side derivation, so no migration
  and no id churn.

## References

- [DESIGN §3 — Subject naming](../DESIGN.md#3-subject-naming-adr-011)
- [ADR-011 — Subject naming](011-subject-naming.md)
- [ADR-019 — Protocol-first](019-protocol-first.md)
- `corpus/subject-resolution/generic-*.json` — the normative fixtures
