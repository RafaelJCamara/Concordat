import { ChangeDetectionStrategy, Component, effect, inject, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
import { DiffStore } from '../application/diff-store';
import { ImpactStore } from '../application/impact-store';
import type { PendingApproval } from '../../../domain/registry/approval-queue';
import { ConsumerImpactList } from '../ui/consumer-impact-list';
import { DivergenceList } from '../ui/divergence-list';

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
 * <b>Not routed, but in `feature/`</b>, because it owns stores: `ui/` may not, and a component
 * that fetched would stop being restyleable without a regression test for fetching.
 */
@Component({
  selector: 'cd-pending-review',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [DiffStore, ImpactStore],
  imports: [RouterLink, HlmAlertImports, HlmSkeleton, ConsumerImpactList, DivergenceList],
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
    } @else if (diff.loading()) {
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
    } @else if (diff.error(); as error) {
      <div hlmAlert variant="destructive">
        <h3 hlmAlertTitle>Could not load the changes</h3>
        <p hlmAlertDescription>{{ error.detail }}</p>
      </div>
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
