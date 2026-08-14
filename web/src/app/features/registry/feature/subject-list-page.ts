import { ChangeDetectionStrategy, Component, effect, inject } from '@angular/core';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmCardImports } from '@spartan-ng/helm/card';
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
import { IfScope } from '../../../core/auth/if-scope';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import { SCHEMA_WRITE_SCOPES } from '../../../domain/identity/scope';
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
 * The "New subject" affordance is wrapped in `*cdIfScope` and is **absent** for a non-admin
 * rather than disabled (ADR-018). A disabled button invites a support ticket; an absent one
 * does not raise the question.
 */
@Component({
  selector: 'cd-subject-list-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [SubjectListStore],
  imports: [HlmCardImports, HlmAlertImports, HlmButton, HlmSkeleton, IfScope, SubjectTable],
  template: `
    <section class="mx-auto w-full max-w-6xl space-y-4 p-6">
      <header class="flex items-start justify-between gap-4">
        <div class="space-y-1">
          <h1 class="text-2xl font-semibold tracking-tight">Subjects</h1>
          <p class="text-muted-foreground text-sm">
            Environment <span class="font-mono">{{ environments.name() }}</span>
          </p>
        </div>

        <!--
          Absent for a non-admin rather than disabled (ADR-018). A disabled button invites a
          support ticket asking to be given the permission; an absent one does not raise the
          question. The server refuses the same request with 403 either way.
        -->
        <button *cdIfScope="schemaWriteScopes" hlmBtn size="sm">New subject</button>
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

  /** The scopes that permit creating a subject, from the one list (ADR-018). */
  protected readonly schemaWriteScopes = SCHEMA_WRITE_SCOPES;

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
