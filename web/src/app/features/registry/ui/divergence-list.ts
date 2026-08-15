import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { Divergence } from '../../../domain/registry/compatibility';
import { StatusBadge } from '../../../shared/ui/status-badge/status-badge';

interface DivergenceGroup {
  readonly version: number;
  readonly divergences: readonly Divergence[];
}

/**
 * The differences between two schemas, as the compatibility engine reported them.
 *
 * Shared by registration, the diff screen and the approval queue, because all three are
 * answering the same question and a reader should not have to learn three layouts for it.
 *
 * <b>The JSON Pointer is the most important thing on each row and is treated that way.</b>
 * "A required field was added" is not actionable; `/properties/discount` is. It is rendered
 * in monospace and first, because the path is what someone takes back to their schema file.
 *
 * <b>`kind` is shown but never branched on.</b> The catalogue lives server-side in
 * `BreakingChangeKinds` and grows per format — Avro and Protobuf add their own — so a UI that
 * switched on it would need a release every time the registry learned a new one. `message` is
 * written by the registry to be shown verbatim, and that is what this renders.
 */
@Component({
  selector: 'cd-divergence-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [StatusBadge],
  host: { class: 'block' },
  template: `
    <h2 class="text-foreground mb-2 text-sm font-semibold">
      {{ heading() }}
    </h2>

    <div class="space-y-4">
      @for (group of groups(); track group.version) {
        <div>
          <!--
            Grouped by the version each difference conflicts with, because a transitive mode
            compares against several at once — and a flat list then repeats the same path
            three times with no indication that they are three separate comparisons.
          -->
          <p class="text-muted-foreground mb-2 text-xs">
            Compared against <span class="font-mono">v{{ group.version }}</span>
          </p>

          <ul class="space-y-2">
            @for (divergence of group.divergences; track divergence.path + divergence.kind) {
              <li class="border-border rounded-lg border p-3">
                <div class="flex flex-wrap items-center gap-2">
                  <code class="text-foreground font-mono text-xs">{{ divergence.path }}</code>
                  <cd-status-badge tone="neutral">{{ divergence.kind }}</cd-status-badge>
                  <span class="text-muted-foreground font-mono text-xs">
                    {{ divergence.direction }} · {{ divergence.surface }}
                  </span>
                </div>
                <p class="text-muted-foreground mt-1.5 text-sm">{{ divergence.message }}</p>
              </li>
            }
          </ul>
        </div>
      }
    </div>
  `,
})
export class DivergenceList {
  readonly divergences = input.required<readonly Divergence[]>();

  /**
   * What to call the list.
   *
   * Overridable because the same data means different things in different places: on a diff
   * it is every difference, and on a registration it is why the change was held.
   */
  readonly heading = input<string>('Differences');

  protected readonly groups = computed<readonly DivergenceGroup[]>(() => {
    const byVersion = new Map<number, Divergence[]>();

    for (const divergence of this.divergences()) {
      const existing = byVersion.get(divergence.conflictsWithVersion);

      if (existing === undefined) {
        byVersion.set(divergence.conflictsWithVersion, [divergence]);
      } else {
        existing.push(divergence);
      }
    }

    // Newest comparison first: under a transitive mode the nearest version is the one whose
    // conflict a reader is most likely to be able to do something about.
    return [...byVersion.entries()]
      .sort(([a], [b]) => b - a)
      .map(([version, divergences]) => ({ version, divergences }));
  });
}
