import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormBuilder, FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import { ThemeStore } from '../../../core/config/theme-store';
import type { EditorFinding } from '../../../domain/registry/editor-finding';
import {
  formatJsonDocument,
  locatePointer,
  parseJsonDocument,
  positionAt,
} from '../../../domain/registry/json-document';
import { checkSchemaText } from '../../../domain/registry/schema-document';
import {
  compareSemanticVersions,
  formatSemanticVersion,
  parseSemanticVersion,
} from '../../../domain/registry/semantic-version';
import { Icon } from '../../../shared/ui/icon/icon';
import { StatusBadge } from '../../../shared/ui/status-badge/status-badge';
import { NewVersionStore } from '../application/new-version-store';
import { SchemaCheckStore } from '../application/schema-check-store';
import { CheckResult } from '../ui/check-result';
import { DivergenceList } from '../ui/divergence-list';
import { EditorFindings } from '../ui/editor-findings';
import { LabelSuggestions } from '../ui/label-suggestions';
import { RegisterConfirmation } from '../ui/register-confirmation';
import { SchemaEditor } from '../ui/schema-editor';
import { VersionStarter } from '../ui/version-starter';

/**
 * Register a version against an existing subject.
 *
 * <b>The app's first write route</b>, which makes it the first one `scopeGuard` is attached
 * to — see `app.routes.ts`. Until this existed the guard was built, unit-tested and
 * referenced by nothing, and M4.5's "direct URL to a write route redirects" test had no
 * write route to paste.
 *
 * <b>A breaking change is not an error on this screen.</b> The registry accepts it, holds it
 * at the approval gate and leaves `latest` unmoved (ADR-017). Reporting that as a failure
 * would have people re-submitting a change that already landed, so the outcome panel says
 * plainly which of the three things happened: registered, held, or already the tip.
 */
@Component({
  selector: 'cd-new-version-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [NewVersionStore, SchemaCheckStore],
  imports: [
    ReactiveFormsModule,
    RouterLink,
    HlmAlertImports,
    HlmButton,
    Icon,
    StatusBadge,
    DivergenceList,
    CheckResult,
    EditorFindings,
    LabelSuggestions,
    RegisterConfirmation,
    SchemaEditor,
    VersionStarter,
  ],
  template: `
    <section class="mx-auto w-full max-w-4xl space-y-6 p-6 lg:p-8">
      <a
        [routerLink]="['/subjects', name()]"
        class="text-muted-foreground hover:text-foreground inline-flex items-center gap-1 text-sm transition-colors"
      >
        <cd-icon name="chevron-left" size="0.875rem" />
        <span class="font-mono">{{ name() }}</span>
      </a>

      <header>
        <h1 class="text-foreground text-2xl font-bold tracking-tight">Register a version</h1>
        <p class="text-muted-foreground mt-1">
          Against <span class="font-mono">{{ name() }}</span> in
          <span class="font-mono">{{ environments.name() }}</span>
          @if (store.nextOrdinal(); as next) {
            · next ordinal <span class="font-mono">v{{ next }}</span>
          }
        </p>
      </header>

      @if (store.outcome(); as outcome) {
        <!--
          The answer, and it replaces the form rather than sitting above it. Leaving an
          editable form under a "registered" banner invites a second submit that either
          allocates another ordinal or silently no-ops, and neither is what the button
          appears to offer.
        -->
        <div class="border-border bg-card space-y-4 rounded-xl border p-6">
          <div class="flex flex-wrap items-center gap-3">
            @if (!outcome.created) {
              <cd-status-badge tone="info">Unchanged</cd-status-badge>
            } @else if (store.held()) {
              <cd-status-badge tone="warning" [pulse]="true">Awaiting approval</cd-status-badge>
            } @else {
              <cd-status-badge tone="success">Registered</cd-status-badge>
            }
            <span class="font-mono text-sm">v{{ outcome.ordinal }}</span>
          </div>

          <p class="text-muted-foreground text-sm">
            @if (!outcome.created) {
              This document is already the current version, so no ordinal was allocated. The
              registry deduplicates by content, and re-registering the tip is deliberately
              idempotent.
            } @else if (store.held()) {
              The change is breaking, so it registered as v{{ outcome.ordinal }} and the
              <code class="font-mono">latest</code> pointer has not moved. Consumers keep resolving
              the previous version until somebody approves this one.
            } @else {
              Registered as v{{ outcome.ordinal }} and <code class="font-mono">latest</code> now
              points at it.
            }
          </p>

          @if (outcome.divergences.length > 0) {
            <cd-divergence-list [divergences]="outcome.divergences" />
          }

          @if (outcome.portability.length > 0) {
            <div>
              <h2 class="text-foreground mb-2 text-sm font-semibold">Portability warnings</h2>
              <p class="text-muted-foreground mb-2 text-xs">
                Accepted, and worth knowing: these are places the schema may not behave identically
                in every SDK.
              </p>
              <ul class="space-y-2">
                @for (finding of outcome.portability; track finding.path + finding.kind) {
                  <li class="border-border rounded-lg border p-3 text-sm">
                    <span class="font-mono text-xs">{{ finding.path }}</span>
                    <p class="text-muted-foreground mt-1">{{ finding.message }}</p>
                  </li>
                }
              </ul>
            </div>
          }

          <div class="flex flex-wrap gap-2">
            <a hlmBtn size="sm" [routerLink]="['/subjects', name(), 'versions', outcome.ordinal]">
              View v{{ outcome.ordinal }}
            </a>
            <a hlmBtn variant="outline" size="sm" [routerLink]="['/subjects', name()]">
              Back to the subject
            </a>
            <button hlmBtn variant="ghost" size="sm" type="button" (click)="another()">
              Register another
            </button>
          </div>
        </div>
      } @else {
        @if (store.error(); as error) {
          <div hlmAlert variant="destructive">
            <h2 hlmAlertTitle>The registry refused this schema</h2>
            <p hlmAlertDescription>{{ error.detail }}</p>
          </div>
        }

        <form [formGroup]="form" (ngSubmit)="ask()" class="space-y-5">
          @if (store.subject() !== null) {
            <cd-version-starter
              [versions]="store.startingPoints()"
              [defaultOrdinal]="store.defaultStartingOrdinal()"
              [editorHasContent]="hasContent()"
              [loading]="store.cloning()"
              [failure]="store.cloneError()?.detail ?? null"
              (startFrom)="store.cloneFrom($event)"
            />
          }

          <div class="space-y-1.5">
            <div class="flex flex-wrap items-end justify-between gap-2">
              <label class="text-sm font-medium" for="schema">Schema document</label>

              <div class="flex gap-2">
                @if (canFormat()) {
                  <button hlmBtn type="button" variant="outline" size="sm" (click)="format()">
                    Format
                  </button>
                }
                @if (canValidate()) {
                  <button hlmBtn type="button" variant="outline" size="sm" (click)="validate()">
                    Validate
                  </button>
                }
              </div>
            </div>

            <cd-schema-editor
              inputId="schema"
              formControlName="schema"
              [invalid]="visibleFindings().length > 0"
              [format]="store.format()"
              [findings]="visibleFindings()"
              [dark]="dark()"
              describedBy="schema-help"
            />

            <div id="schema-help" class="space-y-1.5" aria-live="polite">
              <cd-editor-findings [findings]="visibleFindings()" (reveal)="reveal($event)" />

              @if (formatMessage(); as message) {
                <p class="text-muted-foreground text-xs" role="status">{{ message }}</p>
              }

              @if (visibleFindings().length === 0) {
                <p class="text-muted-foreground text-xs">
                  The document as you want it stored. The registry canonicalises it to compute the
                  id, but keeps what you sent.
                </p>
              }
            </div>

            @if (canValidate()) {
              <details class="border-border rounded-md border px-3 py-2 text-sm">
                <summary class="cursor-pointer font-medium">
                  Check a sample message
                  <span class="text-muted-foreground font-normal">· optional</span>
                </summary>
                <div class="mt-2 space-y-1.5">
                  <label class="text-muted-foreground text-xs" for="sample">
                    A message you expect to be valid under this schema. Press Validate to check it.
                  </label>
                  <textarea
                    id="sample"
                    [formControl]="sample"
                    rows="6"
                    spellcheck="false"
                    placeholder='{ "id": "abc" }'
                    class="border-input bg-background focus-visible:ring-ring block w-full rounded-md border p-3 font-mono text-xs focus-visible:ring-1 focus-visible:outline-none"
                  ></textarea>
                </div>
              </details>
            }

            @if (checkVisible()) {
              <cd-check-result
                [checking]="checks.checking()"
                [failure]="checks.failure()"
                [checked]="checkIsCurrent()"
                [stale]="checkIsStale()"
                [skipped]="checks.result()?.skipped ?? null"
                [adviceCount]="adviceFindings().length"
                [payload]="checks.result()?.payload ?? null"
              />
            }
          </div>

          <div class="grid grid-cols-1 gap-4 sm:grid-cols-2">
            <div class="space-y-1.5">
              <label class="text-sm font-medium" for="semanticVersion">
                Semantic version
                <span class="text-muted-foreground font-normal">· optional</span>
              </label>
              <input
                id="semanticVersion"
                formControlName="semanticVersion"
                placeholder="1.2.0"
                class="border-input bg-background focus-visible:ring-ring flex h-9 w-full rounded-md border px-3 py-1 font-mono text-sm focus-visible:ring-1 focus-visible:outline-none"
              />
              @if (store.subject() !== null) {
                <cd-label-suggestions
                  [from]="store.liveLabel()"
                  [suggestions]="store.labelSuggestions()"
                  (pick)="pickLabel($event)"
                />
              }
              @if (labelFinding(); as finding) {
                <p
                  class="text-xs"
                  [class]="finding.severity === 'refusal' ? 'text-destructive' : 'text-warning'"
                >
                  {{ finding.message }}
                </p>
              }
              <p class="text-muted-foreground text-xs">
                <!--
                  "Verified", not "recorded". The registry checks the label against its own
                  verdict and refuses a patch bump on a breaking change, which is the whole
                  reason the field is worth filling in.
                -->
                Checked against the compatibility verdict, so a patch bump on a breaking change is
                refused rather than believed.
              </p>
            </div>

            <div class="space-y-1.5">
              <label class="text-sm font-medium" for="changelog">
                Changelog <span class="text-muted-foreground font-normal">· optional</span>
              </label>
              <input
                id="changelog"
                formControlName="changelog"
                placeholder="Added the discount field."
                class="border-input bg-background focus-visible:ring-ring flex h-9 w-full rounded-md border px-3 py-1 text-sm focus-visible:ring-1 focus-visible:outline-none"
              />
              <p class="text-muted-foreground text-xs">
                Shown on the version, and in the approval queue if this one is held.
              </p>
            </div>
          </div>

          @if (confirming()) {
            <cd-register-confirmation
              [subject]="name()"
              [environment]="environments.name()"
              [ordinal]="store.nextOrdinal()"
              [label]="sentLabel()"
              [busy]="store.submitting()"
              (confirmed)="register()"
              (declined)="confirmation.set(null)"
            />
          } @else {
            <div class="flex items-center gap-2">
              <button hlmBtn type="submit" [disabled]="!canSubmit()">
                {{ store.submitting() ? 'Registering…' : 'Register' }}
              </button>
              <a hlmBtn variant="ghost" [routerLink]="['/subjects', name()]">Cancel</a>
            </div>
          }
        </form>
      }
    </section>
  `,
})
export class NewVersionPage {
  /** The subject name, bound from the route path. */
  readonly name = input.required<string>();

  protected readonly store = inject(NewVersionStore);
  protected readonly checks = inject(SchemaCheckStore);
  private readonly theme = inject(ThemeStore);

  /** Whether the editor should be drawn for the dark theme. */
  protected readonly dark = computed(() => this.theme.resolved() === 'dark');
  protected readonly environments = inject(ActiveEnvironmentStore);

  private readonly builder = inject(FormBuilder);

  protected readonly form = this.builder.nonNullable.group({
    // `required` and nothing else. What else is wrong with the text depends on the subject's
    // format, which arrives after the form is built, and a validator cannot read a signal
    // that has not settled — so the format-aware checks are `documentFindings` below, and
    // `canSubmit` joins them.
    schema: ['', [Validators.required]],
    semanticVersion: [''],
    changelog: [''],
  });

  /**
   * The form's own events, as a signal.
   *
   * A `computed` over `form.controls.x.invalid` would never recompute: an `AbstractControl`
   * is not a signal, so nothing tells the graph it changed and the message would be
   * whatever it was at construction. Reading this signal first is what makes the computeds
   * below track the form — `events` covers value, status *and* touched, where
   * `statusChanges` alone would miss a blur on an untouched field.
   */
  private readonly formEvents = toSignal(this.form.events);

  /**
   * A message to check against the schema. Outside the form group on purpose: it is not part of
   * what gets registered, so editing it must not withdraw a confirmation of what will be.
   */
  protected readonly sample = new FormControl('', { nonNullable: true });

  private readonly sampleText = toSignal(this.sample.valueChanges, { initialValue: '' });

  private readonly editor = viewChild(SchemaEditor);

  /** The editor's text, as typed. Positions in it are what the editor can underline. */
  private readonly editorText = computed(() => {
    this.formEvents();

    return this.form.controls.schema.value;
  });

  /** The text as the registry would receive it: trimmed, as `register` sends it. */
  private readonly schemaText = computed(() => this.editorText().trim());

  /** Whether there is anything in the editor that loading a version would overwrite. */
  protected readonly hasContent = computed(() => this.schemaText() !== '');

  /**
   * What the registry will certainly refuse about the text.
   *
   * Empty while the subject has not loaded, because every one of these depends on its
   * format: reading a Protobuf file as JSON would call a valid schema malformed.
   */
  private readonly documentFindings = computed((): readonly EditorFinding[] => {
    const format = this.store.format();

    if (format === null) {
      return [];
    }

    // An empty editor is `required`'s business and the placeholder's; a finding that says
    // "a schema body is required" over a box nobody has typed in yet is only noise.
    return checkSchemaText(this.editorText(), format).filter((f) => f.code !== 'schema_body_empty');
  });

  /**
   * Whether the last check was of what is on screen now.
   *
   * An answer about a document that has since been edited is an answer about something else,
   * and showing it beside the new text would be the page lying with a straight face. It is
   * hidden rather than dimmed.
   */
  protected readonly checkIsCurrent = computed(() => {
    const request = this.checks.request();

    return (
      this.checks.result() !== null &&
      request !== null &&
      request.schema === this.schemaText() &&
      request.payload === this.payloadText()
    );
  });

  /** There was an answer and the text has moved on. */
  protected readonly checkIsStale = computed(
    () => this.checks.result() !== null && !this.checkIsCurrent(),
  );

  protected readonly checkVisible = computed(
    () => this.checks.checking() || this.checks.failure() !== null || this.checks.result() !== null,
  );

  /** The sample message as it would be checked: trimmed, and absent rather than empty. */
  private readonly payloadText = computed(() => {
    const text = this.sampleText().trim();

    return text === '' ? null : text;
  });

  /**
   * What ajv thought of the schema, placed in the text.
   *
   * Advice, so it is shown beside the refusals but never joins canSubmit: the registry takes
   * a schema ajv has opinions about, and a block here would be stricter than the registry.
   */
  protected readonly adviceFindings = computed((): readonly EditorFinding[] => {
    const result = this.checks.result();

    if (result === null || !this.checkIsCurrent()) {
      return [];
    }

    // The check ran on the trimmed text; what is underlined is the editor's, which may begin
    // with whitespace the trimmed text does not have.
    const editor = this.editorText();
    const leading = editor.length - editor.trimStart().length;
    const parsed = parseJsonDocument(this.schemaText());

    return result.advice.map((a) => {
      const span = parsed.ok ? locatePointer(parsed.root, a.pointer) : null;

      return {
        severity: 'advice',
        code: a.code,
        message: a.message,
        position: span === null ? null : positionAt(editor, leading + span.start),
        // A container is pointed at by its opening character; underlining a whole nested object
        // would draw a line under half the document.
        length: 1,
      };
    });
  });

  /** The findings worth showing right now. */
  protected readonly visibleFindings = computed((): readonly EditorFinding[] => {
    this.formEvents();

    const control = this.form.controls.schema;

    // Held back until the field has been touched or edited. Complaining about an empty
    // editor the moment the screen opens is telling someone off for not having started.
    const refusals = control.touched || control.dirty ? this.documentFindings() : [];

    return [...refusals, ...this.adviceFindings()];
  });

  /** Whether the client-side check applies: JSON Schema only, since Avro is not a JSON Schema. */
  protected readonly canValidate = computed(
    () => this.hasContent() && this.store.format() === 'json',
  );

  /** Whether Format applies: the text is JSON, which JSON Schema and Avro both are. */
  protected readonly canFormat = computed(
    () => this.hasContent() && (this.store.format() === 'json' || this.store.format() === 'avro'),
  );

  /** What Format said when it could not do what was asked. */
  protected readonly formatMessage = signal<string | null>(null);

