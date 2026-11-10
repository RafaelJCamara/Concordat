# ADR-050: A held change can be read as a line diff, with the registry's findings on the lines they are about

- **Status:** Accepted
- **Date:** 2026-11-09
- **Deciders:** Rafael Camara

## Context

The approval queue ([ADR-042](042-the-approval-queue-is-answered-by-the-registry.md)) opens a held version onto what the
compatibility engine found, as a list (`#/properties/id`, `type_narrowed`, "Type(s) 'string' no longer permitted…"), and who
it would break. That answers *what kind of change is this*. It does not answer the question a reviewer is actually
asking before they press Approve, which is *what did the schema's author change*: the list names a place in the document
and the reviewer has to open two files and find it.

Three things were already in the app and one was missing:

- **The registry already reports where.** Every finding has a `path`, and a held version's diff
  (`GET /v1/environments/{env}/subjects/{name}/versions/{from}/diff/{to}`) carries both schema ids. `GET /v1/schemas/{id}`
  returns a document's text. So the data needs no new endpoint.
- **There is no text diff anywhere in the app.** "The diff page" is the findings list on its own screen
  (`CompatibilityDiffPage`); neither it nor the queue shows two documents side by side or one over the other. There was no
  second diff engine to avoid building, and none to reuse.
- **The three formats do not share a path syntax.** Probed against a running registry rather than assumed:
  JSON Schema reports `#/properties/id` (an RFC 6901 fragment pointer); Avro reports `#/fields/customer/fields/age`, which is
  *names*, not indexes (`AvroSchemaResolver`: reordering fields is compatible and would renumber an index path), and
  steps through the record that is a field's `type`; Protobuf reports `.acme.Order/1`, a fully qualified message and a field
  number, `.acme.Color/1` for an enum value, and the bare `.acme.Extra` for a message that is gone. A pointer resolver written
  for JSON would tie nothing in the other two.
- **`GET /v1/schemas/{id}` is not byte-for-byte what was sent.** For Protobuf it comes back reformatted (enum before message,
  one value per line), so a Protobuf diff is a diff of the registry's text, which is the right thing to review.

The schema editor ([ADR-028](028-schema-editor.md)) had already settled how heavy work is hosted: a Web Worker created with
`new Worker(new URL(…, import.meta.url), { type: 'module' })`, which the build names `worker-<hash>.js` and which the web
container serves under its own Content-Security-Policy (`docker/web/nginx.conf.template`), so the page's `script-src 'self'`
is untouched.

## Decision

**1. The review offers two views and remembers the choice.** A radio group, *Detailed* and *Summary*, at the top of an opened
review. *Summary* is exactly what the screen showed before and is the default, so a first visit changes nothing and an
unopened or Summary review fetches no schema and starts no worker. The choice is one value for the whole screen, kept in
`localStorage` under `concordat.approvals.review-view`, read and written inside `try`/`catch` like the theme: with storage
blocked the screen works and the choice lasts until the tab closes. It is a preference of this browser, not of the account;
the registry has no field for it.

**2. Detailed is a unified line diff, written here, running in a worker.**

- *The engine* is Myers' O(ND) algorithm with the common head and tail trimmed first, about 150 lines of pure TypeScript
  in `domain/registry/line-diff.ts`, with the canonicalisation, the folding and the finding↔line tie in
  `domain/registry/schema-diff.ts`. The Protobuf scanner that names declarations is `domain/registry/proto-outline.ts`.
- *Canonical form before diffing.* JSON Schema and Avro are re-emitted with two-space indent and object members in code-unit
  order, which is the order the registry's own canonical form uses ([ADR-015](015-content-addressed-ids.md)): a version
  that only reorders keys has the same schema id, and a diff that showed it would contradict the registry. Array order is
  kept. Number literals and string escapes are the source's own text, via the strict reader the editor uses
  (`json-document.ts`), so `1.0` against `1` *is* a difference, as it is to the registry ([ADR-028](028-schema-editor.md)).
  Protobuf is compared as text with line endings normalised and trailing blanks trimmed.
