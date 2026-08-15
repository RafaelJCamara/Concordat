import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { Icon, type IconName } from '../icon/icon';

/**
 * One entry in the sidebar's navigation.
 *
 * <b>Every item is a route that exists.</b> The prototype's rail has five rows and this one
 * has two, and the gap was briefly filled with inert rows marked "Soon" for the screens M4.3
 * plans. That is reverted, deliberately: matching a mockup's row count with placeholders is
 * imitating the screenshot rather than the design, and what makes this rail recognisable is
 * the gradient, the tinted logo tile, the active pill and the footer — none of which depend
 * on how many links sit between them.
 *
 * The cost of each answer is not symmetric. A sparse sidebar is cosmetic and ends the day
 * M4.3 lands; a row you can see and cannot use is a small recurring lie that every visitor
 * pays for on every visit, and the roadmap it encodes is already written down in
 * `docs/plan/M4-web-app.md`, where it cannot go stale in two places at once. Adding rows
 * back as screens ship is a one-line change; taking shipped navigation away is one users
 * notice.
 *
 * This is not ADR-018, which is about affordances a *permission* withholds and says nothing
 * about unbuilt ones. It lands in the same place for a different reason.
 */
export interface NavItem {
  readonly label: string;
  readonly icon: IconName;
  readonly route: string;
}

/**
 * The application's left rail.
 *
 * Ported from the prototype's `AppSidebar`: the gradient ground, the 8×8 tinted logo tile,
 * the rounded active row with its pulsing dot, and the collapse toggle pinned to the
 * bottom. The proportions are the prototype's — `w-64` open, `w-16` closed, a 16-unit
 * header that lines its baseline up with the first heading in the main column.
 *
 * Presentational, and deliberately so even though exactly one component will ever render
 * it: `shared/ui` is the layer that gets restyled, and the moment one component in it
 * injects a store, "shared" stops being true. The shell owns the wiring, passes the nav
 * list in, and handles what comes back out.
 *
 * <b>Collapsing is the component's own business and stays here.</b> It is view state — it
 * changes nothing a reload or another tab would need to agree about — so lifting it to a
 * store would buy a subscription and a persisted key for something a signal already does.
 */
@Component({
  selector: 'cd-app-sidebar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, RouterLinkActive, Icon],
  host: {
    // `h-dvh` and not `h-screen`: on mobile Safari `100vh` is the height the viewport has
    // when the URL bar is hidden, so the collapse toggle sits below the fold until you
    // scroll — on a pane that does not scroll.
    class:
      'border-sidebar-border sticky top-0 flex h-dvh flex-col border-e transition-[width] duration-300 ease-in-out',
    '[class.w-64]': '!collapsed()',
    '[class.w-16]': 'collapsed()',
    '[style.background]': '"var(--gradient-sidebar)"',
  },
  template: `
    <!--
      Wordmark. A link to the root, so the logo does what every logo is expected to do.

      The focus ring here and on every control below is the app's own ring token, spelled the
      way the cards, inputs and table links spell it. Left off, these fell back to the
      browser's default outline — visible, so nothing looked broken, and a different shape and
      colour from every other stop on the same tab order.
    -->
    <a
      routerLink="/"
      class="border-sidebar-border hover:bg-sidebar-accent focus-visible:ring-ring flex h-16 items-center gap-3 border-b px-4 transition-colors focus-visible:ring-2 focus-visible:outline-none"
    >
      <span
        class="bg-primary/10 text-primary flex size-8 shrink-0 items-center justify-center rounded-lg"
      >
        <cd-icon name="rabbit" size="1.25rem" />
      </span>

      @if (!collapsed()) {
        <span class="animate-fade-in min-w-0">
          <span class="text-foreground block leading-tight font-semibold">{{ product() }}</span>
          <span class="text-muted-foreground block text-xs leading-tight">Schema registry</span>
        </span>
      }
    </a>

    <nav aria-label="Main" class="flex-1 space-y-1 px-2 py-4">
      @for (item of items(); track item.label) {
        <a
          [routerLink]="item.route"
          routerLinkActive="bg-primary/10 !text-primary"
          #active="routerLinkActive"
          [routerLinkActiveOptions]="{ exact: item.route === '/' }"
          [attr.aria-current]="active.isActive ? 'page' : null"
          [attr.title]="collapsed() ? item.label : null"
          class="text-sidebar-foreground hover:bg-sidebar-accent focus-visible:ring-ring group flex items-center gap-3 rounded-lg px-3 py-2.5 transition-all duration-200 focus-visible:ring-2 focus-visible:outline-none"
          [class.justify-center]="collapsed()"
        >
          <cd-icon
            [name]="item.icon"
            size="1.25rem"
            class="group-hover:text-foreground transition-colors"
            [class.text-primary]="active.isActive"
            [class.text-muted-foreground]="!active.isActive"
          />

          @if (!collapsed()) {
            <span class="animate-fade-in font-medium">{{ item.label }}</span>

            @if (active.isActive) {
              <!--
                The prototype's active marker. Decorative and aria-hidden, because
                aria-current above already says this to a screen reader — and saying it
                twice is how "Dashboard, current page, bullet" happens.
              -->
              <span
                aria-hidden="true"
                class="bg-primary animate-pulse-dot ms-auto size-1.5 rounded-full"
              ></span>
            }
          }
        </a>
      }
    </nav>

    <!--
      Account, environment and theme. The prototype has no top bar and no account controls
      at all; putting them here rather than inventing one keeps the main column starting on
      the page heading, which is the single most recognisable thing about the layout.
    -->
    <div class="border-sidebar-border space-y-1 border-t px-2 py-2">
      <ng-content select="[cdSidebarFooter]" />
    </div>

    <div class="border-sidebar-border border-t p-2">
      <button
        type="button"
        class="text-muted-foreground hover:text-foreground hover:bg-sidebar-accent focus-visible:ring-ring flex w-full items-center justify-center gap-2 rounded-lg px-3 py-2 text-sm transition-colors focus-visible:ring-2 focus-visible:outline-none"
        [attr.aria-label]="collapsed() ? 'Expand sidebar' : 'Collapse sidebar'"
        [attr.aria-expanded]="!collapsed()"
        (click)="toggled.emit()"
      >
        <cd-icon [name]="collapsed() ? 'chevron-right' : 'chevron-left'" size="1rem" />
        @if (!collapsed()) {
          <span>Collapse</span>
        }
      </button>
    </div>
  `,
})
export class AppSidebar {
  readonly items = input.required<readonly NavItem[]>();
  readonly collapsed = input<boolean>(false);

  /** The wordmark. An input so the one place the product is named is the shell. */
  readonly product = input<string>('Concordat');

  readonly toggled = output<void>();
}
