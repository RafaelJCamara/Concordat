import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { HlmButton } from '@spartan-ng/helm/button';
import { Icon } from '../icon/icon';

/** One place in the row of page buttons: a page, or the ellipsis that stands for the ones between. */
export type PageItem =
  { readonly kind: 'page'; readonly page: number } | { readonly kind: 'gap'; readonly key: string };

/** How many page buttons the row has once there are more pages than fit: two ends, three around the current, two gaps. */
const FULL_ROW = 7;

/**
 * The page buttons to draw: always the first and the last, and the pages around the current one,
 * with an ellipsis where pages are left out.
 *
 * At most {@link FULL_ROW} places however long the list is, and **the same number of places wherever
 * the current page is**, so that the row does not change width under the pointer as somebody clicks
 * along it (a row that grew at one end and shrank at the other would move the button about to be
 * clicked). The ends are always reachable, which is the point of numbering them at all: a list of a
 * hundred pages is jumped around, not walked.
 */
export function pageItems(current: number, count: number): readonly PageItem[] {
  const page = (n: number): PageItem => ({ kind: 'page', page: n });
  const range = (from: number, to: number): PageItem[] =>
    Array.from({ length: to - from + 1 }, (_, i) => page(from + i));

  if (count <= FULL_ROW) {
    return range(1, count);
  }

  if (current <= 4) {
    return [...range(1, 5), { kind: 'gap', key: 'after' }, page(count)];
  }

  if (current >= count - 3) {
    return [page(1), { kind: 'gap', key: 'before' }, ...range(count - 4, count)];
  }

  return [
    page(1),
    { kind: 'gap', key: 'before' },
    ...range(current - 1, current + 1),
    { kind: 'gap', key: 'after' },
    page(count),
  ];
}

/**
 * Page buttons for a list read a page at a time: previous, the numbered pages, next.
 *
 * Presentational: it knows which page is current and how many there are, and says which one was
 * asked for. It does not know what is being paged, where the current page is kept, or how a page is
 * fetched. The screen puts the page in the address (ADR-052), and this is what it asks for the click.
 *
 * <b>Buttons, and not links.</b> A page here is a change of the screen's address that the screen makes,
 * not a place with an address of its own to open in a tab; the same URL a link would carry is in the
 * address bar after the click.
 *
 * <b>A page the list cannot go to is still a button, and still focusable.</b> It is `aria-disabled`,
 * not `disabled`: pressing Next on the second to last page moves the focus onto a Next that is
 * then dead, and a `disabled` button drops the focus to the page body, which for a keyboard user
 * is the top of the screen. The click is ignored instead.
 *
 * <b>On a phone the words go and the arrows stay</b> (*Previous* and *Next* are still the buttons' names, for a
 * screen reader), the buttons and the gaps between them narrow, and the seven places of the row fit the width of
 * a 390 px screen's content in one line; a row of larger numbers wraps and stays centred rather than overflowing.
 *
 * <b>Said without colour and without the numbers.</b> The current page is `aria-current="page"` and the
 * numbered buttons are named "Page 4", so a screen reader hears where it is and what each one does; the
 * ellipsis is hidden from it. With no page count (`pageCount` null, because the registry did not send
 * a total) there are no numbers to draw: only the page the reader is on, and a Next that is on while the
 * page was full.
 */
@Component({
  selector: 'cd-pagination',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmButton, Icon],
  template: `
    <nav
      [attr.aria-label]="label()"
      class="flex flex-wrap items-center justify-center gap-0.5 sm:gap-1"
    >
      <button
        type="button"
        hlmBtn
        variant="outline"
        size="sm"
        class="aria-disabled:pointer-events-none aria-disabled:opacity-50"
        [attr.aria-disabled]="hasPrevious() ? null : 'true'"
        data-testid="pager-previous"
        (click)="go(page() - 1, hasPrevious())"
      >
        <cd-icon name="chevron-left" size="0.875rem" />
        <span class="sr-only sm:not-sr-only">Previous</span>
      </button>

      @if (pageCount() === null) {
        <span
          class="text-foreground px-2 text-sm font-medium"
          aria-current="page"
          data-testid="pager-current"
        >
          Page {{ page() }}
        </span>
      } @else {
        @for (item of items(); track item.kind === 'page' ? item.page : item.key) {
          @if (item.kind === 'gap') {
            <span aria-hidden="true" class="text-muted-foreground w-4 text-center text-sm sm:w-6"
              >…</span
            >
          } @else {
            <button
              type="button"
              hlmBtn
              size="sm"
              class="min-w-7 px-2 sm:min-w-8 sm:px-2.5"
              [variant]="item.page === page() ? 'default' : 'outline'"
              [attr.aria-current]="item.page === page() ? 'page' : null"
              [attr.aria-label]="'Page ' + item.page"
              (click)="go(item.page, item.page !== page())"
            >
              {{ item.page }}
            </button>
          }
        }
      }

      <button
        type="button"
        hlmBtn
        variant="outline"
        size="sm"
        class="aria-disabled:pointer-events-none aria-disabled:opacity-50"
        [attr.aria-disabled]="canGoNext() ? null : 'true'"
        data-testid="pager-next"
        (click)="go(page() + 1, canGoNext())"
      >
        <span class="sr-only sm:not-sr-only">Next</span>
        <cd-icon name="chevron-right" size="0.875rem" />
      </button>
    </nav>
  `,
})
export class Pagination {
  /** The current page, 1-based. */
  readonly page = input.required<number>();

  /** How many pages there are, or null when it is not known. */
  readonly pageCount = input.required<number | null>();

  /** Whether the list goes on after this page. Only read when the page count is not known. */
  readonly hasNext = input(false);

  /** The name of the landmark, for a screen that has more than one. */
  readonly label = input('Pagination');

  /** The page the reader asked for. Never the current one, and never one the list does not have. */
  readonly pageChange = output<number>();

  protected readonly items = computed(() => {
    const count = this.pageCount();

    return count === null ? [] : pageItems(this.page(), count);
  });

  protected readonly hasPrevious = computed(() => this.page() > 1);

  protected readonly canGoNext = computed(() => {
    const count = this.pageCount();

    return count === null ? this.hasNext() : this.page() < count;
  });

  protected go(target: number, allowed: boolean): void {
    if (allowed) {
      this.pageChange.emit(target);
    }
  }
}
