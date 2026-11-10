import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
  type WritableSignal,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
import { IfScope } from '../../../core/auth/if-scope';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import { APPROVAL_SCOPES } from '../../../domain/identity/scope';
import { RelativeTimePipe } from '../../../shared/pipes/relative-time-pipe';
import { Icon } from '../../../shared/ui/icon/icon';
import { StatusBadge } from '../../../shared/ui/status-badge/status-badge';
import {
  keyOf,
  PendingApprovalsStore,
  type Decision,
} from '../application/pending-approvals-store';
import { PendingReview } from './pending-review';

/**
 * Every version waiting on a decision, and where it is decided.
 *
 * <b>Anyone may read the queue; only an admin may act on it.</b> DESIGN §9 has non-admins see
 * the pending diff and impact "without approve/reject controls", and ADR-018's rule applies:
 * the buttons are absent, not disabled — a disabled one invites a request for a permission the
 * reader was never meant to have. The server refuses the decision either way.
 *
 * <b>Deciding takes two clicks.</b> Approving moves `latest` for every consumer of the subject
 * and rejecting is final for that version, and neither can be taken back from this screen. The
 * confirmation names the version and says what it does, which is more than a dialog's "Are you
 * sure?" and costs no more than one.
 *
 * <b>The outcome is announced, not just shown.</b> A decided version leaves the queue, and a
 * row that vanishes without a word reads as the page having lost it. The `role="status"` line
 * says what happened to it, and is what a screen reader hears.
 *
 * <b>The address can narrow the queue to one subject, and point at one of its versions</b>
 * (`?subject=orders.created&version=2`, ADR-052). That is where the subjects list sends the pill that
 * says a version is awaiting approval. The registry does the narrowing (ADR-049), so the count beside the
 * list is the subject's own; the page says it is filtered and how to undo it (`Show all`), says so
 * plainly when the subject has nothing held, and opens the review of the version named and brings it
 * into view. Neither parameter is an identity (DESIGN section 9 keeps those in the path): they are a
 * filter and a place to land, and the unfiltered queue works as it always did.
 */