- *Unified, not side by side.* A split view needs twice the width to say the same thing and the reviewer may be on a phone;
  one column also gives a finding somewhere to sit, directly under its line at full width. Both layouts are cheap to
  *draw* and neither was cheap to make accessible in two columns, so one was chosen.
- *Unchanged context is folded* to three lines either side of a change, behind a real `<button>` ("18 unchanged lines")
  that opens it. A fold that would hide a single line is not made. A line a finding sits on is never folded away.
- *A change is marked by more than colour:* a `+`/`-` column, the words "Added:"/"Removed:" for a screen reader (the line
  numbers and signs are `aria-hidden`; the words are not), and a tint, in both themes, as the third signal. The diff scrolls
  inside a named, focusable region (`role="region"`, `tabindex="0"`) so the page never gains a horizontal scrollbar at
  390 px.
- *Schema text is rendered as text,* one element per line, never through `innerHTML`.

**3. A finding is tied to a line by the registry's own path, per format, and never guessed.** `locate(path)` returns a line of
the canonical document or `null`:

| Format | The registry's path | The line |
|---|---|---|
| JSON Schema | `#/properties/id`, `#/required` | the line the member starts on (`"id": {`), by walking the fragment pointer, RFC 6901 escapes included |
| Avro | `#/fields/customer/fields/age`, `#/fields/l/items` | the line that *names* the thing (`"name": "age",`), walking `fields/<name>` through a field's `type` — a record, a union of them, an array's `items` |
| Protobuf | `.acme.Order/1`, `.acme.Order/3/key`, `.acme.Extra` | the declaration with that name and number, from a scan of the text |

