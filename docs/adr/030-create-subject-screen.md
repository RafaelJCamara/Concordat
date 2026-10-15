# ADR-030: A subject is created on a screen that says what is permanent, and blocks only on what the registry certainly refuses

- **Status:** Accepted
- **Date:** 2026-10-13
- **Deciders:** Rafael Camara

## Context

M4's exit criterion is "create a subject, register a version, view a diff and an impact report, and
approve a pending breaking change — entirely in the UI". Every step but the first was a screen.
`POST /v1/environments/{env}/subjects` existed, was tested, and nothing in the web app called it, so
the criterion had been recorded as met when it was not — and the subject list carried a "New subject"
button, absent for readers as ADR-018 asks, that a writer could press and nothing happened.

Creating a subject is the one action in the product with no undo. Probed against the real registry
rather than read from its code (`PATCH` answers 200 and changes nothing for these):

- **The name, the format and the content model cannot change**; a `PATCH` with a new one is ignored.
- **There is no delete**, and a retired subject keeps its name: creating it again answers 409.
  > **Amended 2026-10-15.** A retired subject can be deleted for good by an administrator, through the API only
  > ([ADR-046](046-a-retired-subject-can-be-deleted-for-good.md)), which frees the name. The confirmation says
  > "a retired one keeps its name taken", not "for good"; no screen offers the deletion.
- **Only the owner and the compatibility policy can change**, and only through the API — this app has
  no screen for either.

Three facts about the *rules* turned the obvious form into the wrong one, each found by sending the
case to the real create endpoint:

- **The registry trims before it validates**, so `" acme.orders "` is a valid name stored as
  `acme.orders`. Its set is `System.String.Trim()`'s, and JavaScript's `trim()` differs in both
  directions: it strips U+FEFF, which the registry does not (so a naive client is *looser*), and it
  does not strip U+0085, which the registry does (so it is *stricter*, the direction that blocks
  someone from doing something permitted).
- **Names are case-sensitive.** `orders.Created` and `orders.created` are two subjects, and the
  registry creates the second without a word.
- **A create has no dry run.** Every accepted name in a parity test is a subject that stays.

## Decision

**A routed screen, `/subjects/new-subject`, behind `schemaWriteGuard`.** The last segment is not `new`:
`new` is a valid subject name and the route would hide one. A hyphen is outside the name grammar, so
no subject can ever be named this, and it sits above `subjects/:name` in the route table. It is a
route and not a dialog because it is resource-creating, has its own URL to be linked from the empty
states and an incident channel, and a refused submit has to leave the form editable. The buttons
that lead to it read the same scope constant as the guard (ADR-018): absent, not disabled.

**Reached from where nothing exists yet.** The subject list's header action (now a link; it was a
button with no handler), and the empty list and empty dashboard, which carry "Create the first
subject" beside the sentence saying nothing is wrong. No sidebar row: it is an action on Subjects,
not a screen of its own, and Subjects stays marked on `/subjects/new-subject`.

