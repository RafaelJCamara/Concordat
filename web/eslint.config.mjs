import js from '@eslint/js';
import angular from 'angular-eslint';
import boundaries from 'eslint-plugin-boundaries';
import tseslint from 'typescript-eslint';

/**
 * The architecture in DESIGN §9, as a lint rule.
 *
 * Written down here because a folder structure is a convention until something enforces it,
 * and the failure it prevents is specific: a routed component that reaches past its store
 * into `data-access` to save one method, a presentational component that starts fetching,
 * a `domain/` type that imports `@angular/core` and stops being portable. Each of those is
 * one reviewer's inattention away, and each is expensive to undo once a second file copies
 * it.
 *
 * Elements are matched by path, so the rule follows the folders rather than a naming
 * convention someone can forget.
 */
const elements = [
  { type: 'core', pattern: 'src/app/core/*', capture: ['area'], partialMatch: false },
  { type: 'domain', pattern: 'src/app/domain/*', capture: ['context'], partialMatch: false },
  { type: 'shared', pattern: 'src/app/shared/*', capture: ['kind'], partialMatch: false },
  {
    type: 'data-access',
    pattern: 'src/app/features/*/data-access',
    capture: ['context'],
    partialMatch: false,
  },
  {
    type: 'application',
    pattern: 'src/app/features/*/application',
    capture: ['context'],
    partialMatch: false,
  },
  { type: 'ui', pattern: 'src/app/features/*/ui', capture: ['context'], partialMatch: false },
  {
    type: 'routed',
    pattern: 'src/app/features/*/feature',
    capture: ['context'],
    partialMatch: false,
  },
];

/**
 * The composition root.
 *
 * A *file* category rather than an element, because these four files sit loose in `src/`
 * and `src/app/` — an element pattern matching them would have to be a folder pattern, and
 * the only folders available are `src` and `src/app`, which contain everything else too.
 */
const compositionRoot = [
  {
    category: 'composition-root',
    pattern: [
      'src/main.ts',
      'src/app/app.ts',
      'src/app/app.spec.ts',
      'src/app/app.config.ts',
      'src/app/app.routes.ts',
    ],
  },
];

/** Restricts a dependency to the same bounded context as the file importing it. */
const sameContext = { context: '{{ from.element.capturedValues.context }}' };

