import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type { ConsumerImpact } from '../../../domain/registry/impact';
import { RelativeTimePipe } from '../../../shared/pipes/relative-time-pipe';
import { StatusBadge, type StatusTone } from '../../../shared/ui/status-badge/status-badge';

interface Verdict {
  readonly tone: StatusTone;
  readonly label: string;
  /** What the verdict does not tell you, when it is not a plain answer. */
  readonly note: string | null;
}

/**
 * What a change does to each registered consumer.
 *
 * Shared by the impact screen and the approval queue, because both answer the same question
 * and the person approving a change should read the same verdict they would have read on its
 * own screen.
 *
 * <b>The verdict is chosen from `certainty` first and `breaks` second.</b> A consumer that
 * follows `latest` carries `breaks: false`, which is the absence of an answer rather than a
 * good one — the registry knows what such a consumer fetches, not what its code was built
 * against. Reading `breaks` alone would badge it green, and "safe to approve" is exactly the
 * conclusion this screen exists to prevent being drawn from nothing.
 */
@Component({
  selector: 'cd-consumer-impact-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RelativeTimePipe, StatusBadge],
  host: { class: 'block' },
  template: `
    <ul class="space-y-2">
      @for (consumer of consumers(); track consumer.service) {
        @let verdict = verdictOf(consumer);
        <li class="border-border rounded-lg border p-3">
          <div class="flex flex-wrap items-center gap-2">
            <span class="text-foreground font-mono text-sm font-medium">{{
              consumer.service
            }}</span>
            <cd-status-badge [tone]="verdict.tone">{{ verdict.label }}</cd-status-badge>
            @if (consumer.stale) {
              <cd-status-badge tone="neutral">STALE</cd-status-badge>
            }
            <span class="text-muted-foreground ms-auto font-mono text-xs">
              declares {{ consumer.selector }}
              @if (consumer.readerOrdinal !== null) {
                · judged as v{{ consumer.readerOrdinal }}
              }
            </span>
          </div>

          @if (verdict.note; as note) {
            <p class="text-muted-foreground mt-1.5 text-sm">{{ note }}</p>
          }

          @if (consumer.reasons.length > 0) {
            <ul class="mt-1.5 list-disc space-y-1 ps-5">
              @for (reason of consumer.reasons; track reason) {
                <li class="text-muted-foreground text-sm">{{ reason }}</li>
              }
            </ul>
          }

          <p class="text-muted-foreground mt-1.5 text-xs">
            Last declared
            <span [title]="consumer.lastSeenAt.toISOString()">
              {{ consumer.lastSeenAt | cdRelativeTime }}
            </span>
          </p>
        </li>
      }
    </ul>
  `,
})
export class ConsumerImpactList {
  readonly consumers = input.required<readonly ConsumerImpact[]>();

  protected verdictOf(consumer: ConsumerImpact): Verdict {
    switch (consumer.certainty) {
      case 'CHECKED':
        return consumer.breaks
          ? { tone: 'destructive', label: 'BREAKS', note: null }
          : { tone: 'success', label: 'COMPATIBLE', note: null };

      case 'FOLLOWS_LATEST':
        return {
          tone: 'info',
          label: 'FOLLOWS LATEST',
          note:
            'The registry cannot say whether this consumer breaks: it follows the pointer, and ' +
            'does not declare what its code was built against.',
        };

      case 'UNRESOLVABLE':
        return {
          tone: 'warning',
          label: 'UNRESOLVABLE',
          note: 'The version this consumer pinned no longer exists, so it cannot be judged.',
        };
    }
  }
}
