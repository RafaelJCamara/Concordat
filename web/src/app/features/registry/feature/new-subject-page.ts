import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { SessionStore } from '../../../core/auth/session-store';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import {
  COMPATIBILITY_MODES,
  COMPATIBILITY_SURFACES,
  type CompatibilityMode,
  type CompatibilitySurface,
} from '../../../domain/registry/compatibility';
import {
  CONTENT_MODEL_CHOICES,
  FORMAT_CHOICES,
  checkNewSubject,
  toNewSubject,
  type NewSubjectDraft,
  type NewSubjectField,
  type NewSubjectFinding,
} from '../../../domain/registry/new-subject';
import { checkSubjectName, slugSubjectName } from '../../../domain/registry/subject-name';
import type { ContentModel, SchemaFormat } from '../../../domain/registry/wire-tokens';
import { CompatibilityGuide } from '../../../shared/ui/compatibility-meaning/compatibility-guide';
import { CompatibilityMeaning } from '../../../shared/ui/compatibility-meaning/compatibility-meaning';
import { Icon } from '../../../shared/ui/icon/icon';
import { NewSubjectStore } from '../application/new-subject-store';
import { SubjectListStore } from '../application/subject-list-store';
import { CreateSubjectConfirmation } from '../ui/create-subject-confirmation';

const INPUT =
  'border-input bg-background focus-visible:ring-ring flex h-9 w-full rounded-md border px-3 py-1 text-sm focus-visible:ring-1 focus-visible:outline-none';

/**
 * Create a subject.
 *
 * <b>The last step of M4's exit criterion that was missing</b>: every flow after "create a
 * subject" was in the UI and this one was not, so a subject had to be made through the API or the
 * CLI before the app could do anything with it. The route carries the same `schemaWriteGuard`
 * `NewVersionPage` does, and the buttons that lead here are `*cdIfScope` on the same constant —
 * absent, not disabled, for a caller without the scope (ADR-018).
 *
 * <b>Nothing is applied ahead of the registry.</b> The screen sends the form and waits; what it
 * navigates to is the name the registry answered with (it trims before it validates, so that can
 * differ from what was typed), and the subject page it lands on reads the registry afresh rather
 * than being handed a subject to show. A subject appears in the list because the list is asked,
 * not because this page remembered it.
 *
 * <b>The name is suggested from a title, and followed until it is edited</b> — the prototype's
 * auto-slugged id with its "touched" flag. The rule is `slugSubjectName`, derived from what the
 * registry refuses; this component only decides when to apply it. "Touched" is *the author
 * edited the name by hand* and is set from the input's own event, which programmatic updates do
 * not fire, so following the title never counts as editing. It is not Angular's own `touched`,
 * which means *blurred* and is what decides when a complaint becomes visible.
 *
 * <b>Creating takes two clicks</b>, because a subject cannot be renamed, and deleting one is for an administrator, through the API, and
 * only once it is retired (ADR-046); the second click says so. The confirmation is a snapshot of what would be sent, so editing
 * anything that matters withdraws it; editing the title does not, since it is not sent.
 */
