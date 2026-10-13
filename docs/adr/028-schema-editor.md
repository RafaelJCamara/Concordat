# ADR-028: The schema editor says only what the registry would

- **Status:** Accepted
- **Date:** 2026-10-13
- **Deciders:** Rafael Camara

## Context

[M4.4](../plan/M4-web-app.md) asked for three things on `NewVersionPage`, which had been a plain
textarea: Monaco for editing, `ajv` for client-side validation and sample-message checking, and
the prototype's editing conveniences (an "are you sure" before something permanent, a version
label seeded from the last one, starting from the previous version, Format and Validate buttons).

Each of the three is a way for the client to hold an opinion about a document, and the registry is
the only authority on documents. A client that is *stricter* than the registry stops somebody
registering a schema it would have taken; one that is *looser* only costs a round trip. The
registry is also JSON Schema draft 2020-12 only and refuses other dialects
(`schema_dialect_unsupported`), so a client that validated a draft-07 document under 2020-12 rules
would be applying to it exactly the rules its author did not write it against.

Three facts about this codebase turned the obvious implementation into the wrong one:

- **`ajv` compiles each schema with `new Function`, and the page's policy is `script-src 'self'`
  with no `unsafe-eval`.** `index.html` says why: it is the backstop against a future template
  mistake, in an app that holds a bearer token and renders other tenants' schema text. Hosting
  `ajv` on the page means spending that backstop on a convenience — and `ajv` also compiles
  `pattern` as a regular expression, so a pathological one freezes the tab.
- **The registry preserves number literals verbatim.** `1.0` and `1` are different schemas
  (different ids), and `12345678901234567890` does not survive a round trip through a double.
  A Format button built on `JSON.stringify(JSON.parse(text))` would mint a different schema from
  the one the author wrote, silently.
- **The semantic-version rule compares against the highest label among *live* versions, not the
  label on `latest`** (`Subject.VerifySemanticVersion`). With v1 current at `1.0.0` and v2 held at
  the approval gate as `2.0.0`, the pointer says `1.0.0` and the registry says `2.0.0`.

## Decision

**1. Two grades of finding, and only one may block.** Every finding the editor makes is either a
*refusal* — a port of a rule the registry certainly applies, which may disable Register — or
*advice*, which never does. The refusals live in `domain/` as pure TypeScript
(`checkSchemaText`, `parseJsonDocument`, `parseSemanticVersion`) and are the intersection of what the
registry's canonicalisers and dialect check do: empty, over 512 KiB **in UTF-8 bytes**, not JSON
under `System.Text.Json`'s grammar, nested past 64 levels, a duplicate key, another dialect. JSON
Schema and Avro are both JSON and share them; Protobuf is a different grammar that is the
registry's to judge, so it gets the first two only.

`JSON.parse` is not used for this, because it accepts a duplicate key and has no depth limit — it
is *looser* than the registry. A small strict reader (`json-document.ts`) keeps the raw text of
every literal and its position, which also makes Format safe: it re-emits the source's own tokens
and changes nothing but whitespace.

**2. `ajv` runs in a Web Worker.** The worker's policy is the one on its own script, so the nginx
image serves `worker-*.js` with `script-src 'self' 'unsafe-eval'` and `connect-src 'none'`: the
eval it needs is confined to a context with no DOM and no network, and the page keeps its policy
untouched. A hung check ends in `worker.terminate()` after four seconds and the next check gets a
fresh worker. `ajv` is run permissively (`strict: false`, formats off) and everything it says is
advice: the registry registers `{"type":"bogus"}` without complaint, so a metaschema failure is
worth a second look and not a reason to refuse. A document declaring another dialect is not handed
to `ajv` at all. A sample message that cannot be judged — a `concordat://` reference only the
registry resolves, a `pattern` that does not compile — is *unavailable*, never *valid*.

**3. Monaco is a lazy chunk, self-hosted, that the textarea can always replace.** `SchemaEditor`
is a `ControlValueAccessor`: a `<textarea>` first, and Monaco when its chunk has arrived, taking
over the text, the caret and focus. If the chunk, its worker or its stylesheet cannot be fetched
the screen stays a textarea, says so in one sentence, and every check still works. Only what a
schema editor uses is imported (`monaco-bundle.ts`, built from the package's own
`features/<name>/register` entries), no comment toggling among them — a comment is a syntax error
to the registry. JSON is coloured by a 20-line tokenizer of our own, not Monaco's JSON language
service, which would be a second opinion about the document that accepts comments. Auto-closing
brackets is off, because input-method and dictation text arrives the way typing does and grew a
closing quote the author never typed.

