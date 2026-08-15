import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { SchemaDocument } from '../../../domain/registry/schema';

/**
 * A schema document, as text.
 *
 * <b>Deliberately a `<pre>` and not an editor.</b> Monaco is M4.4 and belongs on the screens
 * that *write* a schema; a read-only view that pulled in a 3 MB editor to render forty lines
 * of JSON would put it on the critical path of the most-visited screen in the app for no
 * reading benefit. When Monaco lands it goes behind a lazy route, and this stays as the
 * viewer.
 *
 * <b>Nothing here goes through `innerHTML`.</b> The prototype highlighted JSON with a regex
 * and `dangerouslySetInnerHTML`, which is an XSS hole with a schema author on the other end
 * of it — schema text is user content, and a registry is precisely a place where one team's
 * content is rendered in another team's browser.
 *
 * <b>It is highlighted anyway, because the hole was in the mechanism and not in the idea.</b>
 * The document is scanned into tokens in TypeScript and each token is interpolated into its
 * own `<span>`, so every character still arrives as text and `<img src=x onerror=…>` in a
 * `title` renders as the four words it is. What that buys is the reason the design system has
 * carried five `--syntax-*` colours in both themes since the port: this is the artifact the
 * product exists to show, and a reader scanning for the one property that changed should not
 * have to find it in an undifferentiated wall of grey.
 */
@Component({
  selector: 'cd-schema-view',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block' },
  template: `
    <div class="border-border bg-card overflow-hidden rounded-xl border">
      <div class="border-border flex items-center justify-between border-b px-4 py-2">
        <span class="text-muted-foreground font-mono text-xs">{{ document().format }}</span>
        <span class="text-muted-foreground font-mono text-xs" [title]="document().schemaId">
          {{ document().schemaId.slice(0, 12) }}…
        </span>
      </div>

      <!--
        tabindex="0" because the block scrolls: a keyboard user has no other way to reach the
        overflow, and a scrollable region that cannot be focused is a WCAG 2.1.1 failure. The
        aria-label is what a screen reader announces on entering it.
      -->
      <pre
        tabindex="0"
        class="focus-visible:ring-ring max-h-[32rem] overflow-auto p-4 font-mono text-xs leading-relaxed focus-visible:ring-2 focus-visible:outline-none"
        [attr.aria-label]="'Schema document, ' + document().format"
      ><code>@for (token of tokens(); track $index) {<span [class]="token.class">{{ token.text }}</span>}</code></pre>
    </div>

    @if (document().references.length > 0) {
      <div class="border-border bg-card mt-4 rounded-xl border p-4">
        <h3 class="text-foreground mb-2 text-sm font-semibold">References</h3>
        <ul class="space-y-1">
          @for (reference of document().references; track reference.name) {
            <li class="text-muted-foreground font-mono text-xs">
              {{ reference.name }} → {{ reference.subject }} v{{ reference.version }}
            </li>
          }
        </ul>
      </div>
    }
  `,
})
export class SchemaView {
  readonly document = input.required<SchemaDocument>();

  /**
   * The document, indented if it is JSON and verbatim otherwise.
   *
   * Re-indenting is a display choice and only a safe one for JSON: a `.proto` is already
   * line-oriented and `JSON.parse` would throw on it. The `catch` covers the third case —
   * an Avro or JSON document the registry accepted that this parser does not like — where
   * showing the original text is strictly better than showing an error, because the reader
   * came here to see what was registered.
   */
  protected readonly pretty = computed(() => {
    const { format, text } = this.document();

    if (format === 'protobuf') {
      return text;
    }

    try {
      return JSON.stringify(JSON.parse(text), null, 2);
    } catch {
      return text;
    }
  });

  /**
   * The document, cut into coloured runs.
   *
   * One list rather than a highlighted path and a plain one, so the template has a single
   * shape and the `<pre>` cannot pick up a stray space between two branches — inside a
   * `<pre>`, a space is content. A document this component cannot scan comes back as a single
   * run wearing no class at all, which inherits the surrounding foreground: unhighlighted,
   * not dimmed.
   */
  protected readonly tokens = computed<readonly StyledToken[]>(() => {
    const text = this.pretty();

    // Protobuf is not JSON, and a document that failed to re-indent above failed to parse, so
    // there is nothing to scan. Both render as themselves.
    if (this.document().format === 'protobuf' || !parses(text)) {
      return [{ text, class: '' }];
    }

    return scan(text).map((token) => ({ text: token.text, class: SYNTAX_CLASSES[token.kind] }));
  });
}

/** What a run of characters is, named for what it means rather than what colour it takes. */
type SyntaxKind = 'property' | 'string' | 'number' | 'boolean' | 'null' | 'punctuation';

