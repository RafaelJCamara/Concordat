import { ChangeDetectionStrategy, Component, computed, effect, inject, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import type { SchemaVersion } from '../../../domain/registry/subject';
import { CompatibilityGuide } from '../../../shared/ui/compatibility-meaning/compatibility-guide';
import { Icon } from '../../../shared/ui/icon/icon';
import { StatusBadge, type StatusTone } from '../../../shared/ui/status-badge/status-badge';
import { DiffStore } from '../application/diff-store';
import { DivergenceList } from '../ui/divergence-list';

const STATUS_TONES: Record<SchemaVersion['status'], StatusTone> = {
  ACTIVE: 'success',
  AWAITING_APPROVAL: 'warning',
  REJECTED: 'destructive',
  DISMISSED: 'neutral',
};

/**
 * What changed between two versions of a subject.
 *
 * The URL is `/subjects/:name/versions/:from/diff/:to`, which is the API's own path with the
 * resource prefix dropped — so a link read out of a registry log and a link pasted from the
 * address bar are the same shape. `from` is the baseline and `to` is checked against it.
 *
 * <b>Three different answers, because "no divergences" is not one of them.</b> Two versions
 * can be the same document, can differ in a way no compatibility rule reads, or can differ
 * in ways that matter. The middle one is real: under an open content model adding a property
 * cannot break anybody, so the engine records nothing for the most common schema change
 * there is (decisions-pending #12). Rendering that as "no changes" would contradict the two
 * different schema ids sitting in the header, so it says what it knows — the documents
 * differ, and no rule reports it — and where to look instead.
 */
@Component({
  selector: 'cd-compatibility-diff-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [DiffStore],
  imports: [
    RouterLink,
    HlmAlertImports,
    HlmButton,
    HlmSkeleton,
    CompatibilityGuide,
    Icon,
    StatusBadge,
    DivergenceList,
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
          <div hlmSkeleton class="h-9 w-80"></div>
          <div hlmSkeleton class="h-24 w-full rounded-xl"></div>
          <div hlmSkeleton class="h-48 w-full rounded-xl"></div>
        </div>
      } @else if (store.diff(); as diff) {
        <header class="flex flex-col gap-4 sm:flex-row sm:items-start sm:justify-between">
          <div>
            <h1 class="text-foreground font-mono text-2xl font-bold tracking-tight">
              v{{ diff.from }}
              <cd-icon name="arrow-right" size="1.25rem" class="text-muted-foreground" />
              v{{ diff.to }}
            </h1>
            <p class="text-muted-foreground mt-1 text-sm">
              Judged under
              <span class="text-foreground font-mono">
                {{ diff.policy.mode ?? 'default' }} · {{ diff.policy.surface ?? 'default' }}
              </span>
            </p>
            @if (diff.policy.mode !== null) {
              <cd-compatibility-guide
                class="mt-1"
                [modes]="[diff.policy.mode]"
                [surfaces]="[diff.policy.surface]"
                label="What does this policy mean?"
              />
            }
          </div>

          <a
            hlmBtn
            variant="outline"
            size="sm"
            [routerLink]="['/subjects', name(), 'versions', diff.to, 'diff', diff.from]"
          >
            Swap direction
          </a>
        </header>

        <div class="grid gap-4 sm:grid-cols-2">
          @for (end of ends(); track end.role) {
            <div class="border-border bg-card rounded-xl border p-5">
              <h2 class="text-muted-foreground mb-2 text-xs">{{ end.role }}</h2>
              <div class="flex flex-wrap items-center gap-2">
                <a
                  [routerLink]="['/subjects', name(), 'versions', end.ordinal]"
                  class="text-foreground hover:text-primary font-mono text-lg font-semibold transition-colors"
                >
                  v{{ end.ordinal }}
                </a>
                @if (end.version; as version) {
                  <cd-status-badge [tone]="tone(version)">{{ version.status }}</cd-status-badge>
                  @if (version.semanticVersion) {
                    <span class="text-muted-foreground font-mono text-xs">
                      {{ version.semanticVersion }}
                    </span>
                  }
                }
              </div>
              <p class="text-muted-foreground mt-2 font-mono text-xs break-all">
                {{ end.schemaId }}
              </p>
            </div>
          }
        </div>

        @if (diff.identical) {
          <div hlmAlert>
            <h2 hlmAlertTitle>These are the same document</h2>
            <p hlmAlertDescription>
              Both versions point at one schema, so there is nothing to compare.
            </p>
          </div>
        } @else if (diff.divergences.length === 0) {
          <div hlmAlert>
            <h2 hlmAlertTitle>The documents differ, and no compatibility rule reports it</h2>
            <p hlmAlertDescription>
              The two schema ids are not the same, but the change is one the engine does not read —
              under an open content model, adding or removing a property is invisible to it. Compare
              the two schema files directly to see what changed.
            </p>
          </div>
        } @else {
          <cd-divergence-list [divergences]="diff.divergences" heading="Differences" />
        }
      } @else if (store.error(); as error) {
        <div hlmAlert variant="destructive">
          <h2 hlmAlertTitle>Could not compare these versions</h2>
          <p hlmAlertDescription>{{ error.detail }}</p>
          <button hlmBtn hlmAlertAction variant="outline" size="sm" (click)="reload()">
            Try again
          </button>
        </div>
      }
    </section>
  `,
})
export class CompatibilityDiffPage {
  /** The subject name, bound from the route path. */
  readonly name = input.required<string>();

  /** The baseline ordinal. A string because it comes out of a URL. */
  readonly from = input.required<string>();

  /** The ordinal checked against the baseline. */
  readonly to = input.required<string>();

  protected readonly store = inject(DiffStore);
  protected readonly environments = inject(ActiveEnvironmentStore);

  constructor() {
    effect(() => {
      this.environments.name();
      this.store.load({ subject: this.name(), from: this.from(), to: this.to() });
    });
  }

  /** The two ends, in reading order, each with whatever the subject says about it. */
  protected readonly ends = computed(() => {
    const diff = this.store.diff();

    return diff === null
      ? []
      : [
          {
            role: 'Baseline',
            ordinal: diff.from,
            schemaId: diff.fromSchemaId,
            version: this.store.fromVersion(),
          },
          {
            role: 'Compared',
            ordinal: diff.to,
            schemaId: diff.toSchemaId,
            version: this.store.toVersion(),
          },
        ];
  });

  protected tone(version: SchemaVersion): StatusTone {
    return STATUS_TONES[version.status];
  }

  protected reload(): void {
    this.store.load({ subject: this.name(), from: this.from(), to: this.to() });
  }
}