**4. The stylesheet is a vendored file.** The application builder emits the CSS that a lazy chunk
imports as a file nothing links to, and `ng serve` papers over that by injecting it from the
module — so a production build rendered Monaco unstyled while the dev server, and every browser
test run against it, looked right. The build is told to drop CSS imported from JavaScript
(`loader: { ".css": "empty" }`), and `tools/vendor-monaco.mjs` copies Monaco's own prebuilt
stylesheet and icon font into `public/monaco/` under content-hashed names (the embedded `data:` font
becomes a file, since `font-src 'self'` refuses `data:`). `npm run monaco:check` fails CI when they
drift from the installed package.

**5. Registration takes two clicks, and says what it allocates.** A version is permanent: the
ordinal is never reused, the document cannot be edited, there is no delete. The first click names
`v{n}` and the subject and environment; the second registers. The confirmation is a snapshot of the
form, so editing anything afterwards withdraws it and the second click can only send what the first
showed. Version labels are *offered* (patch, minor, major from the highest live label) and never
applied: the right bump depends on a verdict only the registry has.

## Alternatives considered

- **Add `unsafe-eval` to the page's policy.** Rejected: it is the one directive the policy exists
  to withhold.
- **A validator that does not need eval** (`@cfworker/json-schema` interprets schemas rather than
  compiling them). A real option, and `DESIGN §9` and ADR-021 name `ajv`, so it was not taken
  unprompted. It would remove the need for the worker's `unsafe-eval` but not the case for the
  worker, which is the regular-expression timeout. Worth reopening if the worker's policy ever
  becomes awkward to serve.
- **`ajv` on the page's thread.** Rejected on both counts above.
- **Monaco from a CDN or its AMD loader.** Rejected for the reasons in ADR-026: a third-party
  request from a page inside somebody's firewall, and nothing at all when there is no route out.
- **Importing `monaco-editor`'s main entry.** Rejected: every feature and eighty languages, loaded
  by the first visitor to the screen.
- **Block the submit on `ajv`'s findings.** Rejected: it is a different engine from the registry's,
  and advice that can veto will eventually be wrong in exactly the strict direction.
- **Seed the label from `latest`.** Rejected: it offers `1.0.1` in the very situation where the
  registry will refuse it.

## Consequences

- **Positive:** the editor and the registry agree, and that is *tested*, not asserted:
  `e2e/schema-parity.spec.ts` sends about fifty documents in three formats to the registry's
  dry-run endpoint and compares its refusal with the editor's, in both directions. Its first run
  found one disagreement, in the test (JavaScript's `trim()` strips a byte-order mark that the
  registry's reader refuses).
- **Positive:** the initial bundle grew by 4.7 kB (405.9 → 410.6 kB raw, 102.5 → 104.3 kB
  transferred). Everything else loads on demand: Monaco 3.32 MB raw / 667 kB transferred, its worker
  307 / 75 kB, the checker's worker (with `ajv`) 136 / 36 kB, and the stylesheet and font 187 and
  153 kB. The screen's own chunk went from 9.7 to 50.2 kB.
- **Positive:** Format is safe on a document whose identity is a hash of its content, and a test
  asks the registry whether the formatted text has the id the original did.
- **Negative:** Monaco is large, and the editor is a worker, a vendored stylesheet and a
  cherry-picked import list that someone has to keep working when Monaco is upgraded. `monaco:check`
  catches the stylesheet; nothing but the browser suite catches a feature that quietly stopped being
  imported.
- **Negative:** `ajv` is CommonJS and the build warns about it on every run, so `angular.json`
  allows it, which also silences a warning about any later CommonJS dependency named `ajv`.
- **Neutral, and worth knowing:** the registry accepts a schema that is not valid under the 2020-12
  metaschema, and answers **500** (no Problem Details body) to a lone-surrogate escape such as
  `"\ud800"` in a string. The first is why `ajv`'s findings are advice; the second is a registry
  defect, recorded in [STATUS.md](../STATUS.md), and the editor does not pretend to know it.
- **Not covered:** the prototype's *auto-slugged id with a "touched" flag* belongs to creating a
  subject, and the web app has no screen that does (`POST /subjects` exists; nothing calls it), so
  there was nothing to put it on. *Compatibility tooltips* were not built.

## References

- [ADR-006](006-angular-port-strategy.md), [ADR-018](018-admin-only-schema-editing.md),
  [ADR-026](026-self-hosted-web-fonts.md), [ADR-029](029-web-container.md)
- [M4.4](../plan/M4-web-app.md), [DESIGN §9](../DESIGN.md#9-frontend-architecture-angular)
- `web/src/app/domain/registry/` (`json-document.ts`, `schema-document.ts`, `semantic-version.ts`),
  `web/src/app/features/registry/ui/` (`schema-editor.ts`, `monaco-*.ts`),
  `web/src/app/features/registry/data-access/` (`schema-check-*.ts`), `web/tools/vendor-monaco.mjs`
