import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
import { IfScope } from '../../../core/auth/if-scope';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import { SCHEMA_WRITE_SCOPES } from '../../../domain/identity/scope';
import {
  SUBJECT_NAME_MAX_LENGTH,
  checkSubjectNamePrefix,
} from '../../../domain/registry/subject-name';
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
 * does not raise the question. It is a link and not a button: it goes to the create screen,
 * which until 2026-10-13 did not exist, so this control sat here for a writer to press and did
 * nothing at all. The empty list carries the same action, because "nothing is wrong, there is
 * simply nothing here" is a sentence that wants its next step beside it.
 *
 * **The search box is the registry's prefix filter** (ADR-038), not a filter over what is on
 * screen. It used to match anywhere in a name or its owner, ignoring case, over the list already
 * in memory, and said it would have to move to the server the day the list was paged; the
 * registry now answers it, ordinal and case-sensitive like the names themselves. Two
 * consequences are deliberate: what it finds is what the registry finds (so it cannot go wrong
 * when the list grows), and it no longer matches on the owner or in the middle of a name — both
 * are things this screen said it did and no longer says.
 */
@Component({
  selector: 'cd-subject-list-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [SubjectListStore],
  imports: [HlmAlertImports, HlmButton, HlmSkeleton, Icon, IfScope, RouterLink, SubjectTable],
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
        <a
          *cdIfScope="schemaWriteScopes"
          hlmBtn
          class="glow-primary gap-2"
          routerLink="/subjects/new-subject"
        >
          <cd-icon name="plus" size="1rem" />
          New subject
        </a>
      </header>

      <!--
        Outside the branches below, and that is the point. The box is typed into: a search
        replaces the rows, and a box that lived in the same branch as the rows (or as the empty
        state a search that found nothing produces) would be unmounted by its own answer and take
        the cursor with it. It is absent only where there is nothing to search: before the first
        answer, and in an environment that has no subjects at all.
      -->
      @if (showSearch()) {
        <div class="max-w-md">
          <div class="relative">
            <span
              class="text-muted-foreground pointer-events-none absolute inset-y-0 left-3 flex items-center"
            >
              <cd-icon name="search" size="1rem" />
            </span>
            <input
              type="search"
              [value]="query()"
              (input)="onInput($event)"
              placeholder="Name starts with…"
              aria-label="Search subjects by the start of their name"
              autocomplete="off"
              spellcheck="false"
              [attr.maxlength]="maxLength"
              class="border-input bg-background focus-visible:ring-ring h-9 w-full rounded-md border ps-10 pe-3 font-mono text-sm focus-visible:ring-1 focus-visible:outline-none"
            />
          </div>
          <!--
            Said where it is read, because the box looks like a search and behaves like a prefix:
            somebody who types the middle of a name and gets nothing needs to know why before
            they suspect the registry has lost it.
          -->
          <p class="text-muted-foreground mt-1.5 text-xs" aria-live="polite">
            @if (store.shown() === '') {
              Matches the start of a name, and the case you type.
            } @else if (!store.noMatches() && !store.error()) {
              {{ count() }} starting with
              <span class="text-foreground font-mono break-all">{{ store.shown() }}</span>
            }
          </p>
        </div>
      }

      @if (store.error(); as error) {
        <div hlmAlert variant="destructive">
          <h2 hlmAlertTitle>Could not load subjects</h2>
          <p hlmAlertDescription>{{ error.detail }}</p>
          <button hlmBtn hlmAlertAction variant="outline" size="sm" (click)="store.load()">
            Try again
          </button>
        </div>
      } @else if (store.loading() && !store.searching()) {
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
          <!-- Absent for a caller who cannot write, like the header's: ADR-018. -->
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
      } @else if (store.noMatches()) {
        <!-- A status, so the empty answer is announced: the caption under the box is silent here. -->
        <div role="status" class="border-border bg-card rounded-xl border p-10 text-center">
          <p class="font-medium">
            No subject names start with
            <span class="font-mono break-all">“{{ store.shown() }}”</span>
          </p>
          <p class="text-muted-foreground mt-1 text-sm">
            @if (impossibleReason(); as reason) {
              {{ reason }}
            } @else {
              Names are case-sensitive, so <span class="font-mono">Order</span> does not find
              <span class="font-mono">order</span>, and the search matches the start of a name — not
              the owner, and not the middle.
            }
          </p>
          <button hlmBtn variant="outline" size="sm" class="mt-4" (click)="clear()">
            Clear search
          </button>
        </div>
      } @else {
        <!--
          Kept on screen while the next answer is awaited, and dimmed: replacing it with a
          skeleton at every pause in typing would flash the table, and the rows that are there are
          the best answer available until the right ones arrive.
        -->
        <div
          [attr.aria-busy]="store.searching()"
          [class]="store.searching() ? 'opacity-60 transition-opacity' : 'transition-opacity'"
        >
          <cd-subject-table [subjects]="store.subjects()" />
        </div>
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

  /**
   * No name is longer, so no prefix is: a box that took more would send a request line the
   * server may refuse outright (414 past about 8 KB) for an answer that is always an empty list.
   */
  protected readonly maxLength = SUBJECT_NAME_MAX_LENGTH;

  /** What the box holds, exactly as typed. The store trims it; the box is not reformatted. */
  protected readonly query = signal('');

  /**
   * Whether there is anything to search. The box is there once an answer has come and the
   * environment has subjects — or has been searched, so that a search which found nothing (and
   * an environment that, as far as one prefix can tell, is empty) never takes the box away from
   * the person who needs it to type something else.
   */
  protected readonly showSearch = computed(() => this.store.loaded() && !this.store.isEmpty());

  /** "3 subjects", for the line under the box. */
  protected readonly count = computed(() => {
    const n = this.store.subjects().length;

    return n === 1 ? '1 subject' : `${n} subjects`;
  });

  /**
   * Why the prefix that matched nothing could never have matched, or null when it could have
   * (and simply did not). Only ever words an empty answer: the search was sent regardless.
   */
  protected readonly impossibleReason = computed(() => {
    const check = checkSubjectNamePrefix(this.store.shown());

    return check.possible ? null : check.message;
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

  protected onInput(event: Event): void {
    const text = (event.target as HTMLInputElement).value;

    this.query.set(text);
    this.store.search(text);
  }

  protected clear(): void {
    this.query.set('');
    this.store.search('');
  }
}
