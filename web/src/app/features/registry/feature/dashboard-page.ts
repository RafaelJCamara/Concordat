import { ChangeDetectionStrategy, Component, computed, effect, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import { latestVersion, pendingVersions } from '../../../domain/registry/subject';
import { Icon } from '../../../shared/ui/icon/icon';
import { StatCard } from '../../../shared/ui/stat-card/stat-card';
import { SubjectListStore } from '../application/subject-list-store';
import { SubjectCard } from '../ui/subject-card';

/** How many subjects the overview shows before it stops and points at the full list. */
const RECENT_LIMIT = 6;

/**
 * The overview screen, and the app's landing page.
 *
 * DESIGN §9 turns the prototype's Dashboard into an environment-scoped overview, and this
 * is the first slice of it: the counts that answer "is anything waiting on me", then the
 * most recently registered subjects. Breaking changes and enforcement coverage join them
 * when the endpoints behind those exist.
 *
 * <b>It reads the subject list and derives every number from it, rather than calling a
 * stats endpoint that does not exist.</b> That is honest for a registry of this size — the
 * list is already one request — and it is a decision with a limit: the day a registry has
 * ten thousand subjects, these counts need to come from the server, because paginating the
 * list would silently start under-counting them. Whoever adds pagination has to come here.
 */
@Component({
  selector: 'cd-dashboard-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [SubjectListStore],
  imports: [RouterLink, HlmAlertImports, HlmButton, HlmSkeleton, Icon, StatCard, SubjectCard],
  template: `
    <section class="mx-auto w-full max-w-6xl space-y-8 p-6 lg:p-8">
      <header>
        <h1 class="text-foreground text-2xl font-bold tracking-tight">Dashboard</h1>
        <p class="text-muted-foreground mt-1">
          Subjects and contracts in
          <span class="font-mono">{{ environments.name() }}</span>
        </p>
      </header>

      @if (store.error(); as error) {
        <div hlmAlert variant="destructive">
          <h2 hlmAlertTitle>Could not load this environment</h2>
          <p hlmAlertDescription>{{ error.detail }}</p>
          <button hlmBtn hlmAlertAction variant="outline" size="sm" (click)="store.load()">
            Try again
          </button>
        </div>
      } @else {
        <div class="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
          <cd-stat-card
            title="Subjects"
            [value]="store.loading() ? '—' : subjectCount()"
            subtitle="Message contracts"
            icon="file-json"
            route="/subjects"
          />
          <cd-stat-card
            title="Versions"
            [value]="store.loading() ? '—' : versionCount()"
            subtitle="Registered across all subjects"
            icon="git-branch"
          />
          <cd-stat-card
            title="Awaiting approval"
            [value]="store.loading() ? '—' : pendingCount()"
            [subtitle]="pendingSubtitle()"
            icon="file-check"
          />
        </div>

        <section>
          <div class="mb-4 flex items-center justify-between">
            <h2 class="text-foreground text-lg font-semibold">Recent subjects</h2>
            <a
              routerLink="/subjects"
              class="text-muted-foreground hover:text-foreground flex items-center gap-1 text-sm transition-colors"
            >
              View all
              <cd-icon name="arrow-right" size="0.875rem" />
            </a>
          </div>

          @if (store.loading()) {
            <div class="grid grid-cols-1 gap-4 md:grid-cols-2 lg:grid-cols-3" aria-busy="true">
              @for (row of skeletonCards; track row) {
                <div hlmSkeleton class="h-36 w-full rounded-xl"></div>
              }
            </div>
          } @else if (store.isEmpty()) {
            <div class="border-border bg-card rounded-xl border p-10 text-center">
              <p class="font-medium">No subjects yet</p>
              <p class="text-muted-foreground mt-1 text-sm">
                A subject has to exist before a version can be registered, so nothing is wrong —
                there is simply nothing here yet.
              </p>
            </div>
          } @else {
            <div class="grid grid-cols-1 gap-4 md:grid-cols-2 lg:grid-cols-3">
              @for (subject of recent(); track subject.name) {
                <cd-subject-card [subject]="subject" />
              }
            </div>
          }
        </section>
      }
    </section>
  `,
})
export class DashboardPage {
  protected readonly store = inject(SubjectListStore);
  protected readonly environments = inject(ActiveEnvironmentStore);

  /** Enough cards to fill the grid a short list would occupy, so the layout does not jump. */
  protected readonly skeletonCards = [0, 1, 2];

  protected readonly subjectCount = computed(() => this.store.subjects().length);

  protected readonly versionCount = computed(() =>
    this.store.subjects().reduce((total, subject) => total + subject.versions.length, 0),
  );

  protected readonly pendingCount = computed(() =>
    this.store.subjects().reduce((total, subject) => total + pendingVersions(subject).length, 0),
  );

  /**
   * Says nothing is waiting rather than leaving a bare zero to be interpreted.
   *
   * A zero on an approvals counter is ambiguous in a way the other two are not: it reads
   * equally as "nothing needs approving" and "approvals aren't switched on".
   */
  protected readonly pendingSubtitle = computed(() =>
    this.pendingCount() === 0 ? 'Nothing waiting' : 'Held at the approval gate',
  );

  /**
   * The most recently registered subjects.
   *
   * Sorted by the registration time of whatever `latest` points at, newest first. A subject
   * with no active version has no such time and sorts last rather than being dropped — it
   * is the newest thing in the registry by one reading, and hiding it from the overview is
   * how someone creates a subject and concludes the registry did not save it.
   */
  protected readonly recent = computed(() =>
    [...this.store.subjects()]
      .sort((a, b) => registeredTime(b) - registeredTime(a))
      .slice(0, RECENT_LIMIT),
  );

  constructor() {
    // Reloads on an environment switch as well as on entry. Reading the signal inside the
    // effect is what registers the dependency — an `ngOnInit` call would load once and then
    // silently show the previous environment's subjects after a switch.
    effect(() => {
      this.environments.name();
      this.store.load();
    });
  }
}

/** Epoch milliseconds of a subject's active version, or `-Infinity` when it has none. */
function registeredTime(subject: Parameters<typeof latestVersion>[0]): number {
  return latestVersion(subject)?.registeredAt.getTime() ?? Number.NEGATIVE_INFINITY;
}
