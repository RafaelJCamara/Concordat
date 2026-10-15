import {
  ChangeDetectionStrategy,
  Component,
  effect,
  inject,
  signal,
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
          <button hlmBtn hlmAlertAction variant="outline" size="sm" (click)="store.load()">
            Try again
          </button>
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
        <ul class="space-y-4">
          @for (item of store.pending(); track key(item.subject, item.ordinal)) {
            @let id = key(item.subject, item.ordinal);
            @let state = store.decisions()[id];
            <li class="border-border bg-card rounded-xl border p-5">
              <div class="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
                <div class="min-w-0">
                  <h2 class="text-foreground font-mono text-lg font-semibold">
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
                  @if (confirming() === id + ':approve' || confirming() === id + ':reject') {
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

  /** Rows whose changes and impact are open. */
  private readonly open: WritableSignal<ReadonlySet<string>> = signal(new Set());

  /** The decision awaiting its second click, as `key:action`, or null. */
  protected readonly confirming = signal<string | null>(null);

  constructor() {
    effect(() => {
      this.environments.name();
      this.store.load();
    });
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
