import type * as Monaco from 'monaco-editor/editor/editor.api';
import { MONACO_STYLESHEET } from './monaco-assets.generated';

/** The slice of Monaco's API this app touches. */
export type MonacoApi = typeof Monaco;

let loading: Promise<MonacoApi> | null = null;

/**
 * Fetches Monaco, once, the first time anything asks.
 *
 * <b>The dynamic import is the whole point of this file.</b> Monaco is several megabytes, and
 * the build turns `import('./monaco-bundle')` into a chunk of its own that is requested only
 * when this runs — so a person who opens the subject list, the dashboard or the approval queue
 * never downloads it, and neither does one who opens this screen and has no network path to the
 * chunk, who gets the textarea instead (see `SchemaEditor`).
 *
 * <b>Self-hosted, with no third-party fetch.</b> The editor, its worker and its icon font are
 * all files of this build, served from this origin; the usual recipe points Monaco at a CDN
 * loader, which would put a request to a third party on a page inside somebody's firewall and
 * is exactly what ADR-026 rejects for fonts.
 *
 * A rejected load is forgotten, so the next editor to be created tries again rather than being
 * handed the same failure for the rest of the session.
 */
export function loadMonaco(): Promise<MonacoApi> {
  loading ??= load().catch((error: unknown) => {
    loading = null;
    throw error;
  });

  return loading;
}

async function load(): Promise<MonacoApi> {
  // Before the bundle is evaluated: Monaco asks for this when it first needs a worker, which is
  // the first time an editor is created, and reads nothing else of it afterwards.
  globalThis.MonacoEnvironment = {
    // A module worker the build bundles from this project, so it is a file on this origin and
    // is governed by this origin's policy. `label` is ignored on purpose: with no language
    // service registered there is one kind of worker to hand out.
    getWorker: () =>
      new Worker(new URL('./monaco-editor.worker', import.meta.url), {
        type: 'module',
        name: 'monaco-editor-worker',
      }),
  };

  // Together, so the stylesheet is not a second round trip after the script. Both have to
  // arrive: an editor without its stylesheet is a pile of unpositioned boxes, which is worse
  // than the textarea it would replace, so a stylesheet that fails is a load that failed.
  const [{ monaco }] = await Promise.all([import('./monaco-bundle'), linkStylesheet()]);

  registerSchemaJson(monaco);

  return monaco;
}

/**
 * Links Monaco's stylesheet, once.
 *
 * A file the build does not know about, vendored by `tools/vendor-monaco.mjs`, because the
 * build cannot be made to link a stylesheet a lazy chunk imports — see that file for the
 * production-only failure that taught it. Same-origin, so the page's `style-src 'self'` allows
 * it, and its icon font is a file beside it for `font-src 'self'`.
 */
function linkStylesheet(): Promise<void> {
  const existing = document.querySelector<HTMLLinkElement>('link[data-monaco]');

  if (existing !== null) {
    return Promise.resolve();
  }

  return new Promise((resolve, reject) => {
    const link = document.createElement('link');

    link.rel = 'stylesheet';
    link.href = MONACO_STYLESHEET;
    link.dataset['monaco'] = '';
    link.onload = () => resolve();
    link.onerror = () => {
      link.remove();
      reject(new Error(`The editor's stylesheet could not be loaded from ${MONACO_STYLESHEET}.`));
    };

    document.head.append(link);
  });
}

/** The id the editor uses for JSON text: a JSON Schema, or an Avro schema, which is JSON too. */
export const SCHEMA_JSON = 'schema-json';

/**
 * A small JSON grammar of this app's own, instead of Monaco's JSON language.
 *
 * Monaco's JSON support is a language *service*: a worker, diagnostics, completion, formatting.
 * Each of those is a second opinion about the document, and the one thing this editor must not
 * have is an opinion that differs from the registry's — Monaco's JSON mode accepts comments, for
 * one, which the registry's reader refuses. What the editor needs from JSON is colour, and colour
 * is a tokenizer. Every judgement about the document comes from `domain/`, which is where the
 * registry's rules are, and reaches the editor as markers.
 *
 * The token names are the ones the stock themes already colour (`string.key.json`, and so on),
 * so both themes work without a rule of ours.
 */
function registerSchemaJson(monaco: MonacoApi): void {
  if (monaco.languages.getLanguages().some((language) => language.id === SCHEMA_JSON)) {
    return;
  }

  monaco.languages.register({ id: SCHEMA_JSON });

  monaco.languages.setLanguageConfiguration(SCHEMA_JSON, {
    brackets: [
      ['{', '}'],
      ['[', ']'],
    ],
    autoClosingPairs: [
      { open: '{', close: '}' },
      { open: '[', close: ']' },
      { open: '"', close: '"', notIn: ['string'] },
    ],
    surroundingPairs: [
      { open: '{', close: '}' },
      { open: '[', close: ']' },
      { open: '"', close: '"' },
    ],
  });

  monaco.languages.setMonarchTokensProvider(SCHEMA_JSON, {
    tokenizer: {
      root: [
        // A key is a string followed by a colon, which is all a line-at-a-time tokenizer can see
        // and all that is needed to colour it differently from a value.
        [/"(?:[^"\\]|\\.)*"(?=\s*:)/, 'string.key.json'],
        [/"(?:[^"\\]|\\.)*"/, 'string.value.json'],
        [/-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?/, 'number'],
        [/\b(?:true|false|null)\b/, 'keyword.json'],
        [/[{}]/, 'delimiter.bracket'],
        [/[[\]]/, 'delimiter.array'],
        [/[,:]/, 'delimiter'],
        [/\s+/, 'white'],
        // Anything else is not JSON. Colouring it as an error is the one place the tokenizer
        // may say so, because it is only saying what the grammar above already established.
        [/./, 'invalid'],
      ],
    },
  });
}
