import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
import { IfScope } from '../../../core/auth/if-scope';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import { SCHEMA_WRITE_SCOPES } from '../../../domain/identity/scope';
import { Icon } from '../../../shared/ui/icon/icon';
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
  imports: [HlmAlertImports, HlmButton, HlmSkeleton, Icon, IfScope, SubjectTable],
  template: `
    <section class="mx-auto w-full max-w-6xl space-y-6 p-6 lg:p-8">
      <header class="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
        <div>
          <h1 class="text-foreground text-2xl font-bold tracking-tight">Subjects</h1>
          <p class="text-muted-foreground mt-1">
            Message contracts in
            <span class="font-mono">{{ environments.name() }}</span>
          </p>
        </div>

        <!--
          Absent for a non-admin rather than disabled (ADR-018). A disabled button invites a
          support ticket asking to be given the permission; an absent one does not raise the
          question. The server refuses the same request with 403 either way.
        -->
        <button *cdIfScope="schemaWriteScopes" hlmBtn class="glow-primary gap-2">
          <cd-icon name="plus" size="1rem" />
          New subject
        </button>
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
        <div class="border-border bg-card rounded-xl border p-10 text-center">
          <p class="font-medium">No subjects yet</p>
          <p class="text-muted-foreground mt-1 text-sm">
            A subject has to exist before a version can be registered, so nothing is wrong — there
            is simply nothing here yet.
          </p>
        </div>
      } @else {
        <!--
          Filtering is client-side over the list already in memory, which is right while the
          whole list is one request and wrong the moment it is paginated. Whoever adds
          paging has to move this to the server, or the box will quietly search one page.
        -->
        <div class="relative max-w-md">
          <span
            class="text-muted-foreground pointer-events-none absolute inset-y-0 left-3 flex items-center"
          >
            <cd-icon name="search" size="1rem" />
          </span>
          <input
            type="search"
            [value]="query()"
            (input)="query.set(inputValue($event))"
            placeholder="Search subjects…"
            aria-label="Search subjects"
            class="border-input bg-background focus-visible:ring-ring h-9 w-full rounded-md border ps-10 pe-3 text-sm focus-visible:ring-1 focus-visible:outline-none"
          />
        </div>

        @if (matches().length === 0) {
          <div class="border-border bg-card rounded-xl border p-10 text-center">
            <p class="font-medium">No subjects match “{{ query() }}”</p>
            <p class="text-muted-foreground mt-1 text-sm">
              Filtering matches on the subject name and its owner.
            </p>
          </div>
        } @else {
          <cd-subject-table [subjects]="matches()" />
        }
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

  protected readonly query = signal('');

  /**
   * Subjects matching the search box, on name or owner.
   *
   * Case-insensitive and substring rather than prefix, because subject names are dotted
   * paths — someone looking for `acme.orders.OrderCreated` types "ordercreated" far more
   * often than they type the namespace it lives under.
   */
  protected readonly matches = computed(() => {
    const query = this.query().trim().toLowerCase();

    if (query === '') {
      return this.store.subjects();
    }

    return this.store
      .subjects()
      .filter(
        (subject) =>
          subject.name.toLowerCase().includes(query) || subject.owner.toLowerCase().includes(query),
      );
  });

  constructor() {
    // Reloads on an environment switch as well as on entry. Reading the signal inside the
    // effect is what registers the dependency — an `ngOnInit` call would load once and then
    // silently show the previous environment's subjects after a switch.
    effect(() => {
      this.environments.name();
      this.store.load();
    });
  }

  protected inputValue(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }
}
