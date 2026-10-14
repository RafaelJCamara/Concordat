import {
  ChangeDetectionStrategy,
  Component,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
import { IfScope } from '../../../core/auth/if-scope';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import { POLICY_WRITE_SCOPES, SCHEMA_WRITE_SCOPES } from '../../../domain/identity/scope';
import type { CompatibilityPolicy } from '../../../domain/registry/compatibility';
import { describePolicy } from '../../../domain/registry/subject-settings';
import { CompatibilityGuide } from '../../../shared/ui/compatibility-meaning/compatibility-guide';
import { Icon } from '../../../shared/ui/icon/icon';
import { StatusBadge } from '../../../shared/ui/status-badge/status-badge';
import { SubjectName } from '../../../shared/ui/subject-name/subject-name';
import { SubjectDetailStore } from '../application/subject-detail-store';
import { SubjectSettingsStore, type SettingsOutcome } from '../application/subject-settings-store';
import { SubjectOwnerEditor } from '../ui/subject-owner-editor';
import { SubjectPolicyEditor } from '../ui/subject-policy-editor';
import { VersionTable } from '../ui/version-table';

/**
 * One subject, its policy and its versions.
 *
 * <b>The name is a route parameter, not a query parameter</b> (DESIGN §9). The prototype
 * addressed a subject as `?subject=orders.created`, which cannot be pasted into an incident
 * channel without carrying whatever other state the page happened to have, and makes
 * `/subjects/:name/versions/:ordinal` impossible to express at all.
 *
 * Bound with `withComponentInputBinding`, so `name` arrives as a signal input and the effect
 * below reloads when it changes. That matters because Angular reuses this component when
 * navigating between two subjects: without the effect, clicking a reference to another
 * subject would leave the first one's versions on screen under the second one's heading.
 */
@Component({
  selector: 'cd-subject-detail-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [SubjectDetailStore, SubjectSettingsStore],
  imports: [
    RouterLink,
    HlmAlertImports,
    HlmButton,
    HlmSkeleton,
    CompatibilityGuide,
    Icon,
    IfScope,
    StatusBadge,
    SubjectName,
    SubjectOwnerEditor,
    SubjectPolicyEditor,
    VersionTable,
  ],
  template: `
    <section class="mx-auto w-full max-w-6xl space-y-6 p-6 lg:p-8">
      <!--
        A real link rather than a history.back() button. Someone who arrived here from a
        pasted URL has no history to go back to, and a control that does nothing on first
        load is worse than one that always goes to the list.
      -->
      <a
        routerLink="/subjects"
        class="text-muted-foreground hover:text-foreground inline-flex items-center gap-1 text-sm transition-colors"
      >
        <cd-icon name="chevron-left" size="0.875rem" />
        All subjects
      </a>

      @if (store.error(); as error) {
        <div hlmAlert variant="destructive">
          <h2 hlmAlertTitle>Could not load {{ name() }}</h2>
          <p hlmAlertDescription>{{ error.detail }}</p>
          <button hlmBtn hlmAlertAction variant="outline" size="sm" (click)="store.load(name())">
            Try again
          </button>
        </div>
      } @else if (store.loading()) {
        <div class="space-y-4" aria-busy="true" aria-live="polite">
          <div hlmSkeleton class="h-9 w-80"></div>
          <div hlmSkeleton class="h-24 w-full rounded-xl"></div>
          <div hlmSkeleton class="h-64 w-full rounded-xl"></div>
        </div>
      } @else if (store.subject(); as subject) {
        <header class="flex flex-col gap-4 sm:flex-row sm:items-start sm:justify-between">
          <div class="min-w-0">
            <h1 class="text-foreground text-2xl font-bold tracking-tight">
              <cd-subject-name [name]="subject.name" />
            </h1>
            <p class="text-muted-foreground mt-1">
              {{ subject.format }} · owned by {{ subject.owner }}
              @if (subject.lifecycle !== 'RETIRED') {
                <!--
                  Absent without a write scope rather than disabled (ADR-018), and absent for a
                  retired subject, which the registry refuses with 409: retired is terminal, so
                  nothing a stale page could be wrong about.
                -->
                <button
                  *cdIfScope="schemaWriteScopes"
                  hlmBtn
                  type="button"
                  variant="link"
                  size="sm"
                  class="h-auto p-0 align-baseline text-sm"
                  [attr.aria-expanded]="editing() === 'owner'"
                  (click)="edit('owner')"
                >
                  Change owner
                </button>
              }
              · <span class="font-mono">{{ environments.name() }}</span>
            </p>
          </div>

          <div class="flex shrink-0 items-center gap-2">
            @if (subject.lifecycle !== 'ACTIVE') {
              <cd-status-badge tone="neutral">{{ subject.lifecycle }}</cd-status-badge>
            }
            @if (store.pending().length > 0) {
              <cd-status-badge tone="warning" [pulse]="true">
                {{ store.pending().length }} awaiting approval
              </cd-status-badge>
            }
            <!--
              Absent for a non-admin rather than disabled (ADR-018). A disabled button invites
              a support ticket asking to be given the permission; an absent one does not raise
              the question. The route behind it carries the same scopes in its scopeGuard, and
              the server refuses the request either way.
            -->
            <a
              *cdIfScope="schemaWriteScopes"
              hlmBtn
              class="glow-primary gap-2"
              [routerLink]="['/subjects', subject.name, 'versions', 'new']"
            >
              <cd-icon name="plus" size="1rem" />
              New version
            </a>
          </div>
        </header>

        <!--
          What the registry said it did, in its own words and not the request's: the owner it
          stored (it trims) and the policy it answered with. A live region, because the editor
          that was open has closed and the person's focus has gone with it.
        -->
        <div role="status" aria-live="polite">
          @if (settings.outcome(); as outcome) {
            <p class="border-success/40 bg-success/5 text-foreground rounded-lg border p-3 text-sm">
              {{ outcomeText(outcome) }}
            </p>
          }
        </div>

        @if (store.refreshError(); as failure) {
          <div hlmAlert variant="destructive" role="alert">
            <h2 hlmAlertTitle>The change is in, but this page could not read it back</h2>
            <p hlmAlertDescription>
              {{ failure.detail }} What is shown may be out of date; reload to see the registry's
              account.
            </p>
          </div>
        }

        @switch (editing()) {
          @case ('owner') {
            <cd-subject-owner-editor
              [subject]="subject.name"
              [current]="subject.owner"
              [error]="settings.ownerError()?.detail ?? null"
              [saving]="settings.ownerSaving()"
              (confirmed)="settings.changeOwner({ subject: subject.name, owner: $event })"
              (cancelled)="closeEditor()"
            />
          }
          @case ('policy') {
            <cd-subject-policy-editor
              [subject]="subject.name"
              [current]="subject.compatibilityPolicy"
              [error]="settings.policyError()?.detail ?? null"
              [saving]="settings.policySaving()"
              (confirmed)="settings.setPolicy({ subject: subject.name, policy: $event })"
              (cancelled)="closeEditor()"
            />
          }
        }

        <dl
          class="border-border bg-card grid grid-cols-2 gap-4 rounded-xl border p-5 sm:grid-cols-4"
        >
          <div>
            <dt class="text-muted-foreground text-xs">Latest</dt>
            <dd class="text-foreground mt-1 font-mono font-medium">
              {{ store.latest() ? 'v' + store.latest()!.ordinal : '—' }}
            </dd>
          </div>
          <div>
            <dt class="text-muted-foreground text-xs">Versions</dt>
            <dd class="text-foreground mt-1 font-mono font-medium">
              {{ subject.versions.length }}
            </dd>
          </div>
          <div>
            <dt class="text-muted-foreground text-xs">Compatibility</dt>
            <!--
              Nulls together mean the subject inherits the environment default, which is not
              the same as having no policy. Saying "Inherited" rather than "—" is the
              difference between "nothing is checked" and "checked, by somebody else's rule".
            -->
            <dd class="text-foreground mt-1 font-mono font-medium">
              @if (subject.compatibilityPolicy.mode === null) {
                <span>Inherited</span>
              } @else {
                <span>{{ subject.compatibilityPolicy.mode }}</span>
                <span class="text-muted-foreground font-normal"
                  >· {{ subject.compatibilityPolicy.surface }}</span
                >
              }
              @if (subject.lifecycle !== 'RETIRED') {
                <!-- Narrower than the owner's control: the registry asks subject:admin for this one. -->
                <button
                  *cdIfScope="policyScopes"
                  hlmBtn
                  type="button"
                  variant="link"
                  size="sm"
                  class="ms-1 h-auto p-0 align-baseline font-sans text-xs"
                  [attr.aria-expanded]="editing() === 'policy'"
                  (click)="edit('policy')"
                >
                  Change policy
                </button>
              }
            </dd>
          </div>
          <div>
            <dt class="text-muted-foreground text-xs">Content model</dt>
            <dd class="text-foreground mt-1 font-mono font-medium">{{ subject.contentModel }}</dd>
          </div>
        </dl>

        @if (subject.compatibilityPolicy.mode !== null) {
          <!--
            Under the cells, full width, rather than in the Compatibility cell: the cell is half of
            a 390px screen, and an explanation of two terms does not fit in half of one. Only for
            a policy the subject set — an inheriting subject's own mode and surface are not on this
            page, so there is nothing here to explain.
          -->
          <cd-compatibility-guide
            [modes]="[subject.compatibilityPolicy.mode]"
            [surfaces]="[subject.compatibilityPolicy.surface]"
          />
        }

        @if (subject.versions.length === 0) {
          <div class="border-border bg-card rounded-xl border p-10 text-center">
            <p class="font-medium">No versions yet</p>
            <p class="text-muted-foreground mt-1 text-sm">
              The subject exists, which is the ordinary state right after creating one. Register a
              version to give it a contract.
            </p>
          </div>
        } @else {
          <cd-version-table
            [subject]="subject.name"
            [versions]="store.versions()"
            [latest]="subject.latest"
          />
        }
      }
    </section>
  `,
})
export class SubjectDetailPage {
  /** The subject name, bound from the route path. */
  readonly name = input.required<string>();

  protected readonly store = inject(SubjectDetailStore);
  protected readonly settings = inject(SubjectSettingsStore);
  protected readonly environments = inject(ActiveEnvironmentStore);

  /**
   * The scopes that permit registering a version, and changing the owner: the registry asks
   * `subject:write` for both, from the one list (ADR-018).
   */
  protected readonly schemaWriteScopes = SCHEMA_WRITE_SCOPES;

  /** The scopes that permit changing the compatibility policy, which the registry holds to `subject:admin`. */
  protected readonly policyScopes = POLICY_WRITE_SCOPES;

  /** Which editor is open. At most one: a person changing an owner is not also changing a policy. */
  protected readonly editing = signal<'owner' | 'policy' | null>(null);

  constructor() {
    // Reloads on the name changing and on an environment switch alike. Reading both signals
    // inside the effect is what registers the dependencies.
    effect(() => {
      this.environments.name();
      this.store.load(this.name());

      // An editor left open on the previous subject would offer to change the next one's owner
      // from a value that is not its own.
      untracked(() => {
        this.editing.set(null);
        this.settings.reset();
      });
    });

    // A change that landed closes the editor that made it: what is on screen from here on is the
    // registry's answer, in the status line, and the subject re-read behind it.
    effect(() => {
      if (this.settings.outcome() !== null) {
        untracked(() => this.editing.set(null));
      }
    });
  }

  protected edit(which: 'owner' | 'policy'): void {
    this.settings.reset();
    this.editing.set(this.editing() === which ? null : which);
  }

  protected closeEditor(): void {
    this.editing.set(null);
    this.settings.reset();
  }

  /** What the registry said it did, as a sentence. */
  protected outcomeText(outcome: SettingsOutcome): string {
    return outcome.kind === 'owner'
      ? `The owner of ${outcome.subject} is now ${outcome.owner}, as the registry stored it.`
      : this.policyText(outcome.subject, outcome.policy);
  }

  private policyText(subject: string, policy: CompatibilityPolicy): string {
    return policy.mode === null
      ? `${subject} now has no policy of its own. The registry answered that it inherits.`
      : `The policy of ${subject} is now ${describePolicy(policy)}, as the registry answered.`;
  }
}
