# The Concordat protocol

**Everything needed to write a Concordat client, in one place, in no particular language.**

[ADR-019](../adr/019-language-neutral-protocol.md) commits to a specific and testable claim:

> The server happens to be .NET. Nothing about the protocol may assume it. No capability exists
> that isn't reachable over documented REST — the .NET SDK gets no privileged endpoint, no
> private header, no serialisation shortcut, no behaviour that isn't written down.
>
> **Acceptance test:** a team writes a complete Go client from these documents without reading
> a line of C#, and hits no surprises.

If you find yourself reading `src/` to answer a question, that is a bug in these documents.
Please open an issue saying what you had to look up.

## The five normative artifacts

| # | Artifact | What it settles |
| --- | --- | --- |
| 1 | [**OpenAPI document**](../api/openapi.v1.json) | Every endpoint, request and response |
| 2 | [**Envelope specification**](envelope.md) | What a publisher stamps on a message and how a consumer reads it |
| 3 | [**Canonicalisation rules**](canonicalisation.md) | The canonical form of each schema language, and how a schema id is derived |
| 4 | [**`concordatCode` catalogue**](concordat-codes.md) | The stable error tokens clients branch on |
| 5 | [**Conformance corpus**](conformance.md) | Executable fixtures every implementation must pass |

Nothing else is protocol. The ADRs explain *why* the protocol is shaped as it is and
[`DESIGN.md`](../DESIGN.md) records the architecture behind it, but neither binds an
implementation — if they disagree with the artifacts above, the artifacts win.

## Which one wins

They are listed in ascending order of authority, and the order matters when they disagree:

1. **Prose explains.** These documents are written by hand and can be wrong.
2. **The corpus decides.** It is executable, it runs in CI, and every implementation runs the
   same JSON files. When a fixture and a sentence disagree, the fixture is right and the
   sentence is a bug.
3. **The registry is the reference for anything neither covers** — and that gap is itself a
   defect worth reporting, because it means an implementer had nowhere to look.

This is not a formality. `tests/Concordat.Conformance/CorpusTests.cs` states the same rule from
the other side: *"When one of these fails, the corpus is presumed right. It is the
specification; this assembly is one implementation of it, and the fact that it happens to be
the first does not make it the reference."*

## Two of these are generated, on purpose

The [OpenAPI document](../api/openapi.v1.json) and the
[`concordatCode` catalogue](concordat-codes.md) are generated from the source and **gated in
CI**: the build fails if either drifts from the code.

That is not tidiness. Both were, at different points, quietly wrong — the OpenAPI document
described **no response shapes at all** for five milestones, and the catalogue existed only as
a C# file that no non-.NET implementer could read. Both failures were invisible because nothing
compared the artifact to the thing it described. A hand-maintained normative document is a
document that is already out of date; the only question is whether anyone has noticed.

## Where to start

Writing a client, roughly in dependency order:

1. **[Canonicalisation](canonicalisation.md)** first. Schema ids are content-addressed, so an
   implementation that canonicalises differently computes different ids and cannot share a
   registry with anyone. Get this byte-exact before anything else, and prove it with
   `corpus/canonicalisation/` and `corpus/schema-id/`.
2. **[The envelope](envelope.md)** next. It is what makes a message self-describing, and it is
   the interoperability surface that matters at runtime — a consumer in one language reading a
   message a publisher in another produced.
3. **The [REST surface](../api/openapi.v1.json)** for registration, lookup and compatibility
   checks, with the **[code catalogue](concordat-codes.md)** for failures.
4. **[The corpus](conformance.md)** throughout, not at the end. It is a specification you can
   execute, and it is much cheaper to satisfy while you build than to retrofit.

## What is deliberately not here

- **Payload validation rules.** Concordat does not implement JSON Schema, Avro or Protobuf
  validation itself, and neither should your SDK — use your language's mature validator. What
  the corpus pins is the *edges where those validators disagree*, in
  `corpus/payload-validation/`, because that is where two SDKs quarantine different messages
  with no bug on either side.
- **Cross-subject references for Avro and Protobuf.** Refused in v1
  ([ADR-023](../adr/023-no-cross-subject-references-avro-protobuf.md)) — neither format has
  anywhere to pin a version. Self-contained schemas are the supported shape.
- **Authentication.** API keys and scopes arrive in M8. Until then the registry is unauthenticated
  and the tenant is implicit.

## Running the registry locally

See [QUICKSTART.md](../QUICKSTART.md) — a database, a broker, and the registry, in four
commands, ending with a message refused for breaking its contract.
