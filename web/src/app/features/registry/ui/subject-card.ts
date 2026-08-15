import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { latestVersion, pendingVersions, type Subject } from '../../../domain/registry/subject';
import { Icon } from '../../../shared/ui/icon/icon';
import { StatusBadge } from '../../../shared/ui/status-badge/status-badge';
import { SubjectName } from '../../../shared/ui/subject-name/subject-name';
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
 * <b>The hover treatment is back, now that `SubjectDetailPage` exists to receive the
 * click.</b> It is driven by `group-hover` from whatever wraps the card, rather than by the
 * card's own `:hover`: the anchor is the screen's to own — this layer still knows nothing
 * about routing — and a card that lit up under the mouse without a wrapping link would be
 * the same broken promise, made by a different file.
 */
@Component({
  selector: 'cd-subject-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon, StatusBadge, SubjectName, RelativeTimePipe],
  host: { class: 'block' },
  template: `
    <article
      class="border-border bg-card group-hover:border-primary/40 group-hover:shadow-primary/5 relative h-full overflow-hidden rounded-xl border p-5 transition-all group-hover:-translate-y-0.5 group-hover:shadow-lg"
    >
      <div class="flex items-start gap-4">
        <span
          class="bg-primary/10 text-primary flex size-10 shrink-0 items-center justify-center rounded-lg"
        >
          <cd-icon name="file-json" size="1.25rem" />
        </span>

        <div class="min-w-0 flex-1">
          <!--
            Wrapping, and the heading deliberately keeps its min-content width. Two cards
            abreast on a tablet leave this row about 120px, which is narrower than a leaf
            segment: with the pill pinned beside it the name had to break mid-word and orphan
            a letter on its own line. Letting the row wrap spends the width on the name and
            drops the pill underneath, which is the right order — the pill says which version,
            and the name says which subject.
          -->
          <div class="mb-1 flex flex-wrap items-start gap-2">
            <h3 class="text-foreground font-semibold">
              <cd-subject-name [name]="subject().name" />
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