@Component({
  selector: 'cd-new-subject-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [NewSubjectStore, SubjectListStore],
  imports: [
    ReactiveFormsModule,
    RouterLink,
    HlmAlertImports,
    HlmButton,
    Icon,
    CreateSubjectConfirmation,
    CompatibilityGuide,
    CompatibilityMeaning,
  ],
  template: `
    <section class="mx-auto w-full max-w-2xl space-y-6 p-6 lg:p-8">
      <a
        routerLink="/subjects"
        class="text-muted-foreground hover:text-foreground inline-flex items-center gap-1 text-sm transition-colors"
      >
        <cd-icon name="chevron-left" size="0.875rem" />
        All subjects
      </a>

      <header>
        <h1 class="text-foreground text-2xl font-bold tracking-tight">New subject</h1>
        <p class="text-muted-foreground mt-1">
          A message contract in <span class="font-mono">{{ environments.name() }}</span
          >. Its name is permanent, so creating one asks twice.
        </p>
      </header>

      @if (store.error(); as failure) {
        <div hlmAlert variant="destructive" role="alert">
          <h2 hlmAlertTitle>The registry refused this subject</h2>
          <p hlmAlertDescription>{{ failure.error.detail }}</p>
          @if (failure.error.code === 'subject_already_exists') {
            <a
              hlmBtn
              hlmAlertAction
              variant="outline"
              size="sm"
              [routerLink]="['/subjects', failure.attempted]"
            >
              Open <span class="font-mono">{{ failure.attempted }}</span>
            </a>
          }
        </div>
      }

      <form [formGroup]="form" (ngSubmit)="ask()" class="space-y-6" novalidate>
        <div class="space-y-1.5">
          <label class="text-sm font-medium" for="subject-title">
            Title <span class="text-muted-foreground font-normal">· optional</span>
          </label>
          <input
            id="subject-title"
            formControlName="title"
            autocomplete="off"
            placeholder="Order created"
            [class]="input"
          />
          <p class="text-muted-foreground text-xs">
            Some words for what this message is. They only suggest the name below; the registry does
            not keep them.
          </p>
        </div>

        <div class="space-y-1.5">
          <label class="text-sm font-medium" for="subject-name">Subject name</label>
          <input
            id="subject-name"
            formControlName="name"
            autocomplete="off"
            autocapitalize="off"
            spellcheck="false"
            placeholder="acme.orders.OrderCreated"
            aria-describedby="subject-name-help"
            [attr.aria-invalid]="nameRefusal() ? 'true' : null"
            [class]="input + ' font-mono'"
            (input)="touchName()"
          />

          <div id="subject-name-help" class="space-y-1.5" aria-live="polite">
            @if (nameRefusal(); as finding) {
              <p class="text-destructive text-xs">{{ finding.message }}</p>
            }
            @for (finding of nameAdvice(); track finding.code) {
              <p class="text-warning text-xs">
                {{ finding.message }}
                @if (finding.code === 'subject_exists') {
                  <a class="text-primary underline" [routerLink]="['/subjects', trimmedName()]"
                    >Open it</a
                  >
                }
              </p>
            }

            @if (!nameRefusal()) {
              <p class="text-muted-foreground text-xs">
                @if (!nameTouched() && titleText().trim() !== '') {
                  Following the title: edit it here and it stops. Dot-separated segments of letters,
                  digits and underscores, such as
                  <span class="font-mono">acme.orders.OrderCreated</span>.
                } @else {
                  Dot-separated segments of letters, digits and underscores, such as
                  <span class="font-mono">acme.orders.OrderCreated</span>. Case counts:
                  <span class="font-mono">Orders</span> and
                  <span class="font-mono">orders</span> are different subjects.
                }
              </p>
            }

            @if (suggestionOffered()) {
              <button
                hlmBtn
                type="button"
                variant="link"
                size="sm"
                class="h-auto p-0 text-xs"
                (click)="followTitle()"
              >
                Use <span class="font-mono">{{ suggestion() }}</span> from the title instead
              </button>
            }
          </div>
        </div>

        <fieldset class="space-y-2">
          <legend class="text-sm font-medium">Format</legend>
          <div class="flex flex-wrap gap-x-6 gap-y-2">
            @for (choice of formats; track choice.value) {
              <label class="flex items-center gap-2 text-sm">
                <input type="radio" formControlName="format" [value]="choice.value" />
                {{ choice.label }}
              </label>
            }
          </div>
          <p class="text-muted-foreground text-xs">
            What every version of this subject is written in. It cannot be changed afterwards.
          </p>
        </fieldset>

        <div class="space-y-1.5">
          <label class="text-sm font-medium" for="subject-owner">Owner</label>
          <input
            id="subject-owner"
            formControlName="owner"
            autocomplete="off"
            aria-describedby="subject-owner-help"
            [attr.aria-invalid]="ownerRefusal() ? 'true' : null"
            [class]="input"
          />
          <div id="subject-owner-help" class="space-y-1.5" aria-live="polite">
            @if (ownerRefusal(); as finding) {
              <p class="text-destructive text-xs">{{ finding.message }}</p>
            } @else {
              <p class="text-muted-foreground text-xs">
                Who answers for this contract: a person or, more often, a team. Defaults to you.
              </p>
            }
          </div>
        </div>

        <fieldset class="space-y-2">
          <legend class="text-sm font-medium">Content model</legend>
          @for (choice of contentModels; track choice.value) {
            <label class="flex items-start gap-2 text-sm">
              <input
                type="radio"
                formControlName="contentModel"
                class="mt-1"
                [value]="choice.value"
              />
              <span>
                <span class="font-medium">{{ choice.label }}</span>
                <span class="text-muted-foreground block text-xs">{{ choice.hint }}</span>
              </span>
            </label>
          }
          <p class="text-muted-foreground text-xs">
            A setting of the subject, never read from a schema, so it cannot drift between versions.
            It cannot be changed afterwards.
          </p>
        </fieldset>

        <fieldset class="space-y-2">
          <legend class="text-sm font-medium">Compatibility policy</legend>
          <div class="grid gap-4 sm:grid-cols-2">
            <div class="space-y-1.5">
              <label class="text-muted-foreground text-xs" for="subject-mode">Mode</label>
              <select
                id="subject-mode"
                formControlName="mode"
                aria-describedby="subject-mode-meaning"
                [class]="input"
              >
                <option value="">Inherit the environment default</option>
                @for (mode of modes; track mode) {
                  <option [value]="mode">{{ mode }}</option>
                }
              </select>
              <cd-compatibility-meaning
                axis="mode"
                [value]="chosenMode()"
                textId="subject-mode-meaning"
              />
            </div>
            <div class="space-y-1.5">
              <label class="text-muted-foreground text-xs" for="subject-surface">Surface</label>
              <select
                id="subject-surface"
                formControlName="surface"
                aria-describedby="subject-surface-meaning"
                [class]="input"
              >
                <option value="">Inherit the environment default</option>
                @for (surface of surfaces; track surface) {
                  <option [value]="surface">{{ surface }}</option>
                }
              </select>
              <cd-compatibility-meaning
                axis="surface"
                [value]="chosenSurface()"
                textId="subject-surface-meaning"
              />
            </div>
          </div>
          <cd-compatibility-guide [modes]="modes" [surfaces]="surfaces" />
          <div aria-live="polite">
            @if (compatibilityRefusal(); as finding) {
              <p class="text-destructive text-xs">{{ finding.message }}</p>
            } @else {
              <p class="text-muted-foreground text-xs">
                Both or neither. Leaving both on the default means this subject follows whatever the
                environment says, now and when that changes; choosing them pins this subject to
                them.
              </p>
            }
          </div>
        </fieldset>

        @if (confirming(); as request) {
          <cd-create-subject-confirmation
            [name]="request.name"
            [environment]="environments.name()"
            [formatLabel]="formatLabel(request.format)"
            [contentModelLabel]="contentModelLabel(request.contentModel)"
            [busy]="store.submitting()"
            (confirmed)="create()"
            (declined)="confirmation.set(null)"
          />
        } @else {
          <div class="flex items-center gap-2">
            <button hlmBtn type="submit" [disabled]="!canSubmit()">
              {{ store.submitting() ? 'Creating…' : 'Create subject' }}
            </button>
            <a hlmBtn variant="ghost" routerLink="/subjects">Cancel</a>
          </div>
        }
      </form>
    </section>
  `,
})
export class NewSubjectPage {
  protected readonly store = inject(NewSubjectStore);
  protected readonly environments = inject(ActiveEnvironmentStore);
  private readonly list = inject(SubjectListStore);
  private readonly router = inject(Router);

