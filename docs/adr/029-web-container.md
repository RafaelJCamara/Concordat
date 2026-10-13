# ADR-029: The web app is an nginx image, configured when it starts

- **Status:** Accepted
- **Date:** 2026-10-13
- **Deciders:** Rafael Camara

## Context

Evaluating Concordat meant two processes: the registry from compose, and the UI from `npm start`,
which needs Node, a checkout and the registry's port hard-coded in `proxy.conf.json`. The promise
[ADR-008](008-built-in-identity.md) makes — compose up, and try it — stopped at the API.

The app has also never been served the way it ships. Every browser test ran against `ng serve`,
which has no response headers, no `config.json`, no content-hashed files and no minifier, and the
first time the production build met the app's own Content-Security-Policy it was wrong in four
ways, none of which any test had a chance to see:

- Angular's production build inlines critical CSS as
  `<link media="print" onload="this.media='all'">`. `script-src 'self'` forbids inline event
  handlers, so the handler was blocked and the stylesheet stayed on `print`.
- The editor's stylesheet, which a lazy chunk imports, was emitted and never linked
  ([ADR-028](028-schema-editor.md)).
- A subject whose name ends `.json` — subjects are dotted, so that is an ordinary name — came back
  **404** from nginx, which had matched it against the rule that serves static files by extension.
- The minifier writes `.625rem` for `0.625rem`, which a test comparing strings called a regression.

[`ConcordatConfig`](../../web/src/app/core/config/app-config.ts) already promised that the same
bundle is served by every deployment and that where the API lives is not compiled in; nothing
supplied it.

## Decision

`docker/web.Dockerfile` builds the production bundle and serves it from the unprivileged nginx
image (digest-pinned, like the others). The compose `registry` profile gains a `web` service on
**`127.0.0.1:5063`** — loopback, like everything else in that file, because it is a console for an
instance that may still be unclaimed.

**Same origin by default.** nginx forwards `/v1` and `/health` to the registry, so the browser only
ever talks to one origin: no CORS, a first-party session cookie, no credential that can be sent
anywhere else, and a `connect-src` of `'self'`. This is the arrangement `ng serve` already has in
`proxy.conf.json`.

**Configured by the environment when the container starts, not by the image.**
`CONCORDAT_API_UPSTREAM` (where nginx forwards), and `CONCORDAT_API_BASE_URL`, `CONCORDAT_PROFILE`,
`CONCORDAT_TENANT`, `CONCORDAT_DEFAULT_ENVIRONMENT` (what the page is told). A start-up script
validates them against a strict alphabet — they reach `config.json` and a response header, and
decide where a bearer token is sent — writes `/config.json`, and **refuses to start** on a bad value
rather than correcting it. The app fetches `config.json` before it bootstraps
(`runtime-config.ts`): *absent* means the defaults, so `ng serve` and a static host still work;
*present and wrong* stops the app with a message, because falling back to the defaults would point a
deployment someone configured at the wrong place without saying so.

**The policy moves from a `<meta>` tag to a response header** computed from the same environment.
The tag is stripped from `index.html` in the image build — and the build fails if the strip stops
matching — because the browser intersects the two, so a retained `connect-src 'self'` would veto
a cross-origin API the header allowed. The header also says what a tag cannot
(`frame-ancestors 'none'`). Content-hashed files are cached for a year, `index.html` and
`config.json` not at all, and a missing file is a 404, never `index.html`.

**The workers get their own policy.** `worker-*.js` is served with `script-src 'self' 'unsafe-eval'`
and `connect-src 'none'` — see ADR-028 for why the schema checker needs the eval and why that is
safe there. This is the only place in the image that permits it.

**The build stops inlining critical CSS** (`optimization.styles.inlineCritical: false`). The stylesheet
is an ordinary render-blocking `<link>`: about 59 kB, same-origin, cached for a year.

**CI builds the image and runs `scripts/smoke-web-image.sh`** against it, and `Publish images` runs
it against the pushed image before promoting it to `latest`. The script is the list of things that
were wrong above, as assertions, plus: the image starts with no registry present (a 502 on `/v1`,
not a container that will not start), refuses a bad environment, has no inline handler or script in
`index.html`, serves the worker's policy and not the page's, and forwards `…/subjects/orders.json`.

## Alternatives considered

- **Serve the SPA from the API image** — what [DESIGN §10](../DESIGN.md) sketches for self-hosted
  ("one image serving API + embedded SPA"). Still the end state, and nothing here prevents it; but
  it couples the registry's release to the UI's, needs the .NET build to run the Angular build, and
  was a much larger change than "bring up both".
- **A different origin for the API, configured in the bundle.** Rejected: the premise of
  `ConcordatConfig`. It is supported — `CONCORDAT_API_BASE_URL` takes an origin, and the header's
  `connect-src` widens to it — but the API then needs `Concordat:WebOrigin` for CORS and a cookie
  policy that works cross-site, and it is not the default for that reason.
- **Keep the `<meta>` tag as well.** Rejected: see above, the intersection.
- **`envsubst` into `index.html` or a generated `config.js`.** A script tag would need to be
  inline or a second file; `config.json` is data, not code, which is the property worth keeping
  beside a policy that forbids inline script.
- **A compose `build:` section instead of an image.** Rejected for consistency: the registry is
  `image: ghcr.io/…` with an override variable, and a first `docker compose up` that begins by
  running `npm ci` is a worse first impression than one that pulls.

## Consequences

- **Positive:** `docker compose --profile registry up -d` brings up the registry and the UI, on
  loopback, with nothing installed but Docker.
- **Positive:** the browser suite now runs against what ships
  (`CONCORDAT_WEB_URL=http://localhost:5063 npm run e2e`), which is how the four defects above were
  found, and CI's `web-container` job keeps them found.
- **Positive:** the page's policy has one source, and it is the same one that decides what the page
  may connect to.
- **Negative:** one same-origin request (`config.json`) precedes the first render. Small, serial,
  and the reason two design-system tests had to wait for an element instead of counting on arrival.
- **Negative:** both workers share a policy that permits `eval`, because the build gives them
  indistinguishable names. The editor's worker does not need it and has no network to misuse it with.
- **Not verified:** a deployment with `CONCORDAT_API_BASE_URL` set to another origin was checked at
  the level of `config.json`, the rendered header and the validation, not by signing in across
  origins; the API's cookie settings for that arrangement are untested here. The `Publish images`
  and CI changes parse as YAML and have not been run by GitHub.

## References

- [ADR-008](008-built-in-identity.md), [ADR-026](026-self-hosted-web-fonts.md),
  [ADR-027](027-read-requires-authentication.md), [ADR-028](028-schema-editor.md)
- `docker/web.Dockerfile`, `docker/web/`, `scripts/smoke-web-image.sh`,
  `web/src/app/core/config/runtime-config.ts`
