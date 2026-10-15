# ADR-041: A pattern that is not a regular expression is warned about, not refused, and is named as the schema's fault

- **Status:** Accepted
- **Date:** 2026-10-14
- **Deciders:** Rafael Camara (chose "warn when .NET cannot compile it" over refusing, over refusing only what every syntax
  rejects, and over doing nothing). Reversal of the warning is one `try` block in `JsonSchemaPortabilityChecker.CheckRegex`.

## Context

[ADR-039](039-a-schema-that-is-not-a-schema-is-refused.md) left one thing open on purpose: `format` is an annotation, so
`{"type":"string","pattern":"("}` registers, and it needs a decision about whose syntax decides. Two separable problems followed
from it, probed here.

**1. The mislabel (measured, 2026-10-14, NJsonSchema 11.6.1).** The schema compiles. The pattern is parsed when a value first
reaches it, so the failure is a `RegexParseException` thrown from `Validate`, from a message that is perfectly well-formed. The
validator's catch-all reported it as **`malformed_json`: "The payload is not well-formed JSON: Invalid pattern '('"**. A reader of
a quarantine was sent to the producer's message when the defect is in the registered schema. Three details the probe found: a
message that does not reach the pattern (`{}` against a `pattern` under an optional property) validates with no error at all;
`patternProperties` keys throw the same way, even for a message whose own keys do not match; and for a schema containing the word
"pattern" the validator runs on a second thread, where the exception arrives wrapped in an `AggregateException`, so a fix that
catches `RegexParseException` has to unwrap it first.

**2. Whose syntax.** JSON Schema says ECMA-262; the Go SDK is RE2; the registry and its own SDK are .NET. 78 patterns were put
through all three on 2026-10-14: .NET's `new Regex`, node's `RegExp` with the `u` flag (what `ajv` uses, and so the ECMA-262
oracle) and Go 1.26's `regexp.Compile` (RE2). **This is a hand-made list, about 20 of them deliberately broken, so the rates are
not how often anything occurs; it measures which constructs the engines disagree about, not how many schemas contain them.**

| Pattern | .NET | ECMA-262 (`u`) | RE2 |
|---|---|---|---|
| `^[a-z]+$`, `^\d{3}-\d{4}$`, `(?:a\|b)+`, `[\w.-]+@[\w.-]+`, `^.{1,64}$`, `(?<year>\d{4})` | ok | ok | ok |
| `(?i)abc`, `(?s).+`, `\Aabc`, `abc\z`, `(?>a+)`, `a{,3}`, `[[:alpha:]]`, `\e`, `(?#c)`, a lone `]` or `{` | **ok** | **refused** | mixed |
| `\p{Letter}`, `\u{1F600}`, `[]`, `[^]` | **refused** | **ok** | mixed |
| `^(?=.*\d)(?=.*[a-z]).{8,}$`, `(?<=\$)\d+`, `^(a)\1$`, `\cA` | ok | ok | **refused** |
| `\u00e9`, `^[\u4e00-\u9fff]+$` | ok | ok | **refused** |
| `(`, `)`, `[a-`, `*abc`, `a**`, `a++`, a trailing `\`, `[z-a]`, `a{2,1}` | refused | refused | refused |

Of the 78: .NET refuses 27, ECMA-262 (`u`) 42, RE2 35; **17 are refused by all three**; **4 are refused by .NET and accepted by
ECMA-262**; **19 are accepted by .NET and refused by ECMA-262**; 10 are accepted by both and refused by RE2. The syntax a refusal
rule used would therefore decide, in both directions, which valid schemas are turned away.

Parsing is also safe to do at registration: constructing a `Regex` from a 250,000-deep nest of groups took 124 ms and did not
overflow the stack; 250,000 alternatives took 89 ms. It parses and matches nothing, so no timeout is involved.

## Decision

**1. The validator names the schema.** A `RegexParseException` while validating is reported as **`schema_uncompilable`**, the kind
the validator already uses when the schema cannot be compiled, with a message that says a `pattern` or `patternProperties` key is
not a valid regular expression. It is not reported as `malformed_json`. `schema_uncompilable` was chosen over a new kind because
it is what the other SDKs report for the same defect at compile time and because nothing branches on a payload error's kind
(consumers read the path and message; the kinds are not in `docs/protocol`).

**2. The registry warns, and does not refuse.** A `pattern` or a `patternProperties` key the .NET parser rejects produces a
**`regex_invalid`** `PortabilityFinding` with `WARNING` severity, in the registration response and the dry-run check (the evaluator both use). It never blocks. The message says it is .NET's judgement alone, names the parser's reason, and says another SDK
may accept the pattern or refuse it for a different reason. One finding per pattern: a pattern that does not parse is not also
reported as lookahead or a backreference.

## Alternatives considered

- **Refuse when .NET cannot compile it.** Rejected: 4 of the 78 are valid ECMA-262, among them `[^]`, the idiom for "any
  character including a newline". It would contradict ADR-039's own position that a keyword whose syntax the SDKs disagree about
  is not asserted, and a refusal cannot be undone by the author as a warning can be ignored.
- **Refuse only what every syntax refuses** (the last row above). Zero false refusals by construction, but there is no ECMA-262 or
  RE2 parser in .NET here, so it means writing and maintaining a structural check of three grammars' common core for a defect the
  warning already names. Not built; it remains the way to get a refusal later if one is wanted.
- **Refuse when RE2 cannot compile it.** Rejected outright: it refuses lookaround, backreferences and `\u00e9`, which ECMA-262 and
  .NET both accept and authors write.
- **Do nothing but fix the label.** Rejected by the choice above: an author learns a pattern is broken from a quarantine in
  production, the least useful place.

## Consequences

- **Positive:** a quarantine reading `schema_uncompilable` for a bad pattern points at the schema, and the author hears about it
  when registering, in the one place they are looking.
- **Positive:** nothing that registered before is refused or rewritten.
- **Negative:** the warning is .NET's alone. It is silent on the 19 of 78 that ECMA-262 refuses and .NET accepts (`(?i)`, `\A`,
  `\z`, `(?>`), which a Node consumer fails to compile, and it warns on 4 that a Node consumer accepts. The message says so; the
  portability checker's RE2 list is unchanged and still does not know that RE2 refuses `\uXXXX`.
- **Negative:** the .NET validator still reports a bad pattern only for a message that reaches it, so a message that does not is
  accepted.
- **Neutral:** `regex_invalid` is a new published kind, documented with the others in `docs/plan/M6-sdks.md`.

> **Amended 2026-10-15.** Both open points are settled by [ADR-044](044-a-pattern-no-engine-accepts-is-refused.md): the registry refuses a pattern that every measured engine (.NET, ECMA-262 with and without `u`, RE2, Python) refuses and warns about the rest, and the RE2 list is a scan that was held to Go on a corpus, so `\uXXXX`, `\cX`, `\e`, `\Z` and `[\b]` now warn.
