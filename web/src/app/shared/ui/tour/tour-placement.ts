// Where the tour's card goes, with no Angular and no DOM in it (ADR-051).
//
// Arithmetic over boxes, so the rule that keeps the card on screen is asserted on numbers and
// not on a browser's layout.

/** A rectangle in viewport coordinates. */
export interface Box {
  readonly left: number;
  readonly top: number;
  readonly width: number;
  readonly height: number;
}

/** Where to put the card's top left corner. */
export interface Placement {
  readonly left: number;
  readonly top: number;
}

/** The space between the target and the card, enough for the spotlight's ring to show. */
export const GAP = 12;

/** The least space between the card and the edge of the window. */
export const MARGIN = 8;

/** The largest of `value` and `low`, but no larger than `high` — and `low` when they cross. */
function clamp(value: number, low: number, high: number): number {
  return Math.max(low, Math.min(value, high));
}

/**
 * The card's position beside its target.
 *
 * <b>Right of the target first, because the targets are mostly the sidebar's rows</b> and a card
 * on the right reads as the row's explanation without covering the row or the rows around it.
 * Below next (the tab bar across the settings screens), then left, then above. The first side
 * with room for the whole card wins; if none has, the card goes below and is pulled back into
 * the window, because a card that is partly off screen has buttons nobody can reach.
 *
 * The card is aligned to the target along the other axis and then clamped, so a target near
 * the bottom edge gets a card that ends at the margin and not one that hangs off it.
 */
export function placeCard(
  target: Box,
  card: Pick<Box, 'width' | 'height'>,
  viewport: Pick<Box, 'width' | 'height'>,
): Placement {
  const centreY = target.top + target.height / 2 - card.height / 2;
  const maxLeft = viewport.width - card.width - MARGIN;
  const maxTop = viewport.height - card.height - MARGIN;

  const right = target.left + target.width + GAP;
  if (right + card.width <= viewport.width - MARGIN) {
    return { left: right, top: clamp(centreY, MARGIN, maxTop) };
  }

  const below = target.top + target.height + GAP;
  if (below + card.height <= viewport.height - MARGIN) {
    return { left: clamp(target.left, MARGIN, maxLeft), top: below };
  }

  const left = target.left - GAP - card.width;
  if (left >= MARGIN) {
    return { left, top: clamp(centreY, MARGIN, maxTop) };
  }

  const above = target.top - GAP - card.height;
  if (above >= MARGIN) {
    return { left: clamp(target.left, MARGIN, maxLeft), top: above };
  }

  return { left: clamp(target.left, MARGIN, maxLeft), top: clamp(below, MARGIN, maxTop) };
}

/**
 * The ring drawn around a target: its box, grown a little so the ring does not touch the
 * text it surrounds.
 */
export function spotlightBox(target: Box, padding = 4): Box {
  return {
    left: target.left - padding,
    top: target.top - padding,
    width: target.width + padding * 2,
    height: target.height + padding * 2,
  };
}

/**
 * Which edge of the window the sheet sits along on a narrow screen.
 *
 * The bottom, where a thumb is, unless that is where the target is: the foot of the rail holds
 * the account and the tour's own button, and a sheet along the bottom would sit on the very
 * thing it is describing. A target in the lower half sends the sheet to the top.
 */
export function sheetEdge(target: Box, viewportHeight: number): 'top' | 'bottom' {
  return target.top + target.height / 2 > viewportHeight / 2 ? 'top' : 'bottom';
}
