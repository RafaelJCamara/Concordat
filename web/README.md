# Concordat web

The Angular web application. Architecture: [DESIGN §9](../docs/DESIGN.md#9-frontend-architecture-angular),
decisions [006](../docs/adr/006-angular-port-strategy.md) and
[018](../docs/adr/018-admin-only-schema-editing.md), plan
[M4](../docs/plan/M4-web-app.md).

Read [NOTES-FOR-INTEGRATION.md](NOTES-FOR-INTEGRATION.md) first: it records what is a
placeholder, what needs a decision, and where M4.2 picks up.

## Running it

Requires Node `^22.22.3 || ^24.15.0 || >=26.0.0` — the floor the Angular 22 CLI enforces.

```bash
npm install
npm start          # http://localhost:4200, proxying /v1 to the API on :5062
npm run build
npm run lint
npm test
npm run codes:check   # fails if the error-code union has drifted from the .NET catalogue
npm run monaco:check  # fails if the vendored editor stylesheet is not the installed Monaco's
```

`npm start` expects `dotnet run --project src/hosts/Concordat.Api` to be up on port 5062;
the proxy is in `proxy.conf.json`.

### As a container

`docker/web.Dockerfile` builds the production bundle and serves it from nginx, and the compose
`registry` profile runs it beside the registry on <http://localhost:5063>. The image is the same
for every deployment: where the registry is, and what the page is told about it, are environment
variables read when the container starts (see the header of `docker/web.Dockerfile`), written
to a `config.json` the app fetches before it bootstraps. A static host can place one beside
`index.html` instead; `ng serve` has none and uses the defaults. `src/app/core/config/runtime-config.ts`
says what it accepts, and why a file that is present and wrong stops the app rather than being
ignored.

## The schema editor

`NewVersionPage` edits in Monaco, checks with `ajv`, and is built around one rule: **a check that
can block a registration must be one the registry would certainly make.** Everything the client
says is either a _refusal_ (a port of the registry's own rules, which may disable Register) or
_advice_ (which never does). `e2e/schema-parity.spec.ts` holds the first kind against the
registry's dry-run endpoint. See [ADR-028](../docs/adr/028-schema-editor.md) for the reasoning and
for what keeps Monaco and `ajv` out of the initial bundle.

## Layout

```
src/app/
  core/       http/ (auth, tenant, problem-details) · auth/ · config/
  shared/     ui/ (Spartan helm, generated) · pipes/
  domain/     registry/ identity/          — pure TypeScript, no Angular
  features/<context>/
      data-access/   the only place HttpClient appears; DTO ↔ domain mappers
      application/   SignalStore facade
      ui/            presentational only
      feature/       routed smart components
```

`eslint.config.mjs` enforces this. The rules are not advisory: `npm run lint` fails on a
`domain/` file that imports Angular, a routed component that reaches past its store into
`data-access`, a presentational component that touches a store, or a source file that
belongs to no layer.

## Design tokens

Everything visual comes from `src/styles/tokens.css`. No component names a colour. The
values there are a **placeholder** until the prototype's `index.css` is available — see the
banner at the top of that file.

## Adding a Spartan component

Helm components are source in this repository, like shadcn. The generator is a one-off
tool rather than a dependency, because it pulls the whole Nx toolchain in with it:

```bash
npm i -D @spartan-ng/cli
npx ng g @spartan-ng/cli:ui <name>   # reads components.json; writes into src/app/shared/ui
npm uninstall @spartan-ng/cli
```

Generated components are excluded from lint and Prettier. They are not hand-maintained —
an edit there is thrown away by the next generation.