**The form blocks only on what the registry certainly refuses** (ADR-028's rule). The refusals live in
`domain/` as pure TypeScript: `trimRegistryText`, `checkSubjectName`, `checkNewSubject`. They are the
registry's trim set (written as code-point ranges), its 512-character limit counted after the trim,
its grammar, the owner's presence and 256-character limit, and a compatibility policy that is whole
or absent — modelled as two selects that both default to "inherit", so that the registry's "both or
neither" rule has no separate switch to disagree with. The messages are the client's own and say
*which* character is wrong; the verdicts are the registry's. **`e2e/subject-name-parity.spec.ts`
holds every row against the real create endpoint**, including each character in the trim set and its
nearest neighbours that are not in it. Its first draft passed; the mutation of swapping in
JavaScript's `trim()` turns it red for exactly U+0085 and U+FEFF.

**Advice never blocks.** A name already in the list, and a name that differs from one only in case,
are shown as advice with a link to the subject. They are not refusals because the list is a
snapshot, and a check that vetoes on a snapshot is wrong in exactly the strict direction.

**Auto-slugged id, with the touched flag** (the prototype's, DESIGN §9). A *Title* — free text, not
stored, optional — suggests the subject name, and the name follows it until it is edited by hand
(set from the input's own `input` event, which programmatic updates do not fire; not Angular's
`touched`, which means blurred). Once edited, an offer to "use the suggestion instead" goes back to
following. The rule (`slugSubjectName`) is the least that makes text acceptable, derived from what
the registry refuses: a name that is already valid is returned unchanged; `+` and `:` become `.`
(`SubjectNormalizer`'s two single-character rewrites, pinned for every SDK, so a pasted nested-type
name lands where a .NET publisher will put it); Latin accents are folded; every other run of
characters becomes one `_` between words and is dropped at a segment's edge; empty segments are
dropped. **Case is never changed and words are never joined into a style**: choosing `OrderCreated`
over `order_created` is a convention the registry does not have, and a name is case-sensitive, so
guessing one is guessing which subject somebody meant. `SubjectNormalizer`'s comma cut and its
generics are *not* applied: the first is right for an assembly-qualified type name and wrong for a
title with a comma in it, and names the SDK derives are the SDK's to derive. `e2e` creates every
non-empty suggestion for a table of titles, so "the slug is a name the registry takes" is measured.

**Creating takes two clicks, and the second says what is permanent** — the prototype's immutable-id
confirmation. The first click names `name`, the environment, the format and the content model; the
second creates. The confirmation is a snapshot of *what would be sent* (not of the form: editing the
title does not withdraw it, and editing anything that is sent does), so the second click can only send
what the first showed. It says the owner and the policy can change *only through the API*, because a
confirmation that promised "you can change that later" would point at a button that is not there.

**Nothing is applied ahead of the registry.** The store holds the answer (`NewSubjectStore`,
`exhaustMap` — a double-clicked submit must not cancel an in-flight create) and the page navigates to
the *name in the answer*, since the registry trims and the stored name is the only one that opens. The
subject page it lands on reads the registry afresh; it is not handed a subject to show. A 409 renders
the registry's words and a link to the subject that holds the name.

## Alternatives considered

- **Trim with `String.prototype.trim()`.** The obvious thing, and wrong in both directions above.
- **`/subjects/new`.** Hides a subject named `new`, which the registry accepts.
- **A dialog on the list.** No URL, so the empty states cannot link to it and a reload loses the form;
  and a refused submit would sit in a modal over the list that has not changed.
- **Make "name already exists" a refusal.** The list is a snapshot; the registry's 409 refuses it.
- **A single "Name" field, no title.** Nothing to slug from, so the prototype's flag has nothing to
  mean. The cost of the title is a field the registry does not keep, which the screen says.
- **Join words in PascalCase.** Looks like the repo's own examples and is a convention, not a rule;
  see above.
- **Slug through all of `SubjectNormalizer`.** See above: half of it is wrong for words.
- **Predict the created subject and navigate before the answer.** The stored name can differ from the
  typed one.

## Consequences

- **Positive:** M4's exit criterion is met end to end in a browser (`new-subject.spec.ts` creates a
  subject on this screen and registers a version on the next, with nothing arranged over HTTP).
- **Positive:** the real registry settled the whitespace set *before* any code depended on it. I had
  expected U+001C to U+001F to be stripped, as I half-remembered of .NET's white-space set; the
  registry refuses them, and so the table has the information separators on the other side of the
  line with a row each. The same probes found the second registry 500 below.
- **Positive:** creating a subject taught the e2e suite something it was assuming. A subject the
  browser creates is owned by the signed-in account, so that email now appears in a table cell on the
  list as well as in the sidebar, and `getByText(email)` stopped being unique — in five places. They
  look in `cd-app-sidebar` now.
- **Negative:** the e2e suite now creates permanent things. Two subjects per run in `dev`; a fixed
  table in an environment of its own, `e2e-names`, that is created once and is a no-op thereafter;
  and `e2e-empty`, which must stay empty.
- **Negative:** the Title is a field the registry does not store, and the confirmation has to say that
  the owner and policy cannot be edited in this app.
- **Found on the way, and worse than a 500:** `compatibilityMode: "99"` (any integer string reaches
  `Enum.TryParse`) was *stored*, answered 500 while projecting its own response, and then broke every
  `GET …/subjects` in the environment. The form sends tokens from a select and cannot reach it, but the
  endpoint it calls could be poisoned by one request, and five other parses had the same shape. They
  refuse an undefined number now (`WireEnums`, [STATUS.md](../STATUS.md), decision 38). A number that
  *is* a member (`"2"` is `BACKWARD`) is still read as one; a comma list, which used to mean a third
  member, is refused.
- **Not covered:** a registry reached across origins (as ADR-029); touch input; right-to-left text in
  the name field; and the compatibility tooltips the prototype also had.

## References

- [ADR-011](011-subject-is-message-type.md), [ADR-018](018-admin-only-schema-editing.md),
  [ADR-028](028-schema-editor.md), [ADR-031](031-unpaired-surrogates-are-refused.md)
- [M4.3](../plan/M4-web-app.md), [DESIGN §9](../DESIGN.md#9-frontend-architecture-angular)
- `web/src/app/domain/registry/` (`registry-text.ts`, `subject-name.ts`, `new-subject.ts`),
  `web/src/app/features/registry/` (`feature/new-subject-page.ts`, `application/new-subject-store.ts`,
  `ui/create-subject-confirmation.ts`), `web/e2e/subject-name-parity.spec.ts`, `web/e2e/new-subject.spec.ts`