  protected readonly input = INPUT;
  protected readonly formats = FORMAT_CHOICES;
  protected readonly contentModels = CONTENT_MODEL_CHOICES;
  protected readonly modes = COMPATIBILITY_MODES;
  protected readonly surfaces = COMPATIBILITY_SURFACES;

  private readonly builder = inject(FormBuilder);
  private readonly session = inject(SessionStore);

  protected readonly form = this.builder.nonNullable.group({
    title: [''],
    name: [''],
    format: ['json' as SchemaFormat],
    // Defaults to the signed-in caller, who is the right owner far more often than anyone else
    // and is what the CLI does too. Empty on an unclaimed instance, which has no one to be.
    owner: [this.session.actor() ?? ''],
    contentModel: ['OPEN' as ContentModel],
    // Empty is "inherit". Both or neither is the registry's rule, so there is no separate
    // "set one" switch for it to disagree with.
    mode: ['' as CompatibilityMode | ''],
    surface: ['' as CompatibilitySurface | ''],
  });

  /**
   * The form's own events, as a signal.
   *
   * A `computed` over a control's value would never recompute — an `AbstractControl` is not a
   * signal — so every computed below reads this first. It covers value, status *and* touched.
   */
  private readonly formEvents = toSignal(this.form.events);

  /** The mode and surface the form holds, for the sentence under each select. */
  protected readonly chosenMode = computed(() => {
    this.formEvents();

    return this.form.controls.mode.value;
  });

  protected readonly chosenSurface = computed(() => {
    this.formEvents();

    return this.form.controls.surface.value;
  });

  protected readonly titleText = computed(() => {
    this.formEvents();

    return this.form.controls.title.value;
  });

  /**
   * Whether the author has edited the name by hand, which stops it following the title.
   *
   * Set from the input's own `input` event and from nothing else. Typing in the title updates
   * the name programmatically, which fires no such event, so following never counts as editing —
   * and a name the author typed first is theirs from the first keystroke.
   */
  protected readonly nameTouched = signal(false);

  /** What the title would make of the name, by the one rule the registry's refusals imply. */
  protected readonly suggestion = computed(() => slugSubjectName(this.titleText()));

  /**
   * Whether to offer to go back to following the title: the name was edited, there is something
   * to follow, and it is not already what the title says.
   */
  protected readonly suggestionOffered = computed(() => {
    this.formEvents();

    return (
      this.nameTouched() &&
      this.suggestion() !== '' &&
      this.suggestion() !== this.form.controls.name.value.trim()
    );
  });