@Component({
  selector: 'cd-approvals-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [PendingApprovalsStore],
  imports: [
    RouterLink,
    HlmAlertImports,
    HlmButton,
    HlmSkeleton,
    IfScope,
    Icon,
    PendingReview,
    RelativeTimePipe,
    StatusBadge,
  ],
  template: `
    <section class="mx-auto w-full max-w-6xl space-y-6 p-6 lg:p-8">
      <header>
        <h1 class="text-foreground text-2xl font-bold tracking-tight">Approvals</h1>
        <p class="text-muted-foreground mt-1">
          Breaking changes waiting on a decision in
          <span class="font-mono">{{ environments.name() }}</span>
        </p>
      </header>

      @if (filter(); as name) {
        <!--
          Said, and undoable in one step. A list that is quietly narrowed to one subject would look like
          the whole queue being short, and the person who followed a link here would not know there is
          more. The name is shown whole and can break anywhere: it is a dotted identifier with no spaces.
        -->
        <div
          role="status"
          class="border-border bg-card flex flex-wrap items-center gap-x-4 gap-y-2 rounded-xl border px-4 py-3"
          data-testid="approvals-filter"
        >
          <p class="text-sm">
            Showing changes waiting on <span class="font-mono break-all">{{ name }}</span>
          </p>
          <a hlmBtn variant="outline" size="sm" routerLink="/approvals">Show all</a>
        </div>
      }

      <div role="status" aria-live="polite">
        @if (store.lastDecided(); as decided) {
          <div hlmAlert>
            <p hlmAlertDescription>
              {{ decided.action === 'approve' ? 'Approved' : 'Rejected' }}
              <span class="font-mono">{{ decided.subject }} v{{ decided.ordinal }}</span
              >.
              @if (decided.action === 'approve') {
                It is now what consumers resolve as latest.
              }
            </p>
          </div>
        }
      </div>

      @if (store.refreshFailed()) {
        <div hlmAlert variant="destructive">
          <h2 hlmAlertTitle>The queue could not be refreshed</h2>
          <p hlmAlertDescription>
            What is shown was read before your last decision and may be out of date.
          </p>
          <button hlmBtn hlmAlertAction variant="outline" size="sm" (click)="store.refresh()">
            Try again
          </button>
        </div>
      }

      @if (store.loading() && !store.loaded()) {
        <div class="space-y-3" aria-busy="true" aria-live="polite">
          <div hlmSkeleton class="h-28 w-full rounded-xl"></div>
          <div hlmSkeleton class="h-28 w-full rounded-xl"></div>
        </div>
      } @else if (store.error(); as error) {
        <div hlmAlert variant="destructive">
          <h2 hlmAlertTitle>Could not load the queue</h2>
          <p hlmAlertDescription>{{ error.detail }}</p>
          <button hlmBtn hlmAlertAction variant="outline" size="sm" (click)="store.load(filter())">
            Try again
          </button>
        </div>
      } @else if (store.isEmpty() && filter() !== null) {
        <!--
          Not the same sentence as the empty queue, because the cause is not the same and neither is the next
          step: the link that brought the reader here was true when it was made, and a held version can be
          decided by somebody else in the meantime. Honest about that, and a way to the rest.
        -->
        <div
          class="border-border bg-card rounded-xl border p-10 text-center"
          data-testid="approvals-filter-empty"
        >
          <p class="font-medium">
            Nothing is waiting on a decision for
            <span class="font-mono break-all">{{ filter() }}</span> — it may already have been
            decided
          </p>
          <a hlmBtn variant="outline" size="sm" class="mt-4" routerLink="/approvals">Show all</a>
        </div>
      } @else if (store.isEmpty()) {
        <div class="border-border bg-card rounded-xl border p-10 text-center">
          <p class="font-medium">Nothing is waiting on a decision</p>
          <p class="text-muted-foreground mx-auto mt-1 max-w-xl text-sm">
            A breaking change registered against a subject lands here instead of becoming its latest
            version, until an admin approves or rejects it.
          </p>
        </div>
      } @else {
        @if (absent(); as gone) {
          <!--
            The link named a version that is not in this list: decided since the page it came from was
            read, or never held. Said, and not silent, because a link that lands and does nothing reads as
            broken. The versions that are held are right below.
          -->
          <p
            class="text-muted-foreground text-sm"
            role="status"
            data-testid="approvals-target-gone"
          >
            <span class="font-mono">{{ gone.subject }} v{{ gone.ordinal }}</span> is not waiting on
            a decision any more; it may already have been decided.
          </p>
        }
        <ul class="space-y-4">
          @for (item of store.pending(); track key(item.subject, item.ordinal)) {
            @let id = key(item.subject, item.ordinal);
            @let state = store.decisions()[id];
            <li class="border-border bg-card rounded-xl border p-5">
              <div class="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
                <div class="min-w-0">
                  <!--
                    Focusable by script and not by Tab: the heading of the version a link pointed at is where
                    the focus goes, so the reader is at the start of what they came to review, and Tab goes on
                    from there to its controls. No trap: nothing here holds the focus.
                  -->
                  <h2
                    class="text-foreground focus-visible:ring-ring rounded-sm font-mono text-lg font-semibold break-all outline-none focus-visible:ring-2"
                    tabindex="-1"
                    [attr.data-pending-key]="id"
                  >
                    <a
                      class="hover:text-primary transition-colors"
                      [routerLink]="['/subjects', item.subject]"
                      >{{ item.subject }}</a
                    >
                    <a
                      class="hover:text-primary ms-1 transition-colors"
                      [routerLink]="['/subjects', item.subject, 'versions', item.ordinal]"
                      >v{{ item.ordinal }}</a
                    >
                    @if (item.semanticVersion) {
                      <span class="text-muted-foreground text-sm font-normal">
                        · {{ item.semanticVersion }}
                      </span>
                    }
                  </h2>
                  <p class="text-muted-foreground mt-1 text-sm">
                    Registered by {{ item.registeredBy }}
                    <span [title]="item.registeredAt.toISOString()">
                      {{ item.registeredAt | cdRelativeTime }}
                    </span>
                    @if (item.baseline !== null) {
                      · replaces <span class="font-mono">v{{ item.baseline }}</span>
                    }
                  </p>
                  @if (item.reviewers.length > 0) {
                    <p class="text-muted-foreground mt-1 text-sm" data-testid="queue-reviewers">
                      Decided by
                      <span class="text-foreground font-mono break-words">{{
                        item.reviewers.join(', ')
                      }}</span>
                    </p>
                  }
                  @if (item.changelog) {
                    <p class="text-foreground mt-2 text-sm whitespace-pre-wrap">
                      {{ item.changelog }}
                    </p>
                  }
                </div>

                <div class="flex shrink-0 flex-wrap items-center gap-2">
                  <cd-status-badge tone="warning">AWAITING_APPROVAL</cd-status-badge>
                </div>
              </div>

              <div class="mt-4 flex flex-wrap items-center gap-2">
                <button
                  type="button"
                  hlmBtn
                  variant="outline"
                  size="sm"
                  [attr.aria-expanded]="isOpen(id)"
                  [attr.aria-controls]="'review-' + id"
                  (click)="toggle(id)"
                >
                  <cd-icon [name]="isOpen(id) ? 'chevron-down' : 'chevron-right'" size="0.875rem" />
                  {{ isOpen(id) ? 'Hide' : 'Review' }} changes and impact
                </button>

                <ng-container *cdIfScope="approvalScopes">
                  @if (!item.mayDecide) {
                    <!--
                      The subject names reviewers and the caller is not one (ADR-043). Absent, like every
                      control the caller may not use (ADR-018), and said, because an administrator who sees
                      neither button and no reason would look for the bug in the page.
                    -->
                    <p class="text-muted-foreground text-sm" data-testid="not-a-reviewer">
                      Only {{ item.reviewers.join(', ') }} may decide this.
                    </p>
                  } @else if (confirming() === id + ':approve' || confirming() === id + ':reject') {
                    <span class="text-foreground text-sm">
                      {{
                        confirming() === id + ':approve'
                          ? 'Approve v' + item.ordinal + ' and make it latest?'
                          : 'Reject v' + item.ordinal + '? This cannot be undone.'
                      }}
                    </span>
                    <button
                      type="button"
                      hlmBtn
                      size="sm"
                      [variant]="confirming() === id + ':reject' ? 'destructive' : 'default'"
                      [disabled]="state?.state === 'deciding'"
                      (click)="confirm(item.subject, item.ordinal)"
                    >
                      Confirm
                    </button>
                    <button type="button" hlmBtn variant="ghost" size="sm" (click)="cancel()">
                      Cancel
                    </button>
                  } @else {
                    <button
                      type="button"
                      hlmBtn
                      size="sm"
                      [disabled]="state?.state === 'deciding'"
                      (click)="ask(id, 'approve')"
                    >
                      Approve
                    </button>
                    <button
                      type="button"
                      hlmBtn
                      variant="outline"
                      size="sm"
                      [disabled]="state?.state === 'deciding'"
                      (click)="ask(id, 'reject')"
                    >
                      Reject
                    </button>
                  }
                </ng-container>
              </div>

              @if (state?.state === 'failed') {
                <div hlmAlert variant="destructive" class="mt-4">
                  <h3 hlmAlertTitle>
                    Could not {{ state?.action === 'approve' ? 'approve' : 'reject' }} this version
                  </h3>
                  <p hlmAlertDescription>{{ failure(id) }}</p>
                </div>
              }

              @if (isOpen(id)) {
                <div class="border-border mt-4 border-t pt-4" [id]="'review-' + id">
                  <cd-pending-review [pending]="item" />
                </div>
              }
            </li>
          }
        </ul>

        @if (store.unread() > 0) {
          <div
            class="border-border bg-card flex flex-col gap-3 rounded-xl border p-4 sm:flex-row sm:items-center sm:justify-between"
          >
            <p class="text-muted-foreground text-sm" data-testid="queue-extent">
              Showing the {{ store.pending().length }} oldest of {{ store.total() }} waiting.
              {{ store.unread() }} more {{ store.unread() === 1 ? 'is' : 'are' }} not shown yet.
            </p>
            <button
              type="button"
              hlmBtn
              variant="outline"
              size="sm"
              [disabled]="store.loadingMore()"
              (click)="store.loadMore()"
            >
              {{ store.loadingMore() ? 'Loading…' : 'Show more' }}
            </button>
          </div>
        }

        @if (store.moreError(); as error) {
          <div hlmAlert variant="destructive">
            <h2 hlmAlertTitle>Could not load more</h2>
            <p hlmAlertDescription>{{ error.detail }}</p>
          </div>
        }
      }
    </section>
  `,
})
export class ApprovalsPage {
  protected readonly store = inject(PendingApprovalsStore);
  protected readonly environments = inject(ActiveEnvironmentStore);