A finding goes on the held version's line when the path exists there, otherwise on the baseline's (so a finding about
something the change took away sits on its removed line). One the registry reports once per direction in the same words —
Avro and Protobuf do — is drawn once, naming both directions. A finding whose path is in neither document (a JSON Schema
`$defs` the registry's bundler synthesised, say) is **listed under the diff as "not tied to a line"** with its path;
the count is in the totals line. No finding is dropped and none is attached to a line it is not about.

**4. Large schemas are guarded, and a guard says so.** Everything runs in `schema-diff.worker.ts`; the page's thread sends two
strings and receives the lines on show. A comparison is refused (`too-large`) when a document is longer than **100 000
lines** or more than **1 000 lines would be added or removed**; the page then says which, in one sentence naming the limit,
and shows the Summary under it. The worker is terminated after **10 s** and restarted for the next request (the checker's
discipline), and a worker that cannot start is the same one sentence. Nothing is truncated: either every changed line is
on screen or the screen says that it is not. A review that falls back leaves the stored choice alone.

**5. The pieces.** `ReviewViewStore` (the choice), `SchemaDiffStore` (fetches the two schemas by id with `SchemasApi`, asks the
worker, holds the result or the problem), `SchemaDiffer` (the worker host, a token for tests), `PendingSchemaDiff`
(owns that store, created only under Detailed) and `cd-schema-diff` in `ui/` (presentational). `PendingReview`'s input is
unchanged (`[pending]="item"`).

## Alternatives considered

- **Monaco's diff editor.** Monaco is vendored ([ADR-028](028-schema-editor.md)) and has a diff editor and a diff worker.
  Rejected: the vendored bundle is cherry-picked and does not include it; a Monaco instance per opened row is a heavy
  thing to mount in a list; the findings would have to be view zones positioned against Monaco's own layout, and the
  collapsed-region and screen-reader behaviour would be Monaco's, not ours to test. The editor's lazy chunk is 3.3 MB; this
  feature's worker is 9.8 kB.
- **`diff` (jsdiff) or another npm package.** A real option: BSD-licensed, maintained, framework-free, and it would run in
  the worker. Rejected on merits, not on principle: it is tens of kilobytes of which this uses one function, it has no
  limit on how long it runs (the `maxChanges` guard is the part that matters here, and has to wrap whatever is used), and
  the part that is *ours* — canonicalisation, the fold, the finding tie — is most of the code either way. Reopen it if the
  engine ever needs word-level highlighting inside a changed line.
- **Diff the registry's canonical body instead of re-canonicalising.** There is no endpoint that returns it; the text from
  `GET /schemas/{id}` is what was registered (JSON, Avro) or the registry's reformatting (Protobuf).
- **Resolve findings against the document with a general JSON Pointer library.** Rejected: it fixes JSON Schema and leaves
  the other two formats untied, which is two thirds of the product.
- **Side-by-side as well.** Cheap to draw, expensive to make usable at 390 px and by a screen reader; deferred. The engine's
  output is the same for both.
- **Default to Detailed.** Rejected for a first visit: it changes a screen people already use, and costs two schema fetches
  for every row anyone opens. One click, remembered, is the price of asking.
- **Truncate a huge diff to its first N lines.** Rejected: a review that silently omits changes is worse than none.
- **Diff on the page's thread for small documents.** Rejected: two code paths, and the one that is not exercised in a
  large document is the one that will freeze when it is.

## Consequences

- **Positive:** a reviewer sees what moved, with the engine's finding on the line, in all three formats, without opening a
  file. The summary is untouched and is what stands in whenever the detail cannot be shown.
- **Positive:** measured, not asserted. Against a real registry in Chromium: a 423 KB JSON Schema (13.8 k canonical lines, 3
  changes) diffs in 63–111 ms inside the worker with **no main-thread block over 50 ms**; a 448 KB one of 51 k lines in
  129–309 ms, likewise; at 6× CPU slowdown the worker takes about 180 ms and the longest main-thread block is 155 ms (the two
  responses being parsed, and the rows rendered). In plain Node the worst case the limits allow — 1 000 changed lines in 51 k
  — is about 250 ms, and a document with nothing in common is refused after about 200 ms. The bundle gains one worker,
  `worker-<hash>.js`, 9.8 kB raw.
- **Positive:** `worker-<hash>.js` is what the container's policy for workers is keyed on, so the diff worker is served
  under it with no change to `nginx.conf.template`; it needs no `eval` and no network.
- **Negative:** the Summary on a change of thousands of findings (4 600 on the test fixture) is still a long list that takes
  about 0.5–0.9 s of main-thread time to render. It was before this change and is what the guard falls back to; paging or
  virtualising that list is a separate piece of work.
- **Negative:** a path the registry reports in a form this does not walk (a `$defs` entry that exists only in the bundled
  form, or an Avro path through a construct not seen in the probes) is listed as "not tied to a line", not placed. The cost
  of being wrong in the other direction is a reviewer trusting a finding on the wrong line, which is worse.
- **Negative:** Protobuf is scanned, not parsed. A document the registry would refuse yields whatever the scanner saw; the
  worst outcome is an untied finding.
- **Negative:** a stored choice of `detailed` means every opened row fetches two schemas and starts a worker. That is the
  point of the choice, and the Summary is one click away.
- **Neutral:** the preference is per browser. A reviewer on two machines chooses twice.

## References

- [ADR-015](015-content-addressed-ids.md) — canonical form and why key order is not a difference
- [ADR-028](028-schema-editor.md) — the worker, its policy, and the strict reader that keeps number literals
- [ADR-042](042-the-approval-queue-is-answered-by-the-registry.md), [ADR-043](043-a-subject-names-who-may-decide-its-held-versions.md) — the queue and who decides
- `web/e2e/approval-diff.spec.ts` — the browser spec; `web/e2e/README.md` — the fixtures it creates
- RFC 6901 (JSON Pointer)
