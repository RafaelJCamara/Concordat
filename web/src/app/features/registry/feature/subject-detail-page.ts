import { ChangeDetectionStrategy, Component, effect, inject, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import { Icon } from '../../../shared/ui/icon/icon';
import { StatusBadge } from '../../../shared/ui/status-badge/status-badge';
import { SubjectDetailStore } from '../application/subject-detail-store';
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
  providers: [SubjectDetailStore],
  imports: [RouterLink, HlmAlertImports, HlmButton, HlmSkeleton, Icon, StatusBadge, VersionTable],
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
            <h1 class="text-foreground truncate font-mono text-2xl font-bold tracking-tight">
              {{ subject.name }}
            </h1>
            <p class="text-muted-foreground mt-1">
              {{ subject.format }} · owned by {{ subject.owner }} ·
              <span class="font-mono">{{ environments.name() }}</span>
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
              The "New version" affordance lands with NewVersionPage, in the same commit as
              its route. Adding the button first would point it at the wildcard route, which
              silently redirects to the dashboard — a button that appears to work and quietly
              does the wrong thing, which is worse than no button at all.
            -->
          </div>
        </header>

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
              {{ subject.compatibilityPolicy.mode ?? 'Inherited' }}
            </dd>
          </div>
          <div>
            <dt class="text-muted-foreground text-xs">Content model</dt>
            <dd class="text-foreground mt-1 font-mono font-medium">{{ subject.contentModel }}</dd>
          </div>
        </dl>

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
  protected readonly environments = inject(ActiveEnvironmentStore);

  constructor() {
    // Reloads on the name changing and on an environment switch alike. Reading both signals
    // inside the effect is what registers the dependencies.
    effect(() => {
      this.environments.name();
      this.store.load(this.name());
    });
  }
}
