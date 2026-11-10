import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  DOCUMENT,
  type ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { HlmButton } from '@spartan-ng/helm/button';
import { placeCard, sheetEdge, spotlightBox, type Box, type Placement } from './tour-placement';

let nextId = 0;

/**
 * One step of the guided tour (ADR-051): a card, and a ring around the thing it is about.
 *
 * <b>Non-modal, and that is the point of every choice below.</b> A tour is an offer, so it
 * must never be the thing between a person and the page:
 *
 * - The ring and the dimming around it are `pointer-events: none`, so the page underneath
 *   still takes clicks. The ring is box-shadow on one element, not an overlay over the page.
 * - Focus is not trapped. Opening moves focus to the card, so a keyboard or screen reader user
 *   arrives at it, and Tab then goes on through the card's buttons and out into the page like
 *   anywhere else. There is no `aria-modal`, no `inert` on the page, and no key handler on Tab.
 * - Escape skips the tour, but only when focus is on the card or on nothing in particular: an
 *   Escape that closes something else (a menu, the editor) is that thing's, not the tour's.
 * - Each step is announced from a polite live region, so a screen reader hears the new step
 *   while focus stays on the Next button the person just pressed. The card is a `dialog` named
 *   by the step's title and described by its text, which is what is read when it first opens.
 *
 * <b>Beside its target on a wide window, a sheet along the bottom on a narrow one.</b> A card
 * 20rem wide cannot sit beside a row of a rail that is 4rem from the edge of a phone without
 * covering the page, so it takes the width of the window and the foot of it, where a thumb is.
 * With no target at all, it is centred and says its piece without pointing.
 *
 * Positioned with the CSSOM (`[style.left.px]` is `element.style.left = ...`), which the app's
 * Content-Security-Policy permits in the way it forbids a `style="..."` attribute in markup,
 * so nothing here needs the policy loosened. Presentational: what the steps are, and what
 * happens at the end of them, is the shell's.
 */
