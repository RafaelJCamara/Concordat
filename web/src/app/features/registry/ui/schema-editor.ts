import {
  ChangeDetectionStrategy,
  ChangeDetectorRef,
  Component,
  DestroyRef,
  InjectionToken,
  afterNextRender,
  effect,
  forwardRef,
  inject,
  input,
  signal,
  viewChild,
  type ElementRef,
} from '@angular/core';
import { NG_VALUE_ACCESSOR, type ControlValueAccessor } from '@angular/forms';
import type * as Monaco from 'monaco-editor/editor/editor.api';
import type { EditorFinding } from '../../../domain/registry/editor-finding';
import type { SchemaFormat } from '../../../domain/registry/wire-tokens';
import { SCHEMA_JSON, loadMonaco, type MonacoApi } from './monaco-loader';
import { applyEditorTheme } from './monaco-theme';

/**
 * How the rich editor is fetched. A token so a test can say "it never arrives" or hand over a
 * fake; the real one is the dynamic import, which is the only thing keeping Monaco out of the
 * initial bundle.
 */
export const MONACO_LOADER = new InjectionToken<() => Promise<MonacoApi>>('MONACO_LOADER', {
  providedIn: 'root',
  factory: () => loadMonaco,
});

/** The marker owner, so this editor clears only the markers it set. */
const MARKER_OWNER = 'concordat';

/**
 * Where the schema text is edited, as a form control.
 *
 * <b>A control, not a textarea with a sibling.</b> The page binds it with `formControlName` and
 * never learns what is behind it, which is the point: it is a plain `<textarea>` first, and
 * becomes Monaco when Monaco has arrived (M4.4). The form, its validators and every test that
 * types into it must not care which.
 *
 * <b>The textarea is the working editor, not a placeholder for the real one.</b> Monaco is a
 * separate chunk, fetched after this screen is already usable, so there is a window in which
 * the textarea is all there is — and a registry on a network that blocks the chunk, or a cache
 * that served half of a deploy, leaves it that way for good. In both cases the screen is a
 * smaller editor and not a broken one. When Monaco does arrive it takes over what is there: the
 * text, the caret, and focus if the textarea had it.
 *
 * <b>Monaco says nothing of its own about the document.</b> It colours it, folds it and
 * underlines what it is told to; every judgement about whether the text is acceptable comes in
 * as `findings`, from the same domain code that mirrors the registry. An editor with opinions of
 * its own would be a second source of "valid JSON" that can disagree with the registry's.
 *
 * Presentational: no store, no HTTP. Where to look is the caller's to ask for (`reveal`).
 *
 * <b>Keyboard.</b> A code editor treats Tab as a character, which traps a keyboard user inside
 * it; Monaco's `Ctrl+M` makes Tab move focus instead, and the hint below the editor says so,
 * because nothing else would.
 */
@Component({
  selector: 'cd-schema-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [
    { provide: NG_VALUE_ACCESSOR, useExisting: forwardRef(() => SchemaEditor), multi: true },
  ],
  host: { class: 'block' },
  template: `
    @if (rich()) {
      <p [id]="inputId() + '-hint'" class="sr-only">
        This is a code editor. Tab inserts a tab; press Control and M to make Tab move focus
        instead.
      </p>
    } @else {
      <textarea
        #field
        [id]="inputId()"
        [value]="value()"
        [disabled]="disabled()"
        [attr.aria-invalid]="invalid() ? 'true' : null"
        [attr.aria-describedby]="describedBy()"
        (input)="edit($event)"
        (blur)="touched()"
        rows="18"
        spellcheck="false"
        autocapitalize="off"
        autocomplete="off"
        placeholder='{ "type": "object", "properties": { } }'
        class="border-input bg-background focus-visible:ring-ring block w-full rounded-md border p-3 font-mono text-xs focus-visible:ring-1 focus-visible:outline-none"
      ></textarea>
    }

    <!--
      Always in the document, hidden until Monaco has arrived: the editor needs an element to be
      created in, and creating one inside a conditional would have to wait a render for it.
    -->
    <div
      #host
      [hidden]="!rich()"
      data-testid="monaco-host"
      class="border-input focus-within:ring-ring h-80 min-h-40 resize-y overflow-hidden rounded-md border focus-within:ring-1"
    ></div>

    @if (rich()) {
      <p class="text-muted-foreground mt-1 text-xs" aria-hidden="true">
        Tab inserts a tab in this editor. <kbd class="font-mono">Ctrl</kbd>+<kbd class="font-mono"
          >M</kbd
        >
        makes it move focus instead.
      </p>
    } @else if (notice(); as message) {
      <p class="text-muted-foreground mt-1 text-xs" role="status">{{ message }}</p>
    }
  `,
})
export class SchemaEditor implements ControlValueAccessor {
  /** The id of the focusable element, so the page's `<label for>` reaches it. */
  readonly inputId = input.required<string>();

