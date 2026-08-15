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
 * content is rendered in another team's browser. Angular's interpolation escapes, and the
 * absence of highlighting is worth strictly more than the alternative was.
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
      ><code>{{ pretty() }}</code></pre>
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
}