export default tseslint.config(
  {
    ignores: [
      'dist/**',
      '.angular/**',
      'node_modules/**',
      // Helm components are generated, not authored (ADR-006). Linting them means editing
      // them, and the next `ng g @spartan-ng/cli:ui` overwrites the edit — so the lint would
      // be a recurring, meaningless diff. They are excluded here rather than suppressed
      // file-by-file so it is obvious that nothing in this tree is hand-maintained.
      'src/app/shared/ui/*/src/**',
      'tools/**',
    ],
  },

  {
    files: ['**/*.ts'],
    extends: [
      js.configs.recommended,
      ...tseslint.configs.recommended,
      ...angular.configs.tsRecommended,
    ],
    // Type-aware linting. It costs a TypeScript program per run, and it buys rules that
    // cannot work without one — `no-uncalled-signals` in particular, which catches a signal
    // read without `()` in a template. That mistake renders the function's source text into
    // the page and is invisible in review.
    languageOptions: {
      parserOptions: { projectService: true, tsconfigRootDir: import.meta.dirname },
    },
    // Makes the template rules below apply to `template:` strings too. Without it, every
    // accessibility rule silently covers only the components that use a separate .html file.
    processor: angular.processInlineTemplates,
    plugins: { boundaries },
    settings: {
      'boundaries/elements': elements,
      'boundaries/files': compositionRoot,
      // Every source file must belong to an element. A file nobody classified is a file no
      // policy applies to, which is how an unenforced corner of the tree starts.
      'boundaries/include': ['src/**/*.ts'],
      /*
       * Not optional, and not obvious. The plugin resolves each import to a file before it
       * can classify it, and the default node resolver does not know about `.ts` or about
       * tsconfig `paths`. Without this the resolution silently fails, every dependency looks
       * "unknown", and the whole policy list below matches nothing — the rule reports zero
       * violations and looks like it is working. That failure mode is why
       * `no-unknown-dependencies` is switched on below rather than left off.
       */
      'import/resolver': { typescript: { project: 'tsconfig.json' } },
    },
    rules: {
      '@angular-eslint/component-selector': [
        'error',
        { type: 'element', prefix: 'cd', style: 'kebab-case' },
      ],
      '@angular-eslint/directive-selector': [
        'error',
        { type: 'attribute', prefix: 'cd', style: 'camelCase' },
      ],
      /*
       * On top of `tsRecommended`, and only these. Extending `tsAll` and switching a dozen
       * rules back off would mean every angular-eslint minor release could break the build
       * with a rule nobody chose.
       */
      // Signals in, decorator `@Input`/`@Output` out. Mixing both makes it impossible to
      // tell at a glance which components have been migrated, and the port is the moment to
      // settle it.
      '@angular-eslint/prefer-signals': 'error',
      // A signal read without `()` in a template renders the function's source text, silently.
      '@angular-eslint/no-uncalled-signals': 'error',
      // Zoneless already implies it, but stating it means a component that opts out has to
      // say so deliberately.
      '@angular-eslint/prefer-on-push-component-change-detection': 'error',

      '@typescript-eslint/no-unused-vars': [
        'error',
        { argsIgnorePattern: '^_', varsIgnorePattern: '^_' },
      ],
      '@typescript-eslint/consistent-type-imports': [
        'error',
        { prefer: 'type-imports', fixStyle: 'inline-type-imports' },
      ],

      'boundaries/no-unknown-files': 'error',
      // Guards the guard: an import the resolver cannot place is an import no policy can
      // check, so it fails here instead of quietly passing everything.
      'boundaries/no-unknown-dependencies': 'error',

      'boundaries/dependencies': [
        'error',
        {
          default: 'disallow',
          /*
           * Off by default, and the rule is half-blind without it: with `checkAllOrigins`
           * false the rule only ever looks at imports that resolve inside the project, so
           * the two package policies at the bottom of this list would be silently inert.
           * Turning it on means every `node_modules` import is also evaluated, hence the
           * blanket allow immediately below.
           */
          checkAllOrigins: true,
          message:
            'This import crosses an architectural boundary. See DESIGN §9 and the element ' +
            'table at the top of eslint.config.mjs.',
          policies: [
            // Packages are allowed by default and then narrowed by the two policies at the
            // bottom of this list. Last matching policy wins.
            { allow: { to: { module: { origin: 'external' } } } },

            // The composition root wires the layers together, so it sees `core/`, `shared/`
            // and the routed feature components. It does not see a store or a data-access
            // service: providing those is each feature's own business.
            {
              from: { file: { categories: 'composition-root' } },
              allow: { to: { element: { types: { anyOf: ['core', 'shared', 'routed'] } } } },
            },
            {
              from: { file: { categories: 'composition-root' } },
              allow: { to: { file: { categories: 'composition-root' } } },
            },

            // `core/` is infrastructure: it may use the domain vocabulary, and nothing above it.
            {
              from: { element: { type: 'core' } },
              allow: { to: { element: { types: { anyOf: ['core', 'domain'] } } } },
            },

            // `domain/` is pure TypeScript. Nothing but itself — see the Angular ban below,
            // which is the half of this rule that actually bites.
            {
              from: { element: { type: 'domain' } },
              allow: { to: { element: { type: 'domain' } } },
            },

            // `shared/` is reusable presentation. It may speak the domain vocabulary but must
            // not depend on application infrastructure, or it stops being reusable.
            {
              from: { element: { type: 'shared' } },
              allow: { to: { element: { types: { anyOf: ['shared', 'domain'] } } } },
            },

            {
              from: { element: { type: 'data-access' } },
              allow: { to: { element: { types: { anyOf: ['core', 'domain'] } } } },
            },
            {
              from: { element: { type: 'data-access' } },
              allow: {
                to: { element: { type: 'data-access', capturedValues: sameContext } },
              },
            },

            {
              from: { element: { type: 'application' } },
              allow: { to: { element: { types: { anyOf: ['core', 'domain'] } } } },
            },
            {
              from: { element: { type: 'application' } },
              allow: {
                to: {
                  element: {
                    types: { anyOf: ['application', 'data-access'] },
                    capturedValues: sameContext,
                  },
                },
              },
            },

            // Presentational only: no store, no HTTP, no `core/`. A component that also knows
            // how to fetch cannot be restyled without a regression test for fetching.
            {
              from: { element: { type: 'ui' } },
              allow: { to: { element: { types: { anyOf: ['domain', 'shared'] } } } },
            },
            {
              from: { element: { type: 'ui' } },
              allow: { to: { element: { type: 'ui', capturedValues: sameContext } } },
            },

            // A routed component talks to its own store and its own presentational
            // components. Not to `data-access` — not even its own.
            {
              from: { element: { type: 'routed' } },
              allow: { to: { element: { types: { anyOf: ['core', 'domain', 'shared'] } } } },
            },
            {
              from: { element: { type: 'routed' } },
              allow: {
                to: {
                  element: {
                    types: { anyOf: ['routed', 'application', 'ui'] },
                    capturedValues: sameContext,
                  },
                },
              },
            },

            /*
             * Package restrictions. Last matching policy wins, so these sit after the
             * blanket external allow above.
             */
            {
              // DESIGN §9: `domain/` imports nothing from Angular. It is the layer that has
              // to stay runnable in a plain Node test, a worker, or a future SDK — and the
              // moment one type in it imports `@angular/core`, all of that is gone, and
              // nobody notices until the day it matters.
              from: { element: { type: 'domain' } },
              disallow: {
                to: {
                  module: {
                    origin: 'external',
                    source: ['@angular/**', '@ngrx/**', 'rxjs', 'rxjs/**', '@spartan-ng/**'],
                  },
                },
              },
              message: "'domain/' is framework-free TypeScript and may not import a framework.",
            },
            {
              // One typed data-access layer, per ADR-006. The prototype's two competing HTTP
              // paths — one of which always 404'd and swallowed the create silently — is what
              // happens without this.
              from: {
                element: { types: { anyOf: ['domain', 'shared', 'ui', 'routed'] } },
              },
              // Matched as package plus entry point: the resolver reports
              // `@angular/common/http` as source `@angular/common` with internal path
              // `http`, so a single `source` pattern would never fire.
              disallow: {
                to: {
                  module: { origin: 'external', source: '@angular/common', internalPath: 'http' },
                },
              },
              message:
                "HttpClient belongs in a feature's data-access layer or in core/http, not here.",
            },
          ],
        },
      ],
    },
  },

  {
    files: ['**/*.html'],
    extends: [...angular.configs.templateRecommended, ...angular.configs.templateAccessibility],
    rules: {},
  },
);
