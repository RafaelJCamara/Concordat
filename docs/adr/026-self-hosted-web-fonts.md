# ADR-026: The web app serves its own fonts

- **Status:** Accepted
- **Date:** 2026-08-15
- **Deciders:** Rafael Camara

## Context

[ADR-006](006-angular-port-strategy.md) ports the React prototype's design system verbatim,
and the prototype's typography is two Google Fonts families — Inter for the interface and
JetBrains Mono for schema text. The prototype loads them the way a Lovable-generated Vite app
does, with an `@import url('https://fonts.googleapis.com/…')` at the top of `index.css`.

Concordat is not that kind of application. It is a self-hosted schema registry: `docker/`
builds an image, `deploy/compose` runs it beside PostgreSQL and RabbitMQ, and the profile
[DESIGN §10](../DESIGN.md) calls single-user self-hosted is somebody running it inside their
own network. An install with no route to the public internet is an ordinary deployment, not
an edge case.

Carrying the prototype's font delivery across therefore has three consequences the prototype
never had to care about:

- On a network that cannot reach `fonts.gstatic.com`, every screen renders in fallback faces.
  The monospace matters here — a dotted subject name is read character by character, and
  `acme.orders.v1` against `acme.0rders.v1` is exactly the confusion a proportional fallback
  creates.
- Every reader's IP address is sent to a third party on a page served from inside the
  customer's own firewall. The Munich regional court (LG München I, 3 O 17493/20, January
  2022) found that embedding to be an unlawful transfer under GDPR absent consent; whether or
  not that reasoning travels, "our registry console phones Google on every page load" is a
  question a self-hosting customer is entitled to ask and we would rather answer with "it
  does not".
- It puts a service nobody here operates on the critical path of the first paint.

There is a fourth, subtler problem specific to Angular: `ng build` rewrites a Google Fonts
`<link>` to inline and self-host the files, and `ng serve` does not. Production and
development would have differed precisely where someone is most likely to be looking, so the
defect would have been invisible to whoever was best placed to notice it.

## Decision

The web app vendors Inter and JetBrains Mono into `web/public/fonts/` and declares them in
`web/src/styles/fonts.css`. `index.html` links no external stylesheet and preconnects to no
external host. `web/tools/vendor-fonts.mjs` (`npm run fonts:vendor`) regenerates both the
`.woff2` files and the `@font-face` block, so the committed binaries are reproducible rather
than mysterious.

Variable fonts, one file per Unicode subset, covering the full weight range the design uses —
300–700 for Inter, 400–600 for JetBrains Mono. Every subset Google serves is vendored, and
`unicode-range` keeps that from costing anything: a Latin reader fetches 78 kB of the 278 kB
on disk, and the Cyrillic and Greek are there for the day a subject owner is not written in
ASCII.

## Alternatives considered

- **Keep the prototype's Google Fonts link, unchanged.** Rejected for the three consequences
  above. This is the one place the port deviates from "verbatim", and the reason is that the
  prototype was a hosted demo and the product is not.
- **Rely on Angular's build-time font inlining.** Rejected: it only runs for a production
  build, so `ng serve` — where the E2E suite runs and where developers spend their day —
  would still hit Google. It also silently *needs* network access at build time, which moves
  the air-gap problem from run time to CI rather than removing it.
- **Drop the webfonts and use a system stack.** Rejected: the typography is part of the
  design system ADR-006 exists to preserve, and the monospace is load-bearing rather than
  decorative. A system stack also renders differently on every OS, which is the opposite of
  the coherence a token set buys.
- **Vendor only the `latin` subset.** Rejected as a false economy: `unicode-range` already
  means unused subsets are never requested, so the saving is disk, and the cost is a
  registry that renders a Cyrillic owner name in a fallback face.
- **Serve the fonts from the API rather than the static bundle.** Rejected: they are static
  assets of the web application, and routing them through the registry would make the
  console's typography depend on the API being up.

## Consequences

- **Positive:** the console renders identically with no internet access, which is what a
  self-hosted product should do.
- **Positive:** no third-party request from a page inside a customer's network, so there is
  nothing to disclose in a data-processing questionnaire.
- **Positive:** development and production now deliver fonts the same way, so a font problem
  is visible to whoever caused it.
- **Positive:** the E2E suite no longer depends on a host nobody here controls — see
  `design-system.spec.ts`, which previously had to exclude third-party responses from its
  "no failed request" assertion.
- **Negative:** 278 kB of binary files in the repository, and a manual step
  (`npm run fonts:vendor`) to pick up an upstream release. Neither is checked in CI, because
  a font does not drift the way a generated code catalogue does — it is byte-identical until
  somebody deliberately re-runs the tool — and a CI job that fetched from Google on every
  build would reintroduce exactly the dependency this removes.
- **Neutral, and an obligation rather than a footnote:** both families are SIL Open Font
  License 1.1, which permits redistribution and **requires** in section 2 that the copyright
  notice and licence accompany the files. Vendoring makes this project a redistributor, so
  `inter-OFL.txt` and `jetbrains-mono-OFL.txt` sit in `web/public/fonts/` beside the
  `.woff2` files — served at `/fonts/`, so they reach anyone who receives the fonts — and
  `NOTICE` records both. Verbatim and separate rather than merged: the two published texts
  differ in small ways (licence URL, "AND" versus "&"), and a redistributor's job is to pass
  a licence on unedited, not to tidy it.

## References

- [ADR-006](006-angular-port-strategy.md) — the port keeps the design system
- [DESIGN §9](../DESIGN.md#9-frontend-architecture-angular), [DESIGN §10](../DESIGN.md)
- `web/src/styles/fonts.css`, `web/tools/vendor-fonts.mjs`
