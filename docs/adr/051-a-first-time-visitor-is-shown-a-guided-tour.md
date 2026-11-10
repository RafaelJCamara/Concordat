# ADR-051: A first-time visitor is shown a guided tour, and it is built here

- **Status:** Accepted
- **Date:** 2026-11-09
- **Deciders:** Rafael Camara

## Context

The web app has seven screens and a vocabulary that is not obvious from outside it: a _subject_ is a
message type, a version that would break its readers is _held_ rather than published, a _contract_ ties
a route to subjects, and a service signs in with an _API key_ where a person uses a password. Somebody who
has just been given an account (ADR-008, ADR-047) lands on the dashboard with nothing that says where
to go next, and the people most likely to be in that position are evaluating a self-hosted registry on
their own.

A guided tour is the usual answer, and it has three ways of going wrong that matter here:

- **It is shown to people who are not in the app.** The setup screen and the sign-in form are the first
  thing a new visitor sees, and a tour that opens over them is pointing at navigation that is not drawn
  (ADR-047). The forced password change (ADR-048) is the same: the only screen that works is the one in
  front of the person.
- **It stands between a person and the page.** The common implementation is a full-window overlay that
  takes the click, a focus trap that holds Tab inside a popover, and `aria-modal`. For a keyboard or
  screen-reader user that is a page that has stopped responding, and it is exactly what the app's
  accessibility rules (every control a real button, visible focus ring, no trap) exist to prevent.
- **It breaks the policy.** The page is served under a strict Content-Security-Policy
  ([ADR-029](029-web-container.md)): `script-src 'self'` with no inline script and no `eval`, and
  `style-src 'self' 'unsafe-inline'` that exists because Angular injects component styles, not as a
  licence for new code to lean on it.

The requirement is therefore narrower than "add a tour": it starts the first time and again after a
cache clear, never over the sign-in screens, can be left at any step and replayed, never traps or blocks,
anchors to stable attributes, copes with a missing target and with a phone, and works under the policy
unchanged.

## Decision

**Concordat builds the tour itself**: `TourStore` (`core/config/`), a presentational `TourPopover`
(`shared/ui/tour/`), a handful of pure functions beside each, and `data-tour` attributes on the things it
points at. No dependency is added.

**What it does.** The steps tell the story in one order: the dashboard (the state of the registry),
subjects (message contracts), approvals (breaking changes wait for a decision), contracts (what a route
enforces), settings, API keys (how services and pipelines authenticate), and the button that replays it.
Every step is anchored to a `data-tour="…"` attribute (`nav-dashboard`, `nav-subjects`, …,
`settings-api-keys`, `help`), never to a class. The API keys step lives on another screen, so the tour goes
there and, when it ends, goes back to where the person was (if they are still where it left them). It is
only planned for a session that holds `org:admin` (ADR-018), because the router would turn anyone else away.

**What is shown is decided once, before the first step.** The plan keeps the steps whose target is on the
page and takes up room, and the counter ("Step 2 of 6") counts only those. A step that cannot be shown is
never mentioned. A target that does not turn up within three seconds of arriving at its screen is dropped
and the counter shrinks to match.

**When it starts.** `inTheApp()` is true only for a signed-in session on an instance that has been
claimed, that is not holding a password an administrator typed, and that is not on `/sign-in` (which is also
the setup screen). The shell asks `TourStore.autoStart()` after the render that draws the rail; the store
refuses if this browser has seen the tour or if it has already started in this page load.

**Seen is `localStorage` `concordat.tour.seen = "1"`**, written when the tour starts (a person who closes the
tab on step two has seen it), read once, and wrapped in `try`/`catch` on every access, including the `localStorage`
property getter itself, which throws when site data is blocked. Clearing site data removes it, which is the
behaviour asked for. With storage blocked the app works and the tour shows once per page load, not on every
navigation, because `startedThisLoad` is separate from `seen`.

**Replay** is a "Take the tour" button in the sidebar footer, once someone is in the app. It is an icon with
an `aria-label` and `title` on the collapsed rail, which is how a phone sees it.

**It is non-modal.** The ring is `pointer-events: none` and `aria-hidden`; the card is a `role="dialog"` named
by the step's title and described by its text, with no `aria-modal` and no `inert` on anything. Opening moves
focus to the card; Tab then walks Skip, Back, Next and on out of it into the page, with no handler on Tab.
Escape skips, but only when focus is on the card or on nothing in particular, so an Escape meant for the
editor or a menu is not taken. Each new step is announced from a polite live region while focus stays on the
button that was pressed; if that button is gone (Back on the first step) focus goes back to the card. When the
tour ends focus returns to what had it, if it is still there. Motion is `motion-safe:` only.

**Phone width.** Below 768px the card is a sheet along the bottom of the window (along the top when the target
is in the lower half, so it is never on top of what it describes), the ring is still drawn around the collapsed
rail's icon, and the sidebar is not opened. A step with no target at all would be centred.