  /** Who may decide (ADR-018), from the one list. */
  protected readonly approvalScopes = APPROVAL_SCOPES;

  protected readonly key = keyOf;

  /** The subject the address narrows the queue to, or null (ADR-052). Bound by the router from the query. */
  readonly subject = input<string | readonly string[] | undefined>();

  /** The version the address points at, as text; read as a number below. Bound by the router from the query. */
  readonly version = input<string | readonly string[] | undefined>();

  /** The subject filter, trimmed as the registry trims it; null when there is none. */
  protected readonly filter = computed(() => {
    const value = first(this.subject())?.trim();

    return value ? value : null;
  });

  /** The ordinal the address points at: only within a filter, and only a positive whole number. */
  private readonly targetOrdinal = computed(() => {
    const value = first(this.version())?.trim() ?? '';

    return this.filter() !== null && /^\d{1,9}$/.test(value) && Number(value) > 0
      ? Number(value)
      : null;
  });

  /** The version the address named when it is not in the list (decided since, or never held). */
  protected readonly absent = signal<{ subject: string; ordinal: number } | null>(null);

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);

  /** The target already handled, so that a refresh after a decision does not open and scroll it again. */
  private handled: string | null = null;

  /** Rows whose changes and impact are open. */
  private readonly open: WritableSignal<ReadonlySet<string>> = signal(new Set());

  /** The decision awaiting its second click, as `key:action`, or null. */
  protected readonly confirming = signal<string | null>(null);

  constructor() {
    // Reloads on an environment switch and when the address narrows the queue to another subject (or stops
    // narrowing it). The filter is read here and handed over: the store never reads the address.
    effect(() => {
      this.environments.name();
      const subject = this.filter();

      untracked(() => this.store.load(subject));
    });

    // Once the queue for the address has been read, opens the review of the version it points at and takes
    // the reader there. Once per target: a refresh after a decision changes the list and must not reopen
    // what the reader closed or pull the page back to a row they have moved on from.
    effect(() => {
      const subject = this.filter();
      const ordinal = this.targetOrdinal();
      const items = this.store.pending();
      const settled = this.store.loaded() && !this.store.loading() && this.store.error() === null;

      untracked(() => {
        if (subject === null || ordinal === null) {
          this.handled = null;
          this.absent.set(null);
          return;
        }

        const target = keyOf(subject, ordinal);

        if (!settled || this.handled === target) {
          return;
        }

        this.handled = target;

        if (!items.some((i) => i.subject === subject && i.ordinal === ordinal)) {
          this.absent.set({ subject, ordinal });
          return;
        }

        this.absent.set(null);
        const next = new Set(this.open());
        next.add(target);
        this.open.set(next);

        // After the row (and its review) is in the document, not before: there is nothing to scroll to until
        // the render that draws it.
        afterNextRender(() => this.reveal(target), { injector: this.injector });
      });
    });
  }

  /** Scrolls the version's heading into view and puts the focus on it. */
  private reveal(id: string): void {
    const heading = [
      ...this.host.nativeElement.querySelectorAll<HTMLElement>('[data-pending-key]'),
    ].find((el) => el.dataset['pendingKey'] === id);

    heading?.scrollIntoView?.({ block: 'start' });
    heading?.focus({ preventScroll: true });
  }

  protected isOpen(id: string): boolean {
    return this.open().has(id);
  }

  protected toggle(id: string): void {
    const next = new Set(this.open());

    if (!next.delete(id)) {
      next.add(id);
    }

    this.open.set(next);
  }

  protected ask(id: string, action: Decision['action']): void {
    this.confirming.set(`${id}:${action}`);
  }

  protected cancel(): void {
    this.confirming.set(null);
  }

  protected confirm(subject: string, ordinal: number): void {
    const pending = this.confirming();

    if (pending === null) {
      return;
    }

    const action = pending.endsWith(':reject') ? 'reject' : 'approve';
    this.confirming.set(null);
    this.store.decide({ subject, ordinal, action });
  }

  protected failure(id: string): string {
    const state = this.store.decisions()[id];
    return state?.state === 'failed' ? state.error.detail : '';
  }
}

/** A routed parameter that the address repeated is a list; the first is the one that is read. */
function first(value: string | readonly string[] | undefined): string | undefined {
  return typeof value === 'string' ? value : value?.[0];
}
