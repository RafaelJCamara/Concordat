import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Icon, type IconName } from '../icon/icon';

/**
 * A single headline number, as a card.
 *
 * The prototype's `StatCard`, down to the 12×12 tinted icon tile that scales to 110% on
 * hover and the blurred primary-tinted blob that fades up from the bottom-right corner.
 * Both are pure decoration and both are why the dashboard reads as a product rather than a
 * report — so they are ported rather than trimmed.
 *
 * <b>A card with a `route` renders as an anchor, not a div with a click handler.</b> The
 * prototype used `onClick` and got a card that a keyboard cannot reach, a screen reader
 * does not announce as a destination, and middle-click cannot open in a tab. The hover
 * treatment is identical either way, so nothing about the look depends on the difference.
 */
@Component({
  selector: 'cd-stat-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [NgTemplateOutlet, RouterLink, Icon],
  template: `
    <ng-template #body>
      <div class="flex items-start justify-between gap-4">
        <div class="min-w-0 space-y-1">
          <p class="text-muted-foreground text-sm font-medium">{{ title() }}</p>
          <p class="text-foreground text-3xl font-bold tracking-tight tabular-nums">
            {{ value() }}
          </p>
          @if (subtitle(); as subtitle) {
            <p class="text-muted-foreground text-xs">{{ subtitle }}</p>
          }
        </div>

        <span
          class="bg-primary/10 text-primary flex size-12 shrink-0 items-center justify-center rounded-xl transition-transform duration-300 group-hover:scale-110"
        >
          <cd-icon [name]="icon()" size="1.5rem" />
        </span>
      </div>

      <!-- The prototype's corner glow. Decorative, so it is hidden from assistive tech. -->
      <span
        aria-hidden="true"
        class="bg-primary/5 pointer-events-none absolute -right-1 -bottom-1 size-24 rounded-full opacity-0 blur-2xl transition-opacity duration-500 group-hover:opacity-100"
      ></span>
    </ng-template>

    @if (route(); as route) {
      <a
        [routerLink]="route"
        class="border-border bg-card hover:border-primary/30 hover:shadow-primary/5 focus-visible:ring-ring group relative block overflow-hidden rounded-xl border p-5 transition-all duration-300 hover:shadow-lg focus-visible:ring-2 focus-visible:outline-none"
      >
        <ng-container [ngTemplateOutlet]="body" />
      </a>
    } @else {
      <div
        class="border-border bg-card hover:border-primary/30 hover:shadow-primary/5 group relative block overflow-hidden rounded-xl border p-5 transition-all duration-300 hover:shadow-lg"
      >
        <ng-container [ngTemplateOutlet]="body" />
      </div>
    }
  `,
})
export class StatCard {
  readonly title = input.required<string>();
  readonly value = input.required<string | number>();
  readonly icon = input.required<IconName>();
  readonly subtitle = input<string | null>(null);

  /** Where the card leads, or null for a card that only reports. */
  readonly route = input<string | null>(null);
}
