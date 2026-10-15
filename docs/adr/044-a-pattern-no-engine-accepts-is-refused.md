# ADR-044: A pattern no engine accepts is refused; the rest is a warning

- **Status:** Accepted
- **Date:** 2026-10-15
- **Deciders:** Rafael Camara

## Context

[ADR-039](039-a-schema-that-is-not-a-schema-is-refused.md) refused a document that is not a valid 2020-12 schema and read `format`
as an annotation, so `{"type":"string","pattern":"("}` still registered. [ADR-041](041-a-pattern-that-is-not-a-regular-expression-is-warned-about.md)
measured why refusing it was undecided, 78 patterns through .NET, ECMA-262 and RE2: the engines disagree about what a regular expression
is *in both directions*, so a rule that refused whatever .NET cannot parse would turn away schemas other SDKs compile, and one that refused
whatever RE2 cannot would turn away lookahead and `\uXXXX`. It left a warning, recorded "refusing it is the largest thing still open", and
recorded a second gap: **the portability checker's RE2 list was short** (`\uXXXX`, `\cA`, `\e`, `\Z`, `[\b]` all compile in .NET and not in Go).

## Decision

**Refuse a pattern that no engine accepts. Warn about everything else.** The line is "no engine", not "some engine", so the rule
cannot turn away a schema a consumer somewhere compiles.

*Which patterns, and how it was settled.* `scripts/regex-oracle` runs a corpus through Go's `regexp` (RE2), Node's `RegExp` **with and
without the `u` flag** (ECMA-262), and Python's `re` (3.14); the .NET verdict is asked at run time. For each class of syntax error .NET
reports, every instance in the corpus was refused by all of them. Six classes are in, and one with a carve-out:

| .NET's `RegexParseError` | Example | Why every engine refuses it |
|---|---|---|
| `InsufficientClosingParentheses` | `(` `(a` `a(b\|c` | a group never closed |
| `InsufficientOpeningParentheses` | `)` `a)` | a `)` that closes nothing |
| `QuantifierAfterNothing` | `*a` `?` `(\|*)` | nothing to repeat |
| `ReversedCharacterRange` | `[z-a]` | range runs backwards |
| `ReversedQuantifierRange` | `a{3,2}` | bounds the wrong way round |
| `UnescapedEndingBackslash` | `a\` | escapes nothing |
| `UnterminatedBracket` | `[` `[a` `a[b` | a class never closed, **unless the text contains `[]` or `[^]`** |

The carve-out is real: ECMA-262 reads `[]` and `[^]` as the empty class and the class of everything and accepts them, and .NET reports
them as an unterminated bracket. Three corpus patterns (`[]`, `[^]`, `a[]b`) are the whole of the disagreement.

*What is left out, and why each is out.* `a++`, `a*+`, `a?+` (a possessive quantifier in Python 3.11+ and Java), `\1` with no group (an
octal escape without `u`), a lone `\c` (a literal without `u`), `[a-\d]` (allowed without `u`), `\p{Letter}` (ECMA-262 with `u`), `(?i)`
(inline flags everywhere but ECMA-262), `(?P<n>…)`, the other `RegexParseError`s. These stay what ADR-041 made them: a `regex_invalid`
warning, because some engine accepts them. **Java was not measured** (no JDK on the machine); each class above is an error in
`java.util.regex` by its documented grammar, and a measurement there is the obvious thing to add.

*How it is wired.* A new defect kind, **`pattern_unparseable`**, in `JsonSchemaValidityChecker` beside the metaschema and reference
checks, so it is `400 schema_invalid` from registration and from the dry-run check, with the JSON Pointer of the pattern (or the
`patternProperties` key) and the reason, and **a document the registry already holds keeps registering** by the same exemption as every
other defect. It reads every schema position (not `default`, `const`, `enum` or `examples`), including under a nested `$id`, where
reference checking steps back and a pattern needs nothing resolved. The editor (`ajv`) advises on a pattern it cannot compile;
`schema-validate.spec.ts` holds the advice and the registry to agreeing on `(`, `a{3,2}`, a `patternProperties` key, `[^]` and a lookahead.

*The RE2 list, completed.* The portability checker used `Contains("(?=")` and `Contains("\\1")`, which took `\(?=` and `[(?=]` for lookahead
and `\\1` for a backreference. It is a scan now (`RegexSyntax.Re2Cannot`) that knows an escape and a character class, and names what Go
refuses and .NET accepts: lookahead and lookbehind (positive and negative), a backreference (`\1`, `\k<n>`), `\uXXXX`, `\cX`, `\e`, `\Z`,
`\G`, `\b` inside a class, an inline `(?#…)` comment, an atomic group, a conditional group, a repetition count over 1000, and an unknown
POSIX class. **Held to Go on every pattern in the corpus that .NET parses**: the scanner names a construct exactly when `regexp.Compile`
refuses. The one finding per pattern rule stays.

## What it costs

- **A behaviour change for new registrations**, and the one to read first: a schema with one of the patterns above, which registered
  until now, is `400 schema_invalid`. Nothing stored is read differently. Reversing it is deleting the pattern walk in
  `JsonSchemaValidityChecker` (no migration).
- **The corpus is a measurement of three engines on one day.** `node scripts/regex-oracle/measure.mjs --check` says whether the
  committed file still matches the installed engines, and is deliberately **not** in CI: a runner's Go or Python moving would turn a gate
  red for a reason that is not a defect in the registry. Run it when one of the engines is upgraded.
- **The refusal depends on .NET's error classification.** A future .NET that reclassifies an error would move a pattern across the line,
  which the corpus test (every refused pattern is refused by every measured engine) catches.

## Not decided here

- A pattern valid in .NET and refused only by ECMA-262 (`(?i)`, `\A`, `a{,3}`) or only by RE2 stays a portability matter; ADR-041's
  table is the evidence. Nothing here warns about ECMA-262-only constructs.