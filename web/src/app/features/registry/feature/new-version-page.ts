import { ChangeDetectionStrategy, Component, computed, effect, inject, input } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import {
  FormBuilder,
  ReactiveFormsModule,
  Validators,
  type AbstractControl,
  type ValidationErrors,
} from '@angular/forms';
import { RouterLink } from '@angular/router';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import { Icon } from '../../../shared/ui/icon/icon';
import { StatusBadge } from '../../../shared/ui/status-badge/status-badge';
import { NewVersionStore } from '../application/new-version-store';
import { DivergenceList } from '../ui/divergence-list';

/**
 * Whether a document is at least well-formed JSON.
 *
 * <b>Well-formedness only, deliberately.</b> Whether it is a *valid* schema is the registry's
 * question and it answers it authoritatively — checking here as well would mean two
 * implementations of the same rule, and the client's would be the one that goes stale.
 * `ajv` arrives in M4.4 to catch the rest before a round trip, which is a convenience; this
 * is the one check worth making without it, because a missing brace is a certain 400 and
 * there is no reason to spend a request finding out.
 *
 * Avro is JSON too. Protobuf is not, so this validator is only attached for the formats
 * where it means something.
 */
function wellFormedJson(control: AbstractControl): ValidationErrors | null {
  const value = String(control.value ?? '').trim();

  if (value === '') {
    return null;
  }

  try {
    JSON.parse(value);
    return null;
  } catch (error) {
    return { json: error instanceof Error ? error.message : 'Not valid JSON.' };
  }
}

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
  providers: [NewVersionStore],
  imports: [
    ReactiveFormsModule,
    RouterLink,
    HlmAlertImports,
    HlmButton,
    Icon,
    StatusBadge,
    DivergenceList,
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
            <button hlmBtn variant="ghost" size="sm" type="button" (click)="store.reset()">
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

        <form [formGroup]="form" (ngSubmit)="submit()" class="space-y-5">
          <div class="space-y-1.5">
            <label class="text-sm font-medium" for="schema">Schema document</label>
            <textarea
              id="schema"
              formControlName="schema"
              rows="18"
              spellcheck="false"
              placeholder='{ "type": "object", "properties": { } }'
              class="border-input bg-background focus-visible:ring-ring block w-full rounded-md border p-3 font-mono text-xs focus-visible:ring-1 focus-visible:outline-none"
            ></textarea>
            @if (schemaError(); as message) {
              <p class="text-destructive text-xs">{{ message }}</p>
            } @else {
              <p class="text-muted-foreground text-xs">
                The document as you want it stored. The registry canonicalises it to compute the id,
                but keeps what you sent.
              </p>
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

          <div class="flex items-center gap-2">
            <button hlmBtn type="submit" [disabled]="!canSubmit()">
              {{ store.submitting() ? 'Registering…' : 'Register' }}
            </button>
            <a hlmBtn variant="ghost" [routerLink]="['/subjects', name()]">Cancel</a>
          </div>
        </form>
      }
    </section>
  `,
})
export class NewVersionPage {
  /** The subject name, bound from the route path. */
  readonly name = input.required<string>();

  protected readonly store = inject(NewVersionStore);
  protected readonly environments = inject(ActiveEnvironmentStore);

  private readonly builder = inject(FormBuilder);

  protected readonly form = this.builder.nonNullable.group({
    schema: ['', [Validators.required, wellFormedJson]],
    semanticVersion: [''],
    changelog: [''],
  });

  /**
   * The form's own events, as a signal.
   *
   * A `computed` over `form.controls.x.invalid` would never recompute: an `AbstractControl`
   * is not a signal, so nothing tells the graph it changed and the message would be
   * whatever it was at construction. Reading this signal first is what makes the two
   * computeds below track the form — `events` covers value, status *and* touched, where
   * `statusChanges` alone would miss a blur on an untouched field.
   */
  private readonly formEvents = toSignal(this.form.events);

  /** The schema field's complaint, once it has one worth showing. */
  protected readonly schemaError = computed(() => {
    this.formEvents();

    const control = this.form.controls.schema;

    // Held back until the field has been touched or edited. Complaining about an empty
    // textarea the moment the screen opens is telling someone off for not having started.
    if (!control.touched && !control.dirty) {
      return null;
    }

    return control.hasError('json') ? `Not valid JSON: ${control.getError('json')}` : null;
  });

  /** Whether the form is in a state worth sending. */
  protected readonly canSubmit = computed(() => {
    this.formEvents();

    return this.form.valid && !this.store.submitting();
  });

  constructor() {
    effect(() => {
      this.environments.name();
      this.store.loadSubject(this.name());
    });
  }

  protected submit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    const { schema, semanticVersion, changelog } = this.form.getRawValue();

    this.store.submit({
      subject: this.name(),
      schema: schema.trim(),
      // Empty is absent, not an empty label. The API distinguishes them: a `""` semantic
      // version would be checked against the verdict and refused as unparseable.
      semanticVersion: semanticVersion.trim() === '' ? null : semanticVersion.trim(),
      changelog: changelog.trim() === '' ? null : changelog.trim(),
    });
  }
}