  /** Whether the text has a problem, for assistive technology. */
  readonly invalid = input(false);

  /** The id of the element that explains what is wrong with the text. */
  readonly describedBy = input<string | null>(null);

  /** What the text is, which decides how it is coloured. Null until the subject has loaded. */
  readonly format = input<SchemaFormat | null>(null);

  /** What is wrong with, or worth knowing about, the text, to be drawn into it. */
  readonly findings = input<readonly EditorFinding[]>([]);

  /** Whether the page is in its dark theme. */
  readonly dark = input(false);

  /** What a screen reader calls the editor, once it is Monaco and has no `<label for>`. */
  readonly ariaLabel = input('Schema document');

  protected readonly value = signal('');
  protected readonly disabled = signal(false);

  /** Whether Monaco has taken over. */
  protected readonly rich = signal(false);

  /** Why it has not, if it has not and somebody should be told. */
  protected readonly notice = signal<string | null>(null);

  private readonly field = viewChild<ElementRef<HTMLTextAreaElement>>('field');
  private readonly host = viewChild.required<ElementRef<HTMLElement>>('host');

  private readonly loader = inject(MONACO_LOADER);
  private readonly changes = inject(ChangeDetectorRef);

  private monaco: MonacoApi | null = null;
  private editor: Monaco.editor.IStandaloneCodeEditor | null = null;

  /** Bumped once Monaco exists, which is what makes the effects below run again. */
  private readonly ready = signal(0);

  /** True while the form is writing into the editor, so that is not reported back as an edit. */
  private applying = false;
  private destroyed = false;

  private onChange: (value: string) => void = () => {};
  private onTouched: () => void = () => {};