interface SyntaxToken {
  readonly text: string;
  readonly kind: SyntaxKind;
}

interface StyledToken {
  readonly text: string;
  readonly class: string;
}

/**
 * The five tokens `styles/tokens.css` has carried since the port, plus the one it does not.
 *
 * Punctuation takes `--muted-foreground` rather than a colour of its own. Braces, brackets and
 * commas are structure a reader already knows is there; dimming them is what lets the property
 * names and values they separate come forward, and it is the one thing that makes a long
 * schema scannable rather than merely colourful.
 */
const SYNTAX_CLASSES: Record<SyntaxKind, string> = {
  property: 'text-syntax-property',
  string: 'text-syntax-string',
  number: 'text-syntax-number',
  boolean: 'text-syntax-boolean',
  null: 'text-syntax-keyword',
  punctuation: 'text-muted-foreground',
};

/** Whether a string is JSON this component can scan. */
function parses(text: string): boolean {
  try {
    JSON.parse(text);
    return true;
  } catch {
    return false;
  }
}

/**
 * Cuts JSON into runs.
 *
 * <b>A scanner and not a regular expression</b>, which is the difference between this and the
 * prototype's highlighter that had to be deleted. A regex over the whole document cannot tell
 * a quote that opens a string from one that a schema author escaped inside it, so
 * `{"pattern":"\\\"|\\\\"}` — a legal and not especially exotic JSON Schema — would colour the
 * rest of the file as one long string. Reading left to right, escapes are simply consumed.
 *
 * It runs over text `JSON.parse` has already accepted, so it does not validate and has no
 * error case: anything it does not recognise falls through to punctuation, which renders.
 */
function scan(text: string): readonly SyntaxToken[] {
  const tokens: SyntaxToken[] = [];
  let plain = '';
  let index = 0;

  const flush = () => {
    if (plain !== '') {
      tokens.push({ text: plain, kind: 'punctuation' });
      plain = '';
    }
  };

  while (index < text.length) {
    // `charAt` rather than `text[index]`, which `noUncheckedIndexedAccess` types as possibly
    // undefined — and an undefined appended to `plain` is the string "undefined" in the middle
    // of somebody's schema.
    const char = text.charAt(index);

    if (char === '"') {
      const end = endOfString(text, index);
      flush();
      // A string followed by a colon is a key. That lookahead is the whole difference between
      // "property" and "string", and it is why the two can share a scanner.
      tokens.push({ text: text.slice(index, end), kind: isKey(text, end) ? 'property' : 'string' });
      index = end;
      continue;
    }

    const literal = LITERALS.find((candidate) => text.startsWith(candidate.text, index));

    if (literal) {
      flush();
      tokens.push(literal);
      index += literal.text.length;
      continue;
    }

    NUMBER.lastIndex = index;
    const number = NUMBER.exec(text)?.[0];

    if (number !== undefined && number !== '') {
      flush();
      tokens.push({ text: number, kind: 'number' });
      index += number.length;
      continue;
    }

    plain += char;
    index++;
  }

  flush();

  return tokens;
}

/** `true`, `false` and `null`, which are the only bare words JSON has. */
const LITERALS: readonly SyntaxToken[] = [
  { text: 'true', kind: 'boolean' },
  { text: 'false', kind: 'boolean' },
  { text: 'null', kind: 'null' },
];

/**
 * Sticky, so it matches at exactly the scanner's position without slicing the document.
 *
 * `lastIndex` is assigned immediately before every `exec`, which is what makes a shared
 * mutable regex safe here — nothing reads it between the two statements.
 */
const NUMBER = /-?\d+(\.\d+)?([eE][+-]?\d+)?/y;

/** Likewise sticky: is the next thing after this string, past any spaces, a colon? */
const KEY_COLON = /\s*:/y;

/** The index just past the closing quote of the string starting at `start`. */
function endOfString(text: string, start: number): number {
  let index = start + 1;

  while (index < text.length) {
    const char = text.charAt(index);

    // A backslash consumes whatever follows it, including another backslash and including the
    // quote that would otherwise have ended the string.
    if (char === '\\') {
      index += 2;
      continue;
    }

    if (char === '"') {
      return index + 1;
    }

    index++;
  }

  // Unterminated. Unreachable on text `JSON.parse` accepted, and the honest answer if it ever
  // is reached: the rest of the document is that string.
  return text.length;
}

/** Whether the next non-space character after `index` is the colon that makes a key a key. */
function isKey(text: string, index: number): boolean {
  KEY_COLON.lastIndex = index;
  return KEY_COLON.test(text);
}
