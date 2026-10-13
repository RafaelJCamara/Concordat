import { ChangeDetectionStrategy, Component, computed, effect, inject, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import { Icon } from '../../../shared/ui/icon/icon';
import { StatusBadge } from '../../../shared/ui/status-badge/status-badge';
import { ImpactStore } from '../application/impact-store';
import { ConsumerImpactList } from '../ui/consumer-impact-list';

/**
 * Who breaks if a version of a subject is the one consumers meet.
 *
 * The URL is `/subjects/:name/versions/:ordinal/impact`. `:ordinal` takes the literal
 * `latest` as well as a number, and the heading names the version the registry resolved it
 * to — so a pasted `latest` link says which version it meant on the day it is read.
 *
 * <b>"No consumers" is not "safe", and the screen says so.</b> The registry knows only the
 * consumers that declared themselves, through the SDK or CI. An empty list is the registry
 * reporting what it was told, and a reader approving a breaking change on the strength of
 * "nobody is affected" needs to know it is a statement about declarations, not about the
 * estate.
 *
 * <b>The headline counts only what was checked.</b> A consumer that follows `latest` is
 * neither broken nor safe, so it is counted separately rather than folded into either — the
 * sentence "0 of 3 break" would otherwise be true and misleading at once.
 */
@Component({
  selector: 'cd-impact-analysis-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [ImpactStore],
  imports: [
    RouterLink,
    HlmAlertImports,
    HlmButton,
    HlmSkeleton,
    Icon,
    StatusBadge,
    ConsumerImpactList,
  ],
  template: `
    <section class="mx-auto w-full max-w-6xl space-y-6 p-6 lg:p-8">
      <a
        [routerLink]="['/subjects', name()]"
        class="text-muted-foreground hover:text-foreground inline-flex items-center gap-1 text-sm transition-colors"
      >
        <cd-icon name="chevron-left" size="0.875rem" />
        <span class="font-mono">{{ name() }}</span>
      </a>

      @if (store.loading()) {
        <div class="space-y-4" aria-busy="true" aria-live="polite">
          <div hlmSkeleton class="h-9 w-72"></div>
          <div hlmSkeleton class="h-20 w-full rounded-xl"></div>
          <div hlmSkeleton class="h-48 w-full rounded-xl"></div>
        </div>
      } @else if (store.report(); as report) {
        <header>
          <h1 class="text-foreground text-2xl font-bold tracking-tight">
            Impact of
            @if (report.candidateOrdinal !== null) {
              <a
                class="hover:text-primary font-mono transition-colors"
                [routerLink]="['/subjects', name(), 'versions', report.candidateOrdinal]"
              >
                v{{ report.candidateOrdinal }}
              </a>
            }
          </h1>
          <p class="text-muted-foreground mt-1 text-sm">
            Judged FORWARD: can a consumer still holding the version it declared read what this
            version writes.
          </p>
        </header>

        <div class="border-border bg-card rounded-xl border p-5">
          <div class="flex flex-wrap items-center gap-3">
            @if (report.breakingCount > 0) {
              <cd-status-badge tone="destructive">
                {{ report.breakingCount }} breaking
              </cd-status-badge>
            } @else if (checked() > 0) {
              <cd-status-badge tone="success">No checked consumer breaks</cd-status-badge>
            }
            <p class="text-foreground text-sm">{{ summary() }}</p>
          </div>
        </div>

        @if (report.consumers.length === 0) {
          <div class="border-border bg-card rounded-xl border p-10 text-center">
            <p class="font-medium">No registered consumers</p>
            <p class="text-muted-foreground mx-auto mt-1 max-w-xl text-sm">
              Nothing has declared that it reads this subject. That is the registry reporting what
              it was told, not a finding that nobody is affected — a consumer that never declared
              itself is not in this list.
            </p>
          </div>
        } @else {
          <div>
            <h2 class="text-foreground mb-2 text-sm font-semibold">Registered consumers</h2>
            <cd-consumer-impact-list [consumers]="report.consumers" />
          </div>
        }

        @if (report.contracts.length > 0) {
          <div>
            <h2 class="text-foreground mb-2 text-sm font-semibold">
              Contracts that carry this subject
            </h2>
            <ul class="flex flex-wrap gap-2">
              @for (contract of report.contracts; track contract) {
                <li class="border-border rounded-lg border px-3 py-1.5 font-mono text-sm">
                  {{ contract }}
                </li>
              }
            </ul>
          </div>
        }
      } @else if (store.error(); as error) {
        <div hlmAlert variant="destructive">
          <h2 hlmAlertTitle>Could not analyse this version</h2>
          <p hlmAlertDescription>{{ error.detail }}</p>
          <button hlmBtn hlmAlertAction variant="outline" size="sm" (click)="reload()">
            Try again
          </button>
        </div>
      }
    </section>
  `,
})
export class ImpactAnalysisPage {
  /** The subject name, bound from the route path. */
  readonly name = input.required<string>();

  /** The ordinal, or the literal `latest`. A string because it is both. */
  readonly ordinal = input.required<string>();

  protected readonly store = inject(ImpactStore);
  protected readonly environments = inject(ActiveEnvironmentStore);

  /** How many consumers were actually judged. */
  protected readonly checked = computed(
    () => this.store.report()?.consumers.filter((c) => c.certainty === 'CHECKED').length ?? 0,
  );

  protected readonly summary = computed(() => {
    const report = this.store.report();

    if (report === null || report.consumers.length === 0) {
      return '';
    }

    const total = report.consumers.length;
    const unjudged = total - this.checked();
    const base = `${report.breakingCount} of ${total} registered ${total === 1 ? 'consumer breaks' : 'consumers break'}.`;

    return unjudged === 0
      ? base
      : `${base} ${unjudged} could not be judged, so the registry makes no claim about ${unjudged === 1 ? 'it' : 'them'}.`;
  });

  constructor() {
    effect(() => {
      this.environments.name();
      this.store.load({ subject: this.name(), ordinal: this.ordinal() });
    });
  }

  protected reload(): void {
    this.store.load({ subject: this.name(), ordinal: this.ordinal() });
  }
}