  /**
   * The label field's complaint, or its advice.
   *
   * A label that does not parse is a certain refusal and blocks; one that does not increase on
   * the highest live label is only advice, because the subject on screen may be older than the
   * registry's (a held version rejected since would have freed the number) — and advice that
   * can veto a submit is wrong in exactly that direction.
   */
  protected readonly labelFinding = computed((): EditorFinding | null => {
    this.formEvents();

    const text = this.form.controls.semanticVersion.value.trim();

    if (text === '') {
      return null;
    }

    const parsed = parseSemanticVersion(text);

    if (!parsed.ok) {
      return {
        severity: 'refusal',
        code: parsed.problem.kind === 'invalid' ? 'semver_invalid' : 'semver_build_metadata',
        message: parsed.problem.message,
        position: null,
        length: 0,
      };
    }

    const live = this.store.liveLabel();

    if (live !== null && compareSemanticVersions(parsed.value, live) <= 0) {
      return {
        severity: 'advice',
        code: 'semver_not_increasing',
        message:
          `${text} does not increase on ${formatSemanticVersion(live)}, the highest label on a ` +
          'live version. The registry will refuse it unless that version has been rejected since ' +
          'this page loaded.',
        position: null,
        length: 0,
      };
    }

    return null;
  });

  /** The label as it will be sent: trimmed, and absent rather than empty. */
  protected readonly sentLabel = computed(() => {
    this.formEvents();

    const text = this.form.controls.semanticVersion.value.trim();

    return text === '' ? null : text;
  });

  /** Whether the form is in a state worth sending. */
  protected readonly canSubmit = computed(() => {
    this.formEvents();

    return (
      this.form.valid &&
      this.store.subject() !== null &&
      !this.documentFindings().some((f) => f.severity === 'refusal') &&
      this.labelFinding()?.severity !== 'refusal' &&
      !this.store.submitting()
    );
  });

  /**
   * What the form said when the person asked to register, or null if they have not.
   *
   * <b>The confirmation belongs to one exact set of values.</b> It is stored as a snapshot and
   * `confirming` compares it with the form as it is now, so editing anything after the first
   * click withdraws it. Without that, the second click would send something the first never
   * showed.
   */
  protected readonly confirmation = signal<string | null>(null);

  private readonly snapshot = computed(() => {
    this.formEvents();

    return JSON.stringify(this.form.getRawValue());
  });

  protected readonly confirming = computed(
    () => this.confirmation() !== null && this.confirmation() === this.snapshot(),
  );

  constructor() {
    effect(() => {
      this.environments.name();
      this.store.loadSubject(this.name());
    });

    // Applies a cloned document to the editor, from what the registry returned.
    effect(() => {
      const cloned = this.store.cloned();

      if (cloned === null) {
        return;
      }

      untracked(() => {
        const control = this.form.controls.schema;

        control.setValue(cloned.text);
        control.markAsDirty();
        this.formatMessage.set(null);
      });
    });
  }

  protected format(): void {
    const formatted = formatJsonDocument(this.form.controls.schema.value);

    if (formatted === null) {
      // Not a guess at what the author meant: a document that does not read has no structure
      // to lay out, and re-indenting a broken one is how a Format button destroys work.
      this.formatMessage.set(
        'This is not well-formed JSON yet, so there is nothing to lay out. Fix the problem above first.',
      );
      return;
    }

    this.formatMessage.set(null);

    const control = this.form.controls.schema;

    control.setValue(formatted);
    control.markAsDirty();
  }

  protected validate(): void {
    this.checks.run({ schema: this.schemaText(), payload: this.payloadText() });
  }

  /**
   * Back to the form after a registration, to write the next version.
   *
   * The document stays, because the next version is nearly always an edit of the one just
   * registered. The label and the changelog do not: they described that version, and a label
   * cannot be used twice.
   */
  protected another(): void {
    this.store.reset();
    this.confirmation.set(null);
    this.form.controls.semanticVersion.reset('');
    this.form.controls.changelog.reset('');
  }

  protected pickLabel(label: string): void {
    const control = this.form.controls.semanticVersion;

    control.setValue(label);
    control.markAsDirty();
  }

  protected reveal(finding: EditorFinding): void {
    if (finding.position !== null) {
      this.editor()?.reveal(finding.position.offset);
    }
  }

  /** The first click: asks for confirmation rather than registering. */
  protected ask(): void {
    this.form.markAllAsTouched();

    if (!this.canSubmit()) {
      return;
    }

    this.confirmation.set(this.snapshot());
  }

  /** The second click: registers exactly what was confirmed. */
  protected register(): void {
    if (!this.confirming()) {
      return;
    }

    const { schema, changelog } = this.form.getRawValue();

    this.confirmation.set(null);
    this.store.submit({
      subject: this.name(),
      schema: schema.trim(),
      // Empty is absent, not an empty label. The API distinguishes them: a `""` semantic
      // version would be checked against the verdict and refused as unparseable.
      semanticVersion: this.sentLabel(),
      changelog: changelog.trim() === '' ? null : changelog.trim(),
    });
  }
}