  constructor() {
    afterNextRender(() => void this.upgrade());

    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      this.editor?.getModel()?.dispose();
      this.editor?.dispose();
      this.editor = null;
    });

    // Everything below follows an input and does nothing until there is an editor. Reading
    // `ready` is what makes each re-run once Monaco has been created: without it they would run
    // once at construction, find nothing to act on, and never again.
    effect(() => {
      this.ready();
      this.language(this.format());
    });

    effect(() => {
      this.ready();
      this.mark(this.findings());
    });

    effect(() => {
      this.ready();
      this.restyle(this.dark());
    });

    effect(() => {
      this.ready();
      this.editor?.updateOptions({ readOnly: this.disabled() });
    });

    effect(() => {
      this.ready();
      this.accessibility(this.describedBy(), this.invalid());
    });
  }

  /**
   * Moves the caret to an offset in the text and focuses the editor.
   *
   * Offset, not line and column: the editor knows what its own coordinates are, and the
   * domain's findings carry both.
   */
  reveal(offset: number): void {
    const editor = this.editor;
    const model = editor?.getModel();

    if (editor != null && model != null) {
      const position = model.getPositionAt(offset);

      editor.setPosition(position);
      editor.revealPositionInCenterIfOutsideViewport(position);
      editor.focus();
      return;
    }

    const element = this.field()?.nativeElement;

    if (element !== undefined) {
      element.focus();
      element.setSelectionRange(offset, offset);
    }
  }

  writeValue(value: string | null): void {
    const text = value ?? '';

    this.value.set(text);

    const editor = this.editor;
    const model = editor?.getModel();

    if (editor == null || model == null || model.getValue() === text) {
      return;
    }

    // An edit and not `setValue`, so the form's own writes — Format, loading an earlier
    // version — are one step on the editor's undo stack. A person who loads a version over
    // something they wrote and regrets it presses Ctrl+Z; the two-click guard is for the one
    // who does not think to.
    this.applying = true;

    try {
      editor.pushUndoStop();
      editor.executeEdits('form', [
        { range: model.getFullModelRange(), text, forceMoveMarkers: true },
      ]);
      editor.pushUndoStop();
      editor.setPosition({ lineNumber: 1, column: 1 });
      editor.revealLine(1);
    } finally {
      this.applying = false;
    }
  }

  registerOnChange(fn: (value: string) => void): void {
    this.onChange = fn;
  }

  registerOnTouched(fn: () => void): void {
    this.onTouched = fn;
  }

  setDisabledState(disabled: boolean): void {
    this.disabled.set(disabled);
  }

  protected edit(event: Event): void {
    const value = (event.target as HTMLTextAreaElement).value;

    this.value.set(value);
    this.onChange(value);
  }

  protected touched(): void {
    this.onTouched();
  }

  /** Fetches Monaco and, if it comes, takes over from the textarea. */
  private async upgrade(): Promise<void> {
    try {
      const monaco = await this.loader();

      if (!this.destroyed) {
        this.mount(monaco);
      }
    } catch {
      // A chunk that cannot be fetched, a worker that cannot start, a browser too old for the
      // build. All of them leave a working textarea, and none of them is worth more than a
      // sentence: the person is here to register a schema, not to debug a bundler.
      this.editor?.dispose();
      this.editor = null;
      this.rich.set(false);
      this.notice.set(
        'The code editor could not be loaded, so this is the plain editor. Everything works the same.',
      );
    }
  }

  private mount(monaco: MonacoApi): void {
    const field = this.field()?.nativeElement;
    const hadFocus = field !== undefined && field.ownerDocument.activeElement === field;
    const selection = field === undefined ? null : [field.selectionStart, field.selectionEnd];

    // Reveal the host before Monaco measures it, and in this turn, not on the next render:
    // an editor created inside a `hidden` element lays itself out at zero by zero.
    this.rich.set(true);
    this.changes.detectChanges();

    const editor = monaco.editor.create(this.host().nativeElement, {
      model: monaco.editor.createModel(this.value(), this.languageId(this.format())),
      ariaLabel: this.ariaLabel(),
      automaticLayout: true,
      readOnly: this.disabled(),
      // A one-line compact schema is common, and scrolling sideways to read it is the worse
      // failure.
      wordWrap: 'on',
      minimap: { enabled: false },
      scrollBeyondLastLine: false,
      lineNumbers: 'on',
      folding: true,
      tabSize: 2,
      insertSpaces: true,
      fontSize: 12,
      fontFamily: "'JetBrains Mono', ui-monospace, SFMono-Regular, Menlo, Consolas, monospace",
      padding: { top: 8, bottom: 8 },
      renderLineHighlight: 'line',
      overviewRulerLanes: 0,
      hideCursorInOverviewRuler: true,
      occurrencesHighlight: 'off',
      // This is a document, not a program: nothing here has completions to offer, and a popup
      // that appears while somebody types a property name is only in the way.
      quickSuggestions: false,
      suggestOnTriggerCharacters: false,
      parameterHints: { enabled: false },
      wordBasedSuggestions: 'off',
      acceptSuggestionOnEnter: 'off',
      accessibilitySupport: 'auto',
      stickyScroll: { enabled: false },
      scrollbar: { alwaysConsumeMouseWheel: false },
      // No auto-closing. Text that arrives other than by a paste — an input method, dictation,
      // a voice-control tool — is delivered the way typing is, and each opening bracket or quote
      // then grows a closing one the person never asked for: this editor's whole job is that the
      // text is exactly what was meant, because it is what gets hashed.
      autoClosingBrackets: 'never',
      autoClosingQuotes: 'never',
      autoSurround: 'never',
      autoClosingOvertype: 'never',
    });

    // LF, always. The registry measures bytes and the form holds what the textarea would, which
    // is LF-only; a model left to guess from the first line it saw would sometimes answer CRLF
    // and the two would count differently.
    editor.getModel()?.setEOL(monaco.editor.EndOfLineSequence.LF);

    editor.onDidChangeModelContent(() => {
      if (this.applying) {
        return;
      }

      const text = editor.getModel()?.getValue() ?? '';

      this.value.set(text);
      this.onChange(text);
    });

    editor.onDidBlurEditorText(() => this.onTouched());

    this.monaco = monaco;
    this.editor = editor;

    const model = editor.getModel();

    if (selection !== null && model !== null) {
      const start = model.getPositionAt(selection[0] ?? 0);
      const end = model.getPositionAt(selection[1] ?? 0);

      editor.setSelection({
        startLineNumber: start.lineNumber,
        startColumn: start.column,
        endLineNumber: end.lineNumber,
        endColumn: end.column,
      });
    }

    if (hadFocus) {
      editor.focus();
    }

    this.ready.update((n) => n + 1);
  }

  private languageId(format: SchemaFormat | null): string {
    return format === 'protobuf' ? 'proto' : SCHEMA_JSON;
  }

  private language(format: SchemaFormat | null): void {
    const model = this.editor?.getModel();

    if (this.monaco !== null && model != null) {
      this.monaco.editor.setModelLanguage(model, this.languageId(format));
    }
  }

  /** Draws findings into the text: a refusal as an error, advice as a warning. */
  private mark(findings: readonly EditorFinding[]): void {
    const monaco = this.monaco;
    const model = this.editor?.getModel();

    if (monaco === null || model == null) {
      return;
    }

    const markers: Monaco.editor.IMarkerData[] = [];

    for (const finding of findings) {
      // A finding about the document as a whole has nowhere to be drawn; the list under the
      // editor carries it.
      if (finding.position === null) {
        continue;
      }

      const start = model.getPositionAt(finding.position.offset);
      const end = model.getPositionAt(finding.position.offset + Math.max(finding.length, 1));

      markers.push({
        severity:
          finding.severity === 'refusal'
            ? monaco.MarkerSeverity.Error
            : monaco.MarkerSeverity.Warning,
        message: finding.message,
        code: finding.code,
        source: finding.severity === 'refusal' ? 'registry' : 'ajv',
        startLineNumber: start.lineNumber,
        startColumn: start.column,
        endLineNumber: end.lineNumber,
        endColumn: end.column,
      });
    }

    monaco.editor.setModelMarkers(model, MARKER_OWNER, markers);
  }

  /**
   * Applies the page's theme to the editor.
   *
   * Twice, the second a frame later: the theme store flips the class on the document and this
   * effect can run before the new custom properties are in force, and reading a token a frame
   * early paints the editor in the theme that was just left.
   */
  private restyle(dark: boolean): void {
    const monaco = this.monaco;

    if (monaco === null) {
      return;
    }

    applyEditorTheme(monaco, dark);
    requestAnimationFrame(() => {
      if (!this.destroyed) {
        applyEditorTheme(monaco, dark);
      }
    });
  }

  /**
   * Gives Monaco's own input the description and validity the textarea would have had.
   *
   * Monaco builds its input itself and has no option for either, so they are set on the element
   * it made. Without this a screen reader reaches the rich editor and never hears that
   * the text has problems.
   */
  private accessibility(describedBy: string | null, invalid: boolean): void {
    // Chromium gives Monaco an `EditContext` element, which is a `div`; browsers without one get
    // a `textarea`. Whichever it made is the thing a screen reader lands on.
    const input = this.host().nativeElement.querySelector(
      '.native-edit-context, textarea.inputarea',
    );

    if (input === null) {
      return;
    }

    const hint = `${this.inputId()}-hint`;
    const described = [describedBy, hint].filter((id) => id !== null).join(' ');

    input.setAttribute('aria-describedby', described);

    if (invalid) {
      input.setAttribute('aria-invalid', 'true');
    } else {
      input.removeAttribute('aria-invalid');
    }
  }
}