**It works under the policy as it is.** Nothing is loaded, nothing is evaluated, and no inline style attribute is
written: positions are set through the CSSOM (`element.style.left = …`), which a `style-src` without
`'unsafe-inline'` still permits. This was checked against the header from `docker/web/nginx.conf.template`, and
against a stricter one with `'unsafe-inline'` removed from `style-src` (see Consequences).

**The existing browser suite is not disturbed.** A tour that opens by itself would put a card over the first page
of every spec. `global-setup.ts` writes the seen flag into the saved owner and reader states it signs in for,
and `signIn()` writes it before signing in, so every spec starts as a browser that has seen the tour. `tour.spec.ts`
is the one spec that removes it, from a copy of the saved state, and so costs no sign-in.

## Alternatives considered

- **intro.js (8.6.0).** AGPL-3.0. The project is Apache-2.0 including its hosted form ([ADR-009](009-apache-2-including-cloud.md));
  linking a copyleft library into the bundle that is served to users is not compatible with that, and the
  commercial licence is a purchase for a feature that is a few hundred lines.
- **Shepherd.js (15.3.0).** AGPL-3.0 or a commercial licence "required for commercial products and
  revenue-generating companies". The same objection, and it brings `@floating-ui/dom` and `deepmerge-ts` with it
  (814 kB unpacked).
- **driver.js (1.11.0).** The only candidate whose licence fits (MIT), and the best of them on every other
  measure: 38.8 kB minified, **11.2 kB gzipped** plus 1.3 kB of CSS, released yesterday, one open issue, no
  dependencies, and it positions through `element.style` so it passes the policy. It loses on the two behaviours the
  requirement forbids. Its shipped source has a Tab handler that cycles focus among the popover's buttons and the
  highlighted element, and its overlay is a full-window SVG that takes pointer events and closes the tour on a click
  (`overlayClickBehavior: 'close'` by default). Both can be turned down by configuration only in part, and the result
  would be a modal library run non-modally. It also has no phone layout, no way to say "this step is on another
  screen and only for some roles" except through callbacks that re-implement the planner, and a visual design that
  would have to be overridden to match the app's tokens in both themes. It was read, not run inside the app.
- **A directive per anchor (`cdTourStep`) that registers itself.** Rejected for the steps' location: the tour
  is one story, and its order and its words belong in one list beside the code that plans it, not scattered over seven
  templates. The anchors are attributes, which is the half of that idea worth keeping.
- **Start on the server's say-so (a flag on the account).** Rejected: the requirement is that clearing the browser
  brings the tour back, which is a property of the browser, and it would add an endpoint and a migration to a
  screen-only feature.

## Consequences

- **Positive:** no new dependency, licence or supply-chain surface; the tour follows the app's tokens, so light and
  dark need nothing extra (every step measured at 4.5:1 or better in both).
- **Positive:** the rules (when it starts, which steps show, where the card goes) are plain functions with unit tests,
  and the behaviour a keyboard or screen-reader user depends on is asserted in a real browser.
- **Cost:** about 12 kB raw, 4 kB gzipped added to the initial bundle (a build with the tour against one without; other
  edits to the tree landed between the two, so it is an upper bound), against 12.5 kB gzipped for driver.js. The
  tour is in the main bundle and not lazy-loaded, because it decides whether to start on the first render.
- **Negative:** ours to maintain: positioning, scroll and resize handling and the live region are code a library would
  have carried. Placement is a pure function with its own tests for exactly that reason.
- **Negative:** the tour is not shown to an open, unclaimed instance, which has no session to be signed in with; that is
  deliberate (it is a state to be closed, not a place to be shown around), and the way to the tour appears once an account
  exists.
- **Negative:** `TourStore` goes to the router for the API keys step, so it is the one thing in `core/config/` that
  navigates. It only ever goes to a route the session is permitted, and returns the person afterwards.
- **Neutral:** `ThemeStore` reads `document.defaultView.localStorage` unguarded when it is built, so a browser that
  throws from that getter fails there before the tour is reached. The tour does not rely on it and guards its own
  read; the theme store is left as it was.
- **Neutral:** `e2e/.auth` was shared by every run, and two runs against two registries at once started from each
  other's sessions. `CONCORDAT_E2E_AUTH_DIR` moves it.

## References

- [ADR-009](009-apache-2-including-cloud.md), [ADR-018](018-admin-only-schema-editing.md),
  [ADR-026](026-self-hosted-web-fonts.md), [ADR-029](029-web-container.md),
  [ADR-047](047-a-new-instance-is-closed-until-it-is-claimed.md),
  [ADR-048](048-an-account-can-change-its-password-be-reset-and-be-disabled.md)
- `web/src/app/core/config/tour.ts`, `tour-store.ts`; `web/src/app/shared/ui/tour/`;
  `web/e2e/tour.spec.ts`; `docker/web/nginx.conf.template`
