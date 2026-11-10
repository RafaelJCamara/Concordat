import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
import { DiffStore } from '../application/diff-store';
import { ImpactStore } from '../application/impact-store';
import { REVIEW_VIEWS, ReviewViewStore, type ReviewView } from '../application/review-view-store';
import type { PendingApproval } from '../../../domain/registry/approval-queue';
import { ConsumerImpactList } from '../ui/consumer-impact-list';
import { DivergenceList } from '../ui/divergence-list';
import { PendingSchemaDiff } from './pending-schema-diff';

/** The two ways to read a held change, with the words the choice is labelled in. */
const VIEW_LABELS: Record<ReviewView, string> = { detailed: 'Detailed', summary: 'Summary' };

/** Gives each open review its own radio group, so choosing in one does not clear another. */
let nextGroup = 0;

/**
 * What a held version changes and who it would break — the two facts a reviewer needs before
 * deciding, loaded when the row is opened.
 *
 * <b>Lazy, per row, and a component of its own because each row needs its own stores.</b> A
 * queue of twenty held versions would otherwise issue forty requests on arrival for rows nobody
 * opened, and a store shared between rows could show one version's consumers under another's
 * heading. The two stores are the same ones the diff and impact screens use, so what is read
 * here is what those screens would say — and the links at the bottom lead to them.
 *
 * <b>Two ways to read the changes, and the reviewer's choice is remembered</b> (ADR-050).
 * *Summary* is the registry's list of findings, as this screen always showed it, and is what a
 * first visit gets. *Detailed* is the schema itself as a line diff with the same findings on the
 * lines they are about (`cd-pending-schema-diff`). The choice is one value for the whole screen,
 * kept in the browser (`ReviewViewStore`); a detailed view that cannot be shown — too large, a
 * schema that would not load, a worker that would not run — says why and the summary stands in.
 *
 * <b>Not routed, but in `feature/`</b>, because it owns stores: `ui/` may not, and a component
 * that fetched would stop being restyleable without a regression test for fetching.
 */
