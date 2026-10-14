import { ChangeDetectionStrategy, Component, computed, effect, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
import { IfScope } from '../../../core/auth/if-scope';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import {
  REGISTRATION_POLICIES,
  REGISTRATION_POLICY_MEANING,
  type Environment,
  type EnvironmentChanges,
  type RegistrationPolicy,
} from '../../../domain/environments/environment';
import { ENV_WRITE_SCOPES } from '../../../domain/identity/scope';
import {
  COMPATIBILITY_MODES,
  COMPATIBILITY_SURFACES,
  type CompatibilityMode,
  type CompatibilitySurface,
} from '../../../domain/registry/compatibility';
import { CompatibilityGuide } from '../../../shared/ui/compatibility-meaning/compatibility-guide';
import { CompatibilityMeaning } from '../../../shared/ui/compatibility-meaning/compatibility-meaning';
import { StatusBadge } from '../../../shared/ui/status-badge/status-badge';
import { EnvironmentStore } from '../application/environment-store';
import { SettingsNav } from './settings-nav';

const INPUT =
  'border-input bg-background focus-visible:ring-ring flex h-9 w-full rounded-md border px-3 py-1 text-sm focus-visible:ring-1 focus-visible:outline-none';

interface FormValue {
  readonly description: string;
  readonly mode: string;
  readonly surface: string;
  readonly policy: string;
  readonly preRelease: boolean;
}

/**
 * An environment's own settings: what it is called, what its subjects inherit, and who may
 * register into it.
 *
 * <b>Readable by anyone, editable only with `env:write`</b> (ADR-018). Without the scope the
 * values are shown as text and there is no form to be disabled.
 *
 * <b>Only what changed is sent, except that the compatibility default is one thing, not two.</b>
 * The registry leaves an omitted member alone, so saving a new description does not also
 * restate the default, and a default nobody touched is not sent. But it refuses half a policy
 * (`400`, "needs both a mode and a surface", ADR-016), so changing either select sends both: the
 * one that moved and the one that did not, as the registry holds it. The form used to send only
 * the member that changed, so changing the mode alone, the obvious edit, could not be saved.
 *
 * <b>Changing a default takes effect at once for every subject that has not set its own</b> —
 * that is what inheritance is for — and the form says so beside the control, because it is the
 * one setting here whose blast radius is not visible from the field. "At once" is the next version
 * registered, checked or compared, and it is true at registration only since ADR-033 (before it,
 * registration, the dry run and the diff read the registry's built-in default instead); versions
 * already registered or held are not judged again, and the sentence says so.
 *
 * <b>There is no "not set" default to choose or to show.</b> An environment created without one
 * stores the built-in `BACKWARD · WIRE_JSON`, and an update with neither member leaves it alone
 * (ADR-033), so both selects always hold a real token.
 */
@Component({
  selector: 'cd-environment-settings-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [EnvironmentStore],
  imports: [
    ReactiveFormsModule,
    HlmAlertImports,
    HlmButton,
    HlmSkeleton,
    CompatibilityGuide,
    CompatibilityMeaning,
    IfScope,
    SettingsNav,
    StatusBadge,
  ],
  template: `
    <section class="mx-auto w-full max-w-4xl space-y-6 p-6 lg:p-8">
      <header>
        <h1 class="text-foreground text-2xl font-bold tracking-tight">Settings</h1>
        <p class="text-muted-foreground mt-1">
          <span class="font-mono">{{ environments.name() }}</span>
        </p>
      </header>

      <cd-settings-nav />

      <div role="status" aria-live="polite">
        @if (store.lastSaved() === 'settings') {
          <div hlmAlert>
            <p hlmAlertDescription>Settings saved.</p>
          </div>
        }
      </div>

      @if (store.loading()) {
        <div class="space-y-3" aria-busy="true" aria-live="polite">
          <div hlmSkeleton class="h-24 w-full rounded-xl"></div>
          <div hlmSkeleton class="h-48 w-full rounded-xl"></div>
        </div>
      } @else if (store.environment(); as environment) {
        <dl class="border-border bg-card grid gap-4 rounded-xl border p-5 sm:grid-cols-2">
          <div>
            <dt class="text-muted-foreground text-xs">Registration</dt>
            <dd class="mt-1">
              <cd-status-badge [tone]="policyTone(environment.registrationPolicy)">
                {{ environment.registrationPolicy }}
              </cd-status-badge>
            </dd>
          </div>
          <div>
            <dt class="text-muted-foreground text-xs">Default compatibility</dt>
            <dd class="text-foreground mt-1 font-mono text-sm">
              {{ defaultPolicy(environment) }}
            </dd>
          </div>
          <div>
            <dt class="text-muted-foreground text-xs">Pre-release versions</dt>
            <dd class="text-foreground mt-1 text-sm">
              {{ environment.allowPreReleaseVersions ? 'Allowed' : 'Not allowed' }}
            </dd>
          </div>
          <div>
            <dt class="text-muted-foreground text-xs">Description</dt>
            <dd class="text-foreground mt-1 text-sm">
              {{ environment.description || '—' }}
            </dd>
          </div>
        </dl>

        <!--
          Whoever may read the settings may ask what the default is. Only the default that is
          shown: the form below has its own, with every word, for whoever is choosing.
        -->
        <cd-compatibility-guide
          [modes]="[environment.defaultCompatibility.mode]"
          [surfaces]="[environment.defaultCompatibility.surface]"
          label="What does this default mean?"
        />

        <form
          *cdIfScope="writeScopes"
          [formGroup]="form"
          (ngSubmit)="save()"
          class="border-border bg-card space-y-5 rounded-xl border p-5"
          aria-labelledby="edit-heading"
        >
          <h2 id="edit-heading" class="text-foreground text-sm font-semibold">Edit</h2>

          <div class="space-y-1.5">
            <label class="text-sm font-medium" for="env-description">Description</label>
            <input
              id="env-description"
              formControlName="description"
              autocomplete="off"
              [class]="input"
            />
          </div>

          <fieldset class="space-y-1.5">
            <legend class="text-sm font-medium">Registration policy</legend>
            <select
              id="env-policy"
              formControlName="policy"
              aria-label="Registration policy"
              [class]="input"
            >
              @for (policy of policies; track policy) {
                <option [value]="policy">{{ policy }}</option>
              }
            </select>
            <p class="text-muted-foreground text-xs">{{ policyMeaning() }}</p>
          </fieldset>

          <div class="grid gap-4 sm:grid-cols-2">
            <div class="space-y-1.5">
              <label class="text-sm font-medium" for="env-mode">Default compatibility mode</label>
              <select
                id="env-mode"
                formControlName="mode"
                aria-describedby="env-mode-meaning"
                [class]="input"
              >
                @for (mode of modes; track mode) {
                  <option [value]="mode">{{ mode }}</option>
                }
              </select>
              <cd-compatibility-meaning
                axis="mode"
                [value]="value().mode ?? null"
                textId="env-mode-meaning"
              />
            </div>

            <div class="space-y-1.5">
              <label class="text-sm font-medium" for="env-surface">
                Default compatibility surface
              </label>
              <select
                id="env-surface"
                formControlName="surface"
                aria-describedby="env-surface-meaning"
                [class]="input"
              >
                @for (surface of surfaces; track surface) {
                  <option [value]="surface">{{ surface }}</option>
                }
              </select>
              <cd-compatibility-meaning
                axis="surface"
                [value]="value().surface ?? null"
                textId="env-surface-meaning"
              />
            </div>
          </div>
          <cd-compatibility-guide
            [modes]="modes"
            [surfaces]="surfaces"
            label="What do these choices mean?"
          />
          <p class="text-muted-foreground -mt-2 text-xs">
            Takes effect at once for every subject here that has not set its own — which is what
            inheriting means: the next version registered, checked or compared is judged by it.
            Versions already registered, or held at the approval gate, are not judged again. Worth
            knowing before changing it on an environment with traffic.
          </p>

          <div class="flex items-start gap-3">
            <input
              id="env-prerelease"
              type="checkbox"
              formControlName="preRelease"
              class="mt-1 size-4"
            />
            <div>
              <label class="text-sm font-medium" for="env-prerelease">
                Allow pre-release versions
              </label>
              <p class="text-muted-foreground text-xs">
                Labels like <code>2.0.0-rc.1</code>. Typically on in dev and staging and off in
                production, so a candidate is exercised everywhere it should be and cannot reach
                production still wearing its suffix. Turning it off does not invalidate labels
                already registered: versions are immutable and their labels are history.
              </p>
            </div>
          </div>

          @if (store.actionErrors()['settings']; as error) {
            <div hlmAlert variant="destructive">
              <h3 hlmAlertTitle>Could not save the settings</h3>
              <p hlmAlertDescription>{{ error.detail }}</p>
            </div>
          }

          <div class="flex items-center gap-3">
            <button type="submit" hlmBtn size="sm" [disabled]="!dirty() || store.saving() !== null">
              Save changes
            </button>
            @if (!dirty()) {
              <span class="text-muted-foreground text-xs">Nothing has changed.</span>
            }
          </div>
        </form>
      } @else if (store.error(); as error) {
        <div hlmAlert variant="destructive">
          <h2 hlmAlertTitle>Could not load this environment</h2>
          <p hlmAlertDescription>{{ error.detail }}</p>
          <button hlmBtn hlmAlertAction variant="outline" size="sm" (click)="store.load()">
            Try again
          </button>
        </div>
      }
    </section>
  `,
})
export class EnvironmentSettingsPage {
  protected readonly store = inject(EnvironmentStore);
  protected readonly environments = inject(ActiveEnvironmentStore);

  protected readonly input = INPUT;
  protected readonly writeScopes = ENV_WRITE_SCOPES;
  protected readonly policies = REGISTRATION_POLICIES;
  protected readonly modes = COMPATIBILITY_MODES;
  protected readonly surfaces = COMPATIBILITY_SURFACES;

  protected readonly form = inject(FormBuilder).nonNullable.group({
    description: [''],
    mode: [''],
    surface: [''],
    policy: ['OPEN'],
    preRelease: [false],
  });

  /** What the form holds now, for the sentences under the compatibility selects. */
  protected readonly value = toSignal(this.form.valueChanges, {
    initialValue: this.form.getRawValue(),
  });

  /** Whether the form differs from the environment as the registry holds it. */
  protected readonly dirty = computed(() => {
    const environment = this.store.environment();

    return (
      environment !== null &&
      Object.keys(changesBetween(environment, this.value() as FormValue)).length > 0
    );
  });

  protected readonly policyMeaning = computed(() => {
    const chosen = this.value().policy as RegistrationPolicy | undefined;

    return chosen === undefined ? '' : REGISTRATION_POLICY_MEANING[chosen];
  });

  constructor() {
    effect(() => {
      this.environments.name();
      this.store.load();
    });

    // The form is the environment, re-read whenever the registry hands one back — on arrival
    // and after every save — so what is shown is always what is stored, not what was typed.
    effect(() => {
      const environment = this.store.environment();

      if (environment !== null) {
        this.form.reset(toFormValue(environment));
      }
    });
  }

  protected save(): void {
    const environment = this.store.environment();

    if (environment === null) {
      return;
    }

    const changes = changesBetween(environment, this.form.getRawValue());

    if (Object.keys(changes).length > 0) {
      this.store.update(changes);
    }
  }

  protected defaultPolicy(environment: Environment): string {
    const { mode, surface } = environment.defaultCompatibility;

    return `${mode} · ${surface}`;
  }

  protected policyTone(policy: RegistrationPolicy): 'success' | 'warning' | 'destructive' {
    return policy === 'OPEN' ? 'success' : policy === 'CI_ONLY' ? 'warning' : 'destructive';
  }
}

function toFormValue(environment: Environment): FormValue {
  return {
    description: environment.description ?? '',
    mode: environment.defaultCompatibility.mode,
    surface: environment.defaultCompatibility.surface,
    policy: environment.registrationPolicy,
    preRelease: environment.allowPreReleaseVersions,
  };
}

/**
 * What the form would change about an environment, and nothing it would not.
 *
 * A member the form leaves as it found it is left out, so saving one field never restates the
 * others. The exception is the compatibility default, whose two members are sent together
 * whenever either differs: the registry answers a lone one with a 400 (ADR-016), so a person
 * who changes only the mode has to be sent as changing the pair, with the surface as stored.
 *
 * <b>An empty select is not a choice.</b> The form starts empty and is reset to the environment
 * on arrival, and in that window a select has no value; a pair with an empty half would be a 400
 * about a value nobody picked, so it is left out of the changes rather than sent.
 */
export function changesBetween(environment: Environment, value: FormValue): EnvironmentChanges {
  const was = toFormValue(environment);
  const changes: {
    -readonly [K in keyof EnvironmentChanges]: EnvironmentChanges[K];
  } = {};

  if (value.description !== was.description) {
    changes.description = value.description;
  }

  const defaultChanged = value.mode !== was.mode || value.surface !== was.surface;

  if (defaultChanged && value.mode !== '' && value.surface !== '') {
    changes.compatibilityMode = value.mode as CompatibilityMode;
    changes.compatibilitySurface = value.surface as CompatibilitySurface;
  }

  if (value.policy !== was.policy) {
    changes.registrationPolicy = value.policy as RegistrationPolicy;
  }

  if (value.preRelease !== was.preRelease) {
    changes.allowPreReleaseVersions = value.preRelease;
  }

  return changes;
}
