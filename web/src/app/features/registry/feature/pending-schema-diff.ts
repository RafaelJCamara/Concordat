import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  output,
} from '@angular/core';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
import type { VersionDiff } from '../../../domain/registry/diff';
import type { SchemaDiffReady, SchemaDiffTooLarge } from '../../../domain/registry/schema-diff';
import { SchemaDiffStore, type SchemaDiffProblem } from '../application/schema-diff-store';
import { SchemaDiff } from '../ui/schema-diff';

/**
 * The detailed view of one held change: its schema as a line diff, loaded when it is shown
 * (ADR-050).
 *
 * <b>A component of its own because it owns a store, and because its store should exist only
 * when the reviewer asked for it.</b> `PendingReview` renders this only under the detailed view,
 * so a review left on the summary creates no store, fetches no schema and starts no worker.
 *
 * <b>It never leaves the reviewer with nothing.</b> A diff that cannot be shown — a schema that
 * would not load, a worker that would not run or took too long, a change too large to be worth
 * reading as lines — is said in one sentence and reported to the parent through `fellBack`, which
 * shows the summary underneath. Nothing is truncated quietly: either every changed line is on
 * screen or the screen says that it is not.
 */
@Component({
  selector: 'cd-pending-schema-diff',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [SchemaDiffStore],
  imports: [HlmAlertImports, HlmSkeleton, SchemaDiff],
  host: { class: 'block' },
  template: `
    @if (store.loading()) {
      <div hlmSkeleton class="h-24 w-full rounded-lg" aria-busy="true"></div>
    } @else if (note(); as text) {
      <div hlmAlert data-testid="diff-fallback">
        <h3 hlmAlertTitle>The line comparison is not shown</h3>
        <p hlmAlertDescription>{{ text }}</p>
      </div>
    } @else if (ready(); as result) {
      <h3 class="text-foreground mb-2 text-sm font-semibold">Changes from v{{ diff().from }}</h3>
      @if (diff().divergences.length === 0) {
        <p class="text-muted-foreground mb-2 text-sm">
          No compatibility rule reports a difference from v{{ diff().from }}.
        </p>
      }
      <cd-schema-diff
        [result]="result"
        [divergences]="diff().divergences"
        [label]="'Schema changes, v' + diff().from + ' to v' + diff().to"
      />
    }
  `,
})
export class PendingSchemaDiff {
  /** The version diff the review already holds: which two schemas, and what the engine found. */
  readonly diff = input.required<VersionDiff>();

  /** Whether the line diff could not be shown, so the parent should show the summary instead. */
  readonly fellBack = output<boolean>();

  protected readonly store = inject(SchemaDiffStore);

  protected readonly ready = computed<SchemaDiffReady | null>(() => {
    const result = this.store.result();
    return result?.status === 'ready' ? result : null;
  });

  /** Why the line diff is not on screen, or null when it is, or is still on its way. */
  protected readonly note = computed<string | null>(() => {
    const problem = this.store.problem();
    const result = this.store.result();

    if (problem !== null) {
      return problemSentence(problem);
    }

    return result?.status === 'too-large' ? tooLargeSentence(result) : null;
  });

  constructor() {
    effect(() => {
      const { fromSchemaId, toSchemaId, divergences } = this.diff();

      this.store.load({ from: fromSchemaId, to: toSchemaId, divergences });
    });

    effect(() => this.fellBack.emit(this.note() !== null));
  }
}

/** The sentence for a comparison that could not be made. Each ends by saying what is shown. */
function problemSentence(problem: SchemaDiffProblem): string {
  switch (problem.reason) {
    case 'load':
      return `The two schemas could not be loaded (${problem.detail}). The summary is shown instead.`;
    case 'timeout':
      return 'The comparison took too long and was stopped. The summary is shown instead.';
    case 'unavailable':
      return `The comparison could not run in this browser (${problem.detail}). The summary is shown instead.`;
  }
}

/** The sentence for a change past the guard, naming the limit it passed. */
function tooLargeSentence(result: SchemaDiffTooLarge): string {
  const limit = result.limit.toLocaleString('en-US');

  return result.reason === 'lines'
    ? `A schema here is longer than ${limit} lines, more than the comparison reads. The summary is shown instead.`
    : `More than ${limit} lines changed, which is more than reads well as a line comparison. The summary is shown instead.`;
}
