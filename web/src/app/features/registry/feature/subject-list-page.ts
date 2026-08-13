import { ChangeDetectionStrategy, Component, effect, inject } from '@angular/core';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmCardImports } from '@spartan-ng/helm/card';
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import { SubjectListStore } from '../application/subject-list-store';
import { SubjectTable } from '../ui/subject-table';

/**
 * The subject list screen.
 *
 * The routed, smart half of the feature: it talks to its own store and to nothing else.
 * There is no `SubjectsApi` import here, and there should never be one — that is the rule
 * the boundaries lint enforces, and the reason is that a screen which can reach the API
 * directly will, the first time a store method looks like one call too many.
 *
 * ADR-018 note for M4.2: the "New subject" affordance belongs here, wrapped in
 * `*cdIfScope`, and must be **absent** for a non-admin rather than disabled. A disabled
 * button invites a support ticket; an absent one does not.
 */
@Component({
  selector: 'cd-subject-list-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [SubjectListStore],
  imports: [HlmCardImports, HlmAlertImports, HlmButton, HlmSkeleton, SubjectTable],
  template: `
    <section class="mx-auto w-full max-w-6xl space-y-4 p-6">
      <header class="space-y-1">
        <h1 class="text-2xl font-semibold tracking-tight">Subjects</h1>
        <p class="text-muted-foreground text-sm">
          Environment <span class="font-mono">{{ environments.name() }}</span>
        </p>
      </header>

      @if (store.error(); as error) {
        <div hlmAlert variant="destructive">
          <h2 hlmAlertTitle>Could not load subjects</h2>
          <p hlmAlertDescription>{{ error.detail }}</p>
          <button hlmBtn hlmAlertAction variant="outline" size="sm" (click)="store.load()">
            Try again
          </button>
        </div>
      } @else if (store.loading()) {
        <div class="space-y-2" aria-busy="true" aria-live="polite">
          @for (row of skeletonRows; track row) {
            <div hlmSkeleton class="h-10 w-full"></div>
          }
        </div>
      } @else if (store.isEmpty()) {
        <div hlmCard class="p-10 text-center">
          <p class="font-medium">No subjects yet</p>
          <p class="text-muted-foreground mt-1 text-sm">
            A subject has to exist before a version can be registered, so nothing is wrong — there
            is simply nothing here yet.
          </p>
        </div>
      } @else {
        <cd-subject-table [subjects]="store.subjects()" />
      }
    </section>
  `,
})
export class SubjectListPage {
  protected readonly store = inject(SubjectListStore);
  protected readonly environments = inject(ActiveEnvironmentStore);

  /** Enough rows to fill the space a short list would occupy, so the layout does not jump. */
  protected readonly skeletonRows = [0, 1, 2, 3, 4];

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
