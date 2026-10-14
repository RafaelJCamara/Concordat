import { ChangeDetectionStrategy, Component, computed, effect, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
import { IfScope } from '../../../core/auth/if-scope';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import { SCHEMA_WRITE_SCOPES } from '../../../domain/identity/scope';
import { Icon } from '../../../shared/ui/icon/icon';
import { StatCard } from '../../../shared/ui/stat-card/stat-card';
import { DashboardStore } from '../application/dashboard-store';
import { BreakingChangesPanel } from '../ui/breaking-changes-panel';
import { EnforcementPanel } from '../ui/enforcement-panel';
import { SubjectCard } from '../ui/subject-card';

/**
 * The overview screen, and the app's landing page.
 *
 * DESIGN §9 turns the prototype's Dashboard into an environment-scoped overview: the counts that
 * answer "is anything waiting on me", the breaking changes the approval gate is holding or turned
 * away, how contracts are set to enforce, and the newest subjects.
 *
 * <b>Every number comes from the registry's overview (ADR-037), and nothing here derives one.</b>
 * It used to read the subject list and count it in the browser, which was honest while the list was
 * one small request and would have silently under-counted the day the list was paginated. The
 * registry now answers with a bounded number of aggregate queries, so a subject count and a
 * version count mean the same thing at ten subjects and at ten thousand. A count added here that
 * was computed from rows instead would put that defect back, and is the first thing to refuse in
 * review.
 */
@Component({
  selector: 'cd-dashboard-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [DashboardStore],
  imports: [
    RouterLink,
    HlmAlertImports,
    HlmButton,
    HlmSkeleton,
    Icon,
    IfScope,
    StatCard,
    SubjectCard,
    BreakingChangesPanel,
    EnforcementPanel,
  ],
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
        <div class="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-4">
          <cd-stat-card
            title="Subjects"
            [value]="subjectCount()"
            [subtitle]="subjectsSubtitle()"
            icon="file-json"
            route="/subjects"
          />
          <cd-stat-card
            title="Versions"
            [value]="versionCount()"
            subtitle="Registered across all subjects"
            icon="git-branch"
          />
          <cd-stat-card
            title="Awaiting approval"
            [value]="pendingCount()"
            [subtitle]="pendingSubtitle()"
            icon="file-check"
            route="/approvals"
          />
          <cd-stat-card
            title="Violations"
            [value]="violationCount()"
            [subtitle]="violationsSubtitle()"
            icon="triangle-alert"
          />
        </div>

        @if (store.summary(); as summary) {
          <div class="grid grid-cols-1 gap-4 lg:grid-cols-2">
            <cd-breaking-changes-panel [changes]="summary.breakingChanges" />
            <cd-enforcement-panel [summary]="summary" />
          </div>
        } @else if (store.loading()) {
          <div class="grid grid-cols-1 gap-4 lg:grid-cols-2" aria-busy="true">
            <div hlmSkeleton class="h-56 w-full rounded-xl"></div>
            <div hlmSkeleton class="h-56 w-full rounded-xl"></div>
          </div>
        }

        <section>
          <div class="mb-4 flex items-center justify-between">
            <h2 class="text-foreground text-lg font-semibold">Newest subjects</h2>
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
              <!--
                What an empty registry is for, one click away. Absent for a caller who cannot
                write (ADR-018), who is told nothing it cannot act on: the sentence above is
                still true for them.
              -->
              <a
                *cdIfScope="schemaWriteScopes"
                hlmBtn
                class="glow-primary mt-4 gap-2"
                routerLink="/subjects/new-subject"
              >
                <cd-icon name="plus" size="1rem" />
                Create the first subject
              </a>
            </div>
          } @else {
            <div class="grid grid-cols-1 gap-4 md:grid-cols-2 lg:grid-cols-3">
              @for (subject of newest(); track subject.name) {
                <!--
                  The anchor is here rather than inside the card, so ui/ stays router-free.
                  The group class is what drives the card's hover treatment, and
                  focus-visible sits on the anchor because that is the element taking focus.
                -->
                <a
                  class="group focus-visible:ring-ring rounded-xl focus-visible:ring-2 focus-visible:ring-offset-2 focus-visible:outline-none"
                  [routerLink]="['/subjects', subject.name]"
                >
                  <cd-subject-card [subject]="subject" />
                </a>
              }
            </div>
          }
        </section>
      }
    </section>
  `,
})
export class DashboardPage {
  protected readonly store = inject(DashboardStore);
  protected readonly environments = inject(ActiveEnvironmentStore);

  /** Enough cards to fill the grid a short list would occupy, so the layout does not jump. */
  protected readonly skeletonCards = [0, 1, 2];

  /** The scopes that permit creating a subject, from the one list (ADR-018). */
  protected readonly schemaWriteScopes = SCHEMA_WRITE_SCOPES;

  /**
   * An em dash until the registry has answered, so a number that has not arrived is never a
   * zero that looks like an answer: "0 awaiting approval" on a screen that is still loading
   * says nothing is waiting, which is the one claim it cannot yet make.
   */
  private readonly notYet = '—';

  protected readonly subjectCount = computed(
    () => this.store.summary()?.subjects.total ?? this.notYet,
  );

  protected readonly versionCount = computed(
    () => this.store.summary()?.versions.total ?? this.notYet,
  );

  protected readonly pendingCount = computed(
    () => this.store.summary()?.versions.awaitingApproval ?? this.notYet,
  );

  protected readonly violationCount = computed(
    () => this.store.summary()?.violations.distinct ?? this.notYet,
  );

  /**
   * "Message contracts", plus what is not ordinary about the count.
   *
   * Retired subjects are in the total (the subject list lists them), so a total that quietly
   * included three of them would disagree with the list's active rows with nothing to say why.
   */
  protected readonly subjectsSubtitle = computed(() => {
    const subjects = this.store.summary()?.subjects;
    const notes = [
      subjects && subjects.deprecated > 0 ? `${subjects.deprecated} deprecated` : null,
      subjects && subjects.retired > 0 ? `${subjects.retired} retired` : null,
    ].filter((note) => note !== null);

    return notes.length === 0 ? 'Message contracts' : notes.join(' · ');
  });

  /**
   * Says nothing is waiting rather than leaving a bare zero to be interpreted.
   *
   * A zero on an approvals counter is ambiguous in a way the other counters are not: it reads
   * equally as "nothing needs approving" and "approvals aren't switched on".
   */
  protected readonly pendingSubtitle = computed(() =>
    this.store.summary()?.versions.awaitingApproval === 0
      ? 'Nothing waiting'
      : 'Held at the approval gate',
  );

  /**
   * What was reported, in the registry's words and no stronger.
   *
   * "None" would claim an absence only an SDK could observe; the card says what the registry was
   * told and over what window, so a zero reads as "no report" and not as "all clear".
   */
  protected readonly violationsSubtitle = computed(() => {
    const violations = this.store.summary()?.violations;

    if (violations === undefined) {
      return 'Reported by SDKs';
    }

    return violations.recent === 0
      ? `None reported in the last ${violations.windowHours} hours`
      : `${violations.recent} seen in the last ${violations.windowHours} hours`;
  });

  protected readonly newest = computed(() => this.store.summary()?.newestSubjects ?? []);

  constructor() {
    // Reloads on an environment switch as well as on entry. Reading the signal inside the
    // effect is what registers the dependency — an `ngOnInit` call would load once and then
    // silently show the previous environment's numbers after a switch.
    effect(() => {
      this.environments.name();
      this.store.load();
    });
  }
}
