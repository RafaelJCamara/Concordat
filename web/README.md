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
```

`npm start` expects `dotnet run --project src/hosts/Concordat.Api` to be up on port 5062;
the proxy is in `proxy.conf.json`.

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