  /** What is being created, as the form stands: judged by the registry's own rules. */
  private readonly draft = computed((): NewSubjectDraft => {
    this.formEvents();

    const { name, format, owner, contentModel, mode, surface } = this.form.getRawValue();

    return {
      name,
      format,
      owner,
      contentModel,
      mode: mode === '' ? null : mode,
      surface: surface === '' ? null : surface,
    };
  });

  /** The name the registry will store, for the link to an existing subject of that name. */
  protected readonly trimmedName = computed(() => {
    const check = checkSubjectName(this.draft().name);

    return check.ok ? check.name : '';
  });

  private readonly findings = computed(() =>
    checkNewSubject(
      this.draft(),
      this.list.subjects().map((subject) => subject.name),
    ),
  );

  /**
   * Whether somebody has had a chance to see a complaint about a field.
   *
   * A complaint about an empty form the moment it opens is telling somebody off for not having
   * started. A field earns its refusals by being edited or left, or by a submit attempt, which
   * marks everything touched.
   */
  private readonly attempted = computed(() => {
    this.formEvents();

    return (field: NewSubjectField): boolean => {
      const control = {
        name: this.form.controls.name,
        owner: this.form.controls.owner,
        compatibility: this.form.controls.surface,
      }[field];
      const sibling = field === 'compatibility' ? this.form.controls.mode : control;

      return control.touched || control.dirty || sibling.touched || sibling.dirty;
    };
  });

  private refusal(field: NewSubjectField): NewSubjectFinding | null {
    return (
      this.findings().find(
        (f) => f.field === field && f.severity === 'refusal' && this.attempted()(field),
      ) ?? null
    );
  }

  protected readonly nameRefusal = computed(() => this.refusal('name'));
  protected readonly ownerRefusal = computed(() => this.refusal('owner'));
  protected readonly compatibilityRefusal = computed(() => this.refusal('compatibility'));

  protected readonly nameAdvice = computed(() =>
    this.findings().filter((f) => f.field === 'name' && f.severity === 'advice'),
  );

  /** Whether the form is in a state worth sending: the registry would not certainly refuse it. */
  protected readonly canSubmit = computed(
    () => toNewSubject(this.draft()) !== null && !this.store.submitting(),
  );

  /**
   * What the form said when the person asked to create, or null if they have not.
   *
   * <b>The confirmation belongs to one exact request.</b> It is a snapshot of what would be
   * sent, and {@link confirming} compares it with the form as it is now, so editing the name,
   * the format, the owner, the content model or the policy after the first click withdraws it.
   * The title is not in it, because it is not sent.
   */
  protected readonly confirmation = signal<string | null>(null);

  private readonly snapshot = computed(() => JSON.stringify(toNewSubject(this.draft())));

  /** The request the confirmation is for, while it still describes the form. */
  protected readonly confirming = computed(() => {
    const request = toNewSubject(this.draft());

    return request !== null &&
      this.confirmation() !== null &&
      this.confirmation() === this.snapshot()
      ? request
      : null;
  });

  constructor() {
    // Follows the title until the name has been edited by hand.
    this.form.controls.title.valueChanges.pipe(takeUntilDestroyed()).subscribe((title) => {
      if (!this.nameTouched()) {
        this.form.controls.name.setValue(slugSubjectName(title));
      }
    });

    // The list is read for one thing — whether this name is already there — and a failure to
    // read it must not stop anybody creating a subject, so nothing here renders its error.
    effect(() => {
      this.environments.name();
      this.list.load();
    });

    // Lands on the subject the registry answered with. Not on what was typed (it trims), and not
    // with the subject handed over: the page it opens asks the registry again, so what is shown
    // is the registry's account of it now.
    effect(() => {
      const created = this.store.created();

      if (created !== null) {
        untracked(() => void this.router.navigate(['/subjects', created.name]));
      }
    });
  }

  protected touchName(): void {
    this.nameTouched.set(true);
  }

  /** Goes back to following the title, which the author had stopped by editing the name. */
  protected followTitle(): void {
    this.nameTouched.set(false);
    this.form.controls.name.setValue(this.suggestion());
  }

  protected formatLabel(format: SchemaFormat): string {
    return this.formats.find((choice) => choice.value === format)?.label ?? format;
  }

  protected contentModelLabel(model: ContentModel): string {
    return (
      this.contentModels.find((choice) => choice.value === model)?.label ?? model
    ).toLowerCase();
  }

  /** The first click: asks for confirmation rather than creating. */
  protected ask(): void {
    this.form.markAllAsTouched();

    if (!this.canSubmit()) {
      return;
    }

    this.store.reset();
    this.confirmation.set(this.snapshot());
  }

  /** The second click: creates exactly what was confirmed. */
  protected create(): void {
    const request = this.confirming();

    if (request === null) {
      return;
    }

    this.confirmation.set(null);
    this.store.create(request);
  }
}