@Component({
  selector: 'cd-pending-review',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [DiffStore, ImpactStore],
  imports: [
    RouterLink,
    HlmAlertImports,
    HlmSkeleton,
    ConsumerImpactList,
    DivergenceList,
    PendingSchemaDiff,
  ],
  host: { class: 'block space-y-5' },
  template: `
    @let item = pending();

    @if (item.baseline === null) {
      <div hlmAlert>
        <h3 hlmAlertTitle>Nothing to compare against</h3>
        <p hlmAlertDescription>
          No version of this subject has been approved yet, so there is no baseline to diff.
        </p>
      </div>
    } @else {
      <!--
        A radio group, and a native one: arrow keys, one tab stop and the checked state are the
        browser's own. The input is transparent and laid over its label, so the whole pill is
        the control, the focus ring is drawn on the pill (has-focus-visible), and focusing it
        cannot scroll anything (a visually hidden input is somewhere else on the page). The group is one for
        the whole screen (ReviewViewStore) and is remembered, so choosing here moves every open
        review.
      -->
      <div
        role="radiogroup"
        aria-label="Show the changes as"
        class="border-border bg-muted inline-flex rounded-lg border p-0.5 text-sm"
        data-testid="review-view"
      >
        @for (option of views; track option) {
          <label
            class="has-checked:bg-background has-checked:text-foreground has-checked:shadow-sm text-muted-foreground hover:text-foreground has-focus-visible:ring-ring relative cursor-pointer rounded-md px-3 py-1 font-medium has-focus-visible:ring-2"
          >
            <input
              type="radio"
              class="absolute inset-0 size-full cursor-pointer opacity-0"
              [name]="group"
              [value]="option"
              [checked]="view() === option"
              (change)="choose(option)"
            />
            {{ labels[option] }}
          </label>
        }
      </div>

      @if (view() === 'detailed' && diff.diff(); as result) {
        <cd-pending-schema-diff [diff]="result" (fellBack)="fellBack.set($event)" />
      }

      @if (showSummary()) {
        @if (diff.loading()) {
          <div hlmSkeleton class="h-20 w-full rounded-lg" aria-busy="true"></div>
        } @else if (diff.diff(); as result) {
          @if (result.divergences.length > 0) {
            <cd-divergence-list
              [divergences]="result.divergences"
              [heading]="'Changes from v' + item.baseline"
            />
          } @else {
            <p class="text-muted-foreground text-sm">
              No compatibility rule reports a difference from v{{ item.baseline }}.
            </p>
          }
        }
      }

      @if (diff.error(); as error) {
        <div hlmAlert variant="destructive">
          <h3 hlmAlertTitle>Could not load the changes</h3>
          <p hlmAlertDescription>{{ error.detail }}</p>
        </div>
      }
    }

    @if (impact.loading()) {
      <div hlmSkeleton class="h-20 w-full rounded-lg" aria-busy="true"></div>
    } @else if (impact.report(); as report) {
      <div>
        <h3 class="text-foreground mb-2 text-sm font-semibold">
          @if (report.consumers.length === 0) {
            Registered consumers
          } @else {
            Registered consumers · {{ report.breakingCount }} of
            {{ report.consumers.length }}
            {{ report.consumers.length === 1 ? 'breaks' : 'break' }}
          }
        </h3>
        @if (report.consumers.length === 0) {
          <p class="text-muted-foreground text-sm">
            Nothing has declared that it reads this subject. That is what the registry was told, not
            a finding that nobody is affected.
          </p>
        } @else {
          <cd-consumer-impact-list [consumers]="report.consumers" />
        }
      </div>
    } @else if (impact.error(); as error) {
      <div hlmAlert variant="destructive">
        <h3 hlmAlertTitle>Could not load the impact</h3>
        <p hlmAlertDescription>{{ error.detail }}</p>
      </div>
    }

    <p class="text-muted-foreground text-xs">
      The full
      @if (item.baseline !== null) {
        <a
          class="hover:text-foreground underline"
          [routerLink]="[
            '/subjects',
            item.subject,
            'versions',
            item.baseline,
            'diff',
            item.ordinal,
          ]"
          >comparison</a
        >
        and
      }
      <a
        class="hover:text-foreground underline"
        [routerLink]="['/subjects', item.subject, 'versions', item.ordinal, 'impact']"
        >impact analysis</a
      >
      are on their own screens.
    </p>
  `,
})
export class PendingReview {
  readonly pending = input.required<PendingApproval>();

  protected readonly diff = inject(DiffStore);
  protected readonly impact = inject(ImpactStore);
  private readonly views$ = inject(ReviewViewStore);

  protected readonly views = REVIEW_VIEWS;
  protected readonly labels = VIEW_LABELS;
  protected readonly group = `review-view-${++nextGroup}`;

  /** The reviewer's remembered choice, shared by every open review. */
  protected readonly view = this.views$.view;

  /** Whether the detailed view reported that it could not be shown. */
  protected readonly fellBack = signal(false);

  /**
   * Whether the registry's own list of findings is on screen.
   *
   * It is the summary view; it is also what stands in when the detailed view cannot be shown, and
   * what is on screen while the version diff the detailed view is built from is still arriving.
   */
  protected readonly showSummary = computed(
    () => this.view() === 'summary' || this.fellBack() || this.diff.diff() === null,
  );

  protected choose(view: ReviewView): void {
    this.views$.choose(view);
  }

  constructor() {
    effect(() => {
      const item = this.pending();

      if (item.baseline !== null) {
        this.diff.load({
          subject: item.subject,
          from: String(item.baseline),
          to: String(item.ordinal),
        });
      }

      this.impact.load({ subject: item.subject, ordinal: String(item.ordinal) });
    });
  }
}
