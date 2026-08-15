import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { latestVersion, pendingVersions, type Subject } from '../../../domain/registry/subject';
import { Icon } from '../../../shared/ui/icon/icon';
import { StatusBadge } from '../../../shared/ui/status-badge/status-badge';
import { RelativeTimePipe } from '../../../shared/pipes/relative-time-pipe';

/**
 * One subject, as a card.
 *
 * The prototype's `SchemaCard`: a tinted 10×10 glyph tile, the name and its version badge
 * on one line, then a footer of dim metadata. What the card *says* is different — the
 * prototype showed a description and a queue count, and a subject here has neither — so it
 * carries the two facts that actually decide whether you click it: what format the contract
 * is in, and whether anything is waiting at the approval gate.
 *
 * <b>No hover lift and no pointer cursor, unlike the prototype's.</b> `SubjectDetailPage`
 * is M4.3; until there is somewhere to go, a card that highlights under the mouse is a
 * promise the app does not keep, and the click that produces nothing is more annoying than
 * the affordance was inviting. The hover treatment comes back with the route.
 */
@Component({
  selector: 'cd-subject-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon, StatusBadge, RelativeTimePipe],
  host: { class: 'block' },
  template: `
    <article class="border-border bg-card relative h-full overflow-hidden rounded-xl border p-5">
      <div class="flex items-start gap-4">
        <span
          class="bg-primary/10 text-primary flex size-10 shrink-0 items-center justify-center rounded-lg"
        >
          <cd-icon name="file-json" size="1.25rem" />
        </span>

        <div class="min-w-0 flex-1">
          <div class="mb-1 flex items-center gap-2">
            <h3 class="text-foreground truncate font-mono font-semibold">
              {{ subject().name }}
            </h3>
            <span
              class="border-border text-muted-foreground shrink-0 rounded-full border px-2 py-0.5 font-mono text-xs"
            >
              {{ latestLabel() }}
            </span>
          </div>

          <p class="text-muted-foreground mb-3 text-sm">
            {{ subject().format }} · owned by {{ subject().owner }}
          </p>

          <div class="text-muted-foreground flex flex-wrap items-center gap-x-4 gap-y-2 text-xs">
            <span class="flex items-center gap-1">
              <cd-icon name="clock" size="0.875rem" />
              <!--
                The title attribute carries the exact timestamp. "3 days ago" is the right
                thing to read at a glance and the wrong thing to paste into an incident
                channel.
              -->
              <span [title]="registeredAt()?.toISOString() ?? ''">
                @if (registeredAt(); as at) {
                  Registered {{ at | cdRelativeTime }}
                } @else {
                  Never registered
                }
              </span>
            </span>

            <span class="flex items-center gap-1">
              <cd-icon name="git-branch" size="0.875rem" />
              {{ versionCount() }}
            </span>
          </div>

          @if (pending() > 0 || subject().lifecycle !== 'ACTIVE') {
            <div class="mt-3 flex flex-wrap gap-2">
              @if (subject().lifecycle !== 'ACTIVE') {
                <cd-status-badge tone="neutral">{{ subject().lifecycle }}</cd-status-badge>
              }
              @if (pending() > 0) {
                <cd-status-badge tone="warning" [pulse]="true">
                  {{ pending() }} awaiting approval
                </cd-status-badge>
              }
            </div>
          }
        </div>
      </div>
    </article>
  `,
})
export class SubjectCard {
  readonly subject = input.required<Subject>();

  private readonly latest = computed(() => latestVersion(this.subject()));

  /**
   * An em dash, not "v0" or "none": a subject with no active version is the ordinary state
   * right after creation, and a zero reads like a count that went wrong.
   */
  protected readonly latestLabel = computed(() => {
    const latest = this.latest();
    return latest === null ? '—' : `v${latest.ordinal}`;
  });

  protected readonly registeredAt = computed(() => this.latest()?.registeredAt ?? null);

  protected readonly pending = computed(() => pendingVersions(this.subject()).length);

  protected readonly versionCount = computed(() => {
    const count = this.subject().versions.length;
    return `${count} version${count === 1 ? '' : 's'}`;
  });
}
