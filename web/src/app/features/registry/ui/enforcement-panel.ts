import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import {
  contractCount,
  type EnforcementTier,
  type EnvironmentSummary,
} from '../../../domain/registry/environment-summary';
import { EnforcementBadge } from '../../../shared/ui/enforcement-badge/enforcement-badge';
import { Icon } from '../../../shared/ui/icon/icon';

/** The counts one row shows, and how each is worded so that one reads as a noun and not "1 s". */
function plural(count: number, singular: string): string {
  return `${count} ${singular}${count === 1 ? '' : 's'}`;
}

/**
 * How contracts are set, by enforcement mode.
 *
 * <b>This is coverage as declared, and the panel says so.</b> The registry never sees message
 * traffic: a contract set to `ENFORCE` is an instruction clients receive when they resolve a route,
 * not proof that any service is running a client that follows it. And it is a count of contracts and
 * bindings, not a share of anything: it cannot say what fraction of subjects, services or traffic is
 * covered, because a binding names its subjects in a free-text list the database does not index, and
 * saying so is better than a percentage that looks like it can. ADR-037 spells out both.
 *
 * Presentational: tiers in, nothing else.
 */
@Component({
  selector: 'cd-enforcement-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, EnforcementBadge, Icon],
  host: { class: 'block' },
  template: `
    <section class="border-border bg-card h-full rounded-xl border" aria-labelledby="cd-ec-title">
      <header class="flex items-start justify-between gap-4 p-5 pb-3">
        <div class="min-w-0">
          <h2 id="cd-ec-title" class="text-foreground text-lg font-semibold">
            Enforcement coverage
          </h2>
          <p class="text-muted-foreground mt-0.5 text-sm">Contracts and bindings, by mode.</p>
        </div>
        <a
          routerLink="/contracts"
          class="text-muted-foreground hover:text-foreground focus-visible:ring-ring flex shrink-0 items-center gap-1 rounded-sm text-sm transition-colors focus-visible:ring-2 focus-visible:outline-none"
        >
          Contracts
          <cd-icon name="arrow-right" size="0.875rem" />
        </a>
      </header>

      @if (contracts() === 0) {
        <div class="px-5 pb-4">
          <p class="font-medium">No contracts in this environment</p>
          <p class="text-muted-foreground mt-1 text-sm">
            A contract says which subjects may travel on which routes, and how strictly that is held
            to. Until one exists nothing here is governed.
          </p>
        </div>
      } @else {
        <ul class="divide-border divide-y" aria-label="Contracts by enforcement mode">
          @for (tier of tiers(); track tier.mode) {
            <li class="flex flex-wrap items-center gap-x-4 gap-y-1 px-5 py-3">
              <span class="w-24 shrink-0"><cd-enforcement-badge [mode]="tier.mode" /></span>
              <span class="text-foreground text-sm font-medium tabular-nums">
                {{ contractsLabel(tier) }}
              </span>
              <span class="text-muted-foreground text-xs tabular-nums">
                {{ publishLabel(tier) }} · {{ consumeLabel(tier) }}
              </span>
            </li>
          }
        </ul>
      }

      <p class="text-muted-foreground border-border border-t px-5 py-3 text-xs">
        Declared, not observed. The registry never sees message traffic, so this is what contracts
        tell clients to do, not proof that a service does it. Violations are reported by SDKs: none
        reported is not the same as none happened.
      </p>
    </section>
  `,
})
export class EnforcementPanel {
  readonly summary = input.required<EnvironmentSummary>();

  protected readonly tiers = computed(() => this.summary().enforcement);
  protected readonly contracts = computed(() => contractCount(this.summary()));

  protected contractsLabel(tier: EnforcementTier): string {
    return plural(tier.contracts, 'contract');
  }

  protected publishLabel(tier: EnforcementTier): string {
    return plural(tier.publishBindings, 'publish binding');
  }

  protected consumeLabel(tier: EnforcementTier): string {
    return plural(tier.consumeBindings, 'consume binding');
  }
}
