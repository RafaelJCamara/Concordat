# ADR-006: The Angular port keeps the prototype's design system and rebuilds the code

- **Status:** Accepted
- **Date:** 2026-08-13
- **Deciders:** Rafael Camara

## Context

A React prototype (Vite + shadcn/ui) already sketches the intended UX. Its design tokens
live in `index.css` as CSS custom properties, which are framework-agnostic. Its component
and state layer is not: an 871-line "Schemas" god component, uncontrolled `defaultValue`
forms, two competing HTTP paths where one always 404s and silently swallows the create,
regex-based syntax highlighting through `dangerouslySetInnerHTML` — an XSS hole — and a
large installed-but-unused dependency surface.

## Decision

Port the token set verbatim. Rebuild the component and state layer in Angular 22 with
standalone components, signals and `@ngrx/signals`, using Spartan UI — `@spartan-ng/brain`
plus `helm` components generated into the repo as source, the direct analogue of shadcn.

## Alternatives considered

- **Keep React, skip the port.** Rejected: the backend and CLI are .NET, and a single
  language across server and web reduces the context switch for a solo maintainer.
- **Port the components mechanically, fix later.** Rejected: it would carry the XSS hole
  and the god component into the new codebase, where "later" never arrives.
- **Rebuild the design system too.** Rejected: the tokens are the part that is already
  right, and redoing them costs visual coherence for no benefit.

## Consequences

- **Positive:** the prototype stays a live visual reference. Spartan's token contract means
  `index.css` transfers without translation.
- **Positive:** specific defects get fixed as part of the port rather than inherited —
  Monaco replaces the regex highlighter, one typed data-access layer replaces the two HTTP
  paths, reactive forms replace uncontrolled inputs, and a light theme is added.
- **Negative:** Spartan UI is younger and smaller than shadcn; some components may need
  writing by hand.
- **Negative:** the prototype is maintained separately, so it can drift from the port.

## References

- [DESIGN §9](../DESIGN.md#9-frontend-architecture-angular)
- [M4 — Angular web app](../plan/M4-web-app.md)
