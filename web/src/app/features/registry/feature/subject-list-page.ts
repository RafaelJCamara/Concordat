import { DecimalPipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
import { Subject as Typed, debounceTime } from 'rxjs';
import { IfScope } from '../../../core/auth/if-scope';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import { SCHEMA_WRITE_SCOPES } from '../../../domain/identity/scope';
import { trimRegistryText } from '../../../domain/registry/registry-text';
import {
  SUBJECT_NAME_MAX_LENGTH,
  checkSubjectNamePrefix,
} from '../../../domain/registry/subject-name';
import { Icon } from '../../../shared/ui/icon/icon';
import { Pagination } from '../../../shared/ui/pagination/pagination';
import { SEARCH_DEBOUNCE_MS, SubjectListStore } from '../application/subject-list-store';
import {
  PAGE_SIZES,
  isCanonical,
  pageAfterResize,
  parseSubjectListQuery,
  toQueryParams,
  type ParamSource,
  type SubjectListQuery,
} from '../application/subject-list-query';
import { SubjectTable } from '../ui/subject-table';

/** A query parameter as the router binds it: text, a list of text when the address repeats it, or absent. */
type RoutedParam = string | readonly string[] | undefined;

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
 *
 * **The address is the screen's state** (ADR-052). The prefix, the page and the size are the query
 * parameters `q`, `page` and `size`, and this component only ever does two things with them: reads
 * them (the router binds them to the inputs below) and changes them (a click, a pause in typing). It
 * never tells the store "go to page 3" directly. A click is a navigation, the navigation changes the
 * inputs, the inputs reach the store; so a click, a reload, a link somebody pasted and the Back button
 * are the same path, and cannot disagree about which page is showing.
 */
@Component({
  selector: 'cd-subject-list-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [SubjectListStore],
  imports: [
    DecimalPipe,
    HlmAlertImports,
    HlmButton,
    HlmSkeleton,
    Icon,
    IfScope,
    Pagination,
    RouterLink,
    SubjectTable,
  ],
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
              [value]="text()"
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
            they suspect the registry has lost it. Always the same sentence: how many matched, and for
            which prefix, is said beside the table, with the page.
          -->
          <p class="text-muted-foreground mt-1.5 text-xs">
            Matches the start of a name, and the case you type.
          </p>
        </div>
      }

      @if (store.error(); as error) {
        <div hlmAlert variant="destructive">
          <h2 hlmAlertTitle>Could not load subjects</h2>
          <p hlmAlertDescription>{{ error.detail }}</p>
          <button hlmBtn hlmAlertAction variant="outline" size="sm" (click)="store.reload()">
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
          Where the rows are and how many there are, said once and where it is read, above the table
          because that is the part of the screen on the first screen: "Page 3 of 12 · subjects 101–150 of
          5,000" is the first thing a person with a long list asks. A polite status: the sentence is
          rewritten when an answer lands, which is what a screen reader reads out, and it is the only
          announcement a page change makes (the focus stays where the person put it, on the pager).
        -->
        <div class="flex flex-col gap-3 sm:flex-row sm:items-end sm:justify-between">
          <div class="space-y-1">
            <p
              class="text-muted-foreground text-sm"
              role="status"
              aria-live="polite"
              data-testid="list-extent"
            >
              @if (store.pages(); as pages) {
                Page {{ store.shownPage() | number }} of {{ pages | number }} · subjects
                {{ store.firstRow() | number }}–{{ store.lastRow() | number }} of
                {{ store.total() | number }}
              } @else {
                Page {{ store.shownPage() | number }} · subjects {{ store.firstRow() | number }}–{{
                  store.lastRow() | number
                }}
                @if (store.hasMore()) {
                  · more may follow
                } @else {
                  · the end of the list
                }
              }
              @if (store.shown() !== '') {
                starting with
                <span class="text-foreground font-mono break-all">{{ store.shown() }}</span>
              }
              · newest first
            </p>

            @if (store.clampedFrom(); as from) {
              <!--
                Said, because the address asked for a page the list does not have, and the table now shows
                a different one. A link that went stale (the list shrank, or the address was edited) lands
                on the nearest page that exists and does not leave a blank table.
              -->
              <p class="text-warning text-sm" role="status" data-testid="page-clamped">
                Page {{ from | number }} does not exist, so this is page
                {{ store.shownPage() | number }}, the last.
              </p>
            }
          </div>

          <label class="text-muted-foreground flex items-center gap-2 text-sm">
            Subjects per page
            <select
              class="border-input bg-background focus-visible:ring-ring h-8 rounded-md border px-2 text-sm focus-visible:ring-1 focus-visible:outline-none"
              data-testid="page-size"
              (change)="onSize($event)"
            >
              @for (n of pageSizes; track n) {
                <option [value]="n" [selected]="n === store.size()">{{ n }}</option>
              }
            </select>
          </label>
        </div>

        <!--
          Kept on screen while the next answer is awaited, and dimmed: replacing it with a
          skeleton at every page and pause in typing would flash the table, and the rows that are
          there are the best answer available until the right ones arrive.
        -->
        <div
          [attr.aria-busy]="store.searching()"
          [class]="store.searching() ? 'opacity-60 transition-opacity' : 'transition-opacity'"
        >
          <cd-subject-table [subjects]="store.subjects()" />
        </div>

        @if (showPager()) {
          <cd-pagination
            label="Subject pages"
            [page]="store.page()"
            [pageCount]="store.pages()"
            [hasNext]="store.hasMore()"
            (pageChange)="goToPage($event)"
          />
        }
      }
    </section>
  `,
})
export class SubjectListPage {
  protected readonly store = inject(SubjectListStore);
  protected readonly environments = inject(ActiveEnvironmentStore);

  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  /**
   * The address's three parameters, which the router binds here by name (`withComponentInputBinding`).
   * Text, because that is what an address holds; {@link requested} is what they mean.
   */
  readonly q = input<RoutedParam>();
  readonly page = input<RoutedParam>();
  readonly size = input<RoutedParam>();

  /** Enough rows to fill the space a short list would occupy, so the layout does not jump. */
  protected readonly skeletonRows = [0, 1, 2, 3, 4];

  /** The scopes that permit creating a subject, from the one list (ADR-018). */
  protected readonly schemaWriteScopes = SCHEMA_WRITE_SCOPES;

  /** The sizes the person may choose between (ADR-052). */
  protected readonly pageSizes = PAGE_SIZES;

  /**
   * No name is longer, so no prefix is: a box that took more would send a request line the
   * server may refuse outright (414 past about 8 KB) for an answer that is always an empty list.
   */
  protected readonly maxLength = SUBJECT_NAME_MAX_LENGTH;

  /** What the box holds, exactly as typed. The address gets it trimmed; the box is not reformatted. */
  protected readonly text = signal('');

  /** The address, as something with a `get`, which is all the query module asks of it. */
  private readonly params = computed<ParamSource>(() => {
    const values = { q: this.q(), page: this.page(), size: this.size() };

    return {
      get: (name) => {
        const value = (values as Record<string, RoutedParam>)[name];

        return Array.isArray(value) ? (value[0] ?? null) : ((value as string | undefined) ?? null);
      },
    };
  });

  /** What the address asks for, believing as little of it as possible. */
  protected readonly requested = computed<SubjectListQuery>(() =>
    parseSubjectListQuery(this.params()),
  );

  /**
   * Whether there is anything to search. The box is there once an answer has come and the
   * environment has subjects — or has been searched, so that a search which found nothing (and
   * an environment that, as far as one prefix can tell, is empty) never takes the box away from
   * the person who needs it to type something else.
   */
  protected readonly showSearch = computed(() => this.store.loaded() && !this.store.isEmpty());

  /**
   * Whether to draw the page buttons: more than one page, or, when the registry sent no count, a page
   * that is not the first or that was full. One page of results has nothing to turn to.
   */
  protected readonly showPager = computed(() => {
    const pages = this.store.pages();

    return pages === null ? this.store.page() > 1 || this.store.hasMore() : pages > 1;
  });

  /**
   * Why the prefix that matched nothing could never have matched, or null when it could have
   * (and simply did not). Only ever words an empty answer: the search was sent regardless.
   */
  protected readonly impossibleReason = computed(() => {
    const check = checkSubjectNamePrefix(this.store.shown());

    return check.possible ? null : check.message;
  });

  /** The text of the box, after each keystroke, until typing pauses (see the constructor). */
  private readonly typed = new Typed<string>();

  /** The environment the last answer was asked of: a change means the page number means nothing any more. */
  private lastEnvironment: string | null = null;

  /** Whether the next ask starts from a clean slate, because the rows on screen belong to another environment. */
  private fresh = false;

  constructor() {
    // The address, and the environment, to the store. One effect for both because they are one question:
    // which subjects, of which environment. Reading both inside it is what registers the dependency —
    // an `ngOnInit` call would load once and then silently show the previous environment's subjects
    // after a switch.
    effect(() => {
      const environment = this.environments.name();
      const wanted = this.requested();
      const params = this.params();

      untracked(() => {
        // An address that says what it means in a way the screen would not have written (page 0, a size
        // nobody offered, a page 1 spelled out, a prefix with spaces round it) is rewritten in place, and
        // the screen waits for the rewritten one: two addresses for one list would be two history
        // entries for the same screen.
        if (!isCanonical(params)) {
          this.navigate(wanted, true);
          return;
        }

        // A page number belongs to the environment it was read from: page 7 of `prod` is not a place in
        // `dev`, which may have two pages. Switching environment lands on the first page.
        const switched = this.lastEnvironment !== null && this.lastEnvironment !== environment;
        this.lastEnvironment = environment;
        this.fresh ||= switched;

        if (switched && wanted.page > 1) {
          this.navigate({ ...wanted, page: 1 }, true);
          return;
        }

        // The box follows the address (Back, Forward, a pasted link), but is not rewritten while the person
        // is typing something that trims to the same prefix: the address got it from the box.
        if (trimRegistryText(this.text()) !== wanted.prefix) {
          this.text.set(wanted.prefix);
        }

        this.store.show(wanted, { fresh: this.fresh });
        this.fresh = false;
      });
    });

    // The registry said the page asked for is past the end: go to the last one, in place of the address that
    // named a page that is not there, so Back does not return to it.
    effect(() => {
      const range = this.store.outOfRange();

      if (range !== null) {
        untracked(() => this.navigate({ ...this.requested(), page: range.last }, true));
      }
    });

    // Typing is waited out here, before the address changes, and not in the store: the address is what
    // asks the registry, so a pause that has not ended asks nothing. One request per pause, as before.
    this.typed
      .pipe(debounceTime(SEARCH_DEBOUNCE_MS), takeUntilDestroyed(inject(DestroyRef)))
      .subscribe((text) => this.search(text));
  }

  protected onInput(event: Event): void {
    const text = (event.target as HTMLInputElement).value;

    this.text.set(text);
    this.typed.next(text);
  }

  protected clear(): void {
    this.text.set('');
    // Straight away, and through the same channel so that a pause still running is superseded and does not
    // put the old text back.
    this.typed.next('');
    this.search('');
  }

  protected goToPage(page: number): void {
    this.navigate({ ...this.requested(), page }, false);
  }

  protected onSize(event: Event): void {
    const size = Number((event.target as HTMLSelectElement).value);
    const current = this.requested();

    if (!PAGE_SIZES.includes(size) || size === current.size) {
      return;
    }

    this.navigate(
      { ...current, size, page: pageAfterResize(current.page, current.size, size) },
      false,
    );
  }

  /**
   * A new prefix is the first page of it. Replaces the entry in the history and does not add one: a
   * search is refined letter by letter, and Back should leave the list and not walk back through
   * every prefix typed on the way.
   */
  private search(text: string): void {
    const prefix = trimRegistryText(text);
    const current = this.requested();

    if (prefix !== current.prefix) {
      this.navigate({ ...current, prefix, page: 1 }, true);
    }
  }

  /**
   * Changes the address, keeping anything else in it. Only the three parameters are written, and the ones
   * at their defaults are removed, so the plain list has the plain address.
   */
  private navigate(query: SubjectListQuery, replace: boolean): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: toQueryParams(query),
      queryParamsHandling: 'merge',
      replaceUrl: replace,
    });
  }
}