@Component({
  selector: 'cd-tour-popover',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmButton],
  host: { '(document:keydown.escape)': 'onEscape($event)' },
  template: `
    @if (ring(); as box) {
      <!--
        The spotlight: a ring, and the page dimmed around it by a very large shadow. Decorative
        and inert, so it takes no clicks and a screen reader does not meet it.
      -->
      <div
        aria-hidden="true"
        data-tour-ring
        class="pointer-events-none fixed z-40 rounded-lg shadow-[0_0_0_2px_var(--ring),0_0_0_9999px_rgb(0_0_0/0.4)] motion-safe:transition-all motion-safe:duration-200"
        [style.left.px]="box.left"
        [style.top.px]="box.top"
        [style.width.px]="box.width"
        [style.height.px]="box.height"
      ></div>
    }

    <div
      #card
      role="dialog"
      tabindex="-1"
      [attr.aria-labelledby]="uid + '-title'"
      [attr.aria-describedby]="uid + '-body'"
      data-tour-card
      class="bg-popover text-popover-foreground border-border focus-visible:ring-ring z-50 space-y-3 rounded-xl border p-4 shadow-lg focus:outline-none focus-visible:ring-2"
      [class]="layout()"
      [class.opacity-0]="!ready() || (anchored() && position() === null)"
      [style.left.px]="anchored() ? position()?.left : null"
      [style.top.px]="anchored() ? position()?.top : null"
    >
      <p class="text-muted-foreground text-xs">Step {{ step() }} of {{ total() }}</p>
      <h2 [id]="uid + '-title'" class="text-base leading-snug font-semibold">{{ title() }}</h2>
      <p [id]="uid + '-body'" class="text-muted-foreground text-sm leading-relaxed">
        {{ body() }}
      </p>

      <div class="flex items-center justify-between gap-2 pt-1">
        <button type="button" hlmBtn variant="ghost" size="sm" (click)="skipped.emit()">
          Skip tour
        </button>

        <div class="flex items-center gap-2">
          @if (step() > 1) {
            <button type="button" hlmBtn variant="outline" size="sm" (click)="previous.emit()">
              Back
            </button>
          }
          <button type="button" hlmBtn size="sm" (click)="advanced.emit()">
            {{ step() === total() ? 'Done' : 'Next' }}
          </button>
        </div>
      </div>
    </div>

    <!--
      What a screen reader hears when the step changes. Empty while the first step opens,
      because opening focuses the dialog, whose name and description say the same thing and
      would otherwise be read twice.
    -->
    <div class="sr-only" role="status" aria-live="polite" aria-atomic="true">
      {{ announcement() }}
    </div>
  `,
})
export class TourPopover {
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);

  protected readonly uid = `cd-tour-${++nextId}`;

  readonly title = input.required<string>();
  readonly body = input.required<string>();
  /** One-based. */
  readonly step = input.required<number>();
  readonly total = input.required<number>();
  /** What the step points at; null for a card that has nothing to point at. */
  readonly target = input<HTMLElement | null>(null);
  /** The window is narrow: a sheet along the bottom, not a card beside the target. */
  readonly sheet = input(false);

  readonly advanced = output<void>();
  readonly previous = output<void>();
  readonly skipped = output<void>();

  private readonly card = viewChild.required<ElementRef<HTMLElement>>('card');

  /**
   * What was measured, or null before the first measurement: the target's box in viewport
   * coordinates and where the card goes beside it. One signal, so the ring and the card are
   * never drawn from two different moments.
   */
  private readonly measured = signal<{
    box: Box | null;
    position: Placement | null;
    edge: 'top' | 'bottom';
  } | null>(null);

  private readonly targetBox = computed(() => this.measured()?.box ?? null);
  protected readonly position = computed(() => this.measured()?.position ?? null);
  private readonly edge = computed(() => this.measured()?.edge ?? 'bottom');
  protected readonly announcement = signal('');

  /**
   * Whether the first measurement has been made. Until then the card is laid out but not seen:
   * its first paint would be centred, because nothing yet says where the target is, and then
   * jump to the target. Transparent and not `visibility: hidden`, which would also refuse the
   * focus that opening gives it.
   */
  protected readonly ready = computed(() => this.measured() !== null);

  protected readonly ring = computed(() => {
    const box = this.targetBox();
    return box ? spotlightBox(box) : null;
  });

  /** Beside the target, which is the layout that needs coordinates. */
  protected readonly anchored = computed(() => !this.sheet() && this.targetBox() !== null);

  protected readonly layout = computed(() => {
    if (this.sheet()) {
      return this.edge() === 'top' ? 'fixed inset-x-2 top-2' : 'fixed inset-x-2 bottom-2';
    }

    return this.anchored()
      ? 'fixed w-80'
      : 'fixed top-1/2 left-1/2 w-80 -translate-x-1/2 -translate-y-1/2';
  });

  constructor() {
    // Opening moves focus here, so that a person using a keyboard or a screen reader arrives at
    // the tour. Later steps leave it where it is (see below).
    afterNextRender(() => this.card().nativeElement.focus({ preventScroll: true }));

    let opened = false;

    effect(() => {
      // Read, so a new step re-runs this.
      const text = `Step ${this.step()} of ${this.total()}: ${this.title()}. ${this.body()}`;

      untracked(() => {
        if (!opened) {
          opened = true;
          return;
        }

        this.announcement.set(text);
        afterNextRender(() => this.keepFocus(), { injector: this.injector });
      });
    });

    // Position again whenever what it is beside, or the layout, changes.
    effect(() => {
      this.target();
      this.sheet();
      this.title();
      untracked(() => afterNextRender(() => this.measure(), { injector: this.injector }));
    });

    this.followTheWindow();
  }

  /**
   * Puts focus back on the card if the button that had it is gone.
   *
   * Back is not drawn on the first step, so pressing it on the second removes the focused
   * element and the browser drops focus onto the page. Focus that is anywhere else on purpose
   * (the person clicked into the page) is left alone.
   */
  private keepFocus(): void {
    const active = this.document.activeElement;
    const card = this.card().nativeElement;

    if (!active || active === this.document.body || !active.isConnected) {
      card.focus({ preventScroll: true });
    }
  }

  protected onEscape(event: Event): void {
    if (event.defaultPrevented) {
      return;
    }

    const active = this.document.activeElement;
    const onCard = active !== null && this.card().nativeElement.contains(active);

    if (onCard || !active || active === this.document.body) {
      this.skipped.emit();
    }
  }

  /** Reads the target's box and the card's size, and decides where the card goes. */
  private measure(): void {
    const target = this.target();
    const view = this.document.defaultView;

    if (!target || !view) {
      this.measured.set({ box: null, position: null, edge: 'bottom' });
      return;
    }

    const rect = target.getBoundingClientRect();
    const box = { left: rect.left, top: rect.top, width: rect.width, height: rect.height };

    if (this.sheet()) {
      this.measured.set({ box, position: null, edge: sheetEdge(box, view.innerHeight) });
      return;
    }

    const card = this.card().nativeElement;
    this.measured.set({
      box,
      edge: 'bottom',
      position: placeCard(
        box,
        { width: card.offsetWidth, height: card.offsetHeight },
        { width: view.innerWidth, height: view.innerHeight },
      ),
    });
  }

  /**
   * Keeps the ring and the card on the target as the window is resized and anything scrolls.
   *
   * `scroll` does not bubble, so it is listened for in the capture phase to catch a scroll
   * inside a panel and not only of the page. Coalesced into one measurement per frame.
   */
  private followTheWindow(): void {
    const view = this.document.defaultView;
    if (!view) {
      return;
    }

    let queued = false;
    const schedule = () => {
      if (queued) {
        return;
      }
      queued = true;
      view.requestAnimationFrame(() => {
        queued = false;
        this.measure();
      });
    };

    view.addEventListener('resize', schedule);
    this.document.addEventListener('scroll', schedule, true);

    // The sidebar animates its width, and a target can move without the window doing anything.
    const observer =
      typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(() => schedule());

    effect(() => {
      const target = this.target();
      observer?.disconnect();
      if (target) {
        observer?.observe(target);
      }
    });

    inject(DestroyRef).onDestroy(() => {
      view.removeEventListener('resize', schedule);
      this.document.removeEventListener('scroll', schedule, true);
      observer?.disconnect();
    });
  }
}
