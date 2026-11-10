import { describe, expect, it } from 'vitest';
import { GAP, MARGIN, placeCard, sheetEdge, spotlightBox } from './tour-placement';

// The card must always end up where its buttons can be reached. These are the numbers behind
// that, on the two windows that matter: a desktop one and a phone-sized one.

const CARD = { width: 320, height: 180 };
const DESKTOP = { width: 1280, height: 800 };

/** A row of the sidebar: 224 wide, 44 tall, at `top`. */
const row = (top: number) => ({ left: 16, top, width: 224, height: 44 });

describe('placeCard', () => {
  it('puts the card to the right of a sidebar row, centred on it', () => {
    const target = row(300);

    expect(placeCard(target, CARD, DESKTOP)).toEqual({
      left: 16 + 224 + GAP,
      top: 300 + 22 - 90,
    });
  });

  it('keeps the card inside the top of the window for a row near the top', () => {
    expect(placeCard(row(0), CARD, DESKTOP).top).toBe(MARGIN);
  });

  it('keeps the card inside the bottom of the window for a row near the bottom', () => {
    const placed = placeCard(row(780), CARD, DESKTOP);

    expect(placed.top).toBe(DESKTOP.height - CARD.height - MARGIN);
    expect(placed.top + CARD.height).toBeLessThanOrEqual(DESKTOP.height - MARGIN);
  });

  it('goes below a target that has no room on its right, aligned to its left edge', () => {
    // A tab across the top of the page, wide enough that the card does not fit beside it.
    const tab = { left: 700, top: 120, width: 560, height: 36 };

    expect(placeCard(tab, CARD, DESKTOP)).toEqual({ left: 700, top: 120 + 36 + GAP });
  });

  it('pulls a card that would hang off the right edge back inside it', () => {
    const tab = { left: 1200, top: 120, width: 70, height: 36 };
    const placed = placeCard(tab, CARD, DESKTOP);

    expect(placed.left).toBe(DESKTOP.width - CARD.width - MARGIN);
    expect(placed.left + CARD.width).toBeLessThanOrEqual(DESKTOP.width - MARGIN);
  });

  it('goes to the left when there is room neither beside nor below', () => {
    const target = { left: 900, top: 760, width: 360, height: 36 };

    // Centred on the target it would be at 688, which hangs off the bottom, so it ends at the margin.
    expect(placeCard(target, CARD, DESKTOP)).toEqual({ left: 568, top: 612 });
  });

  it('goes above when only above has room', () => {
    const phone = { width: 390, height: 600 };
    const target = { left: 8, top: 560, width: 374, height: 32 };

    expect(placeCard(target, { width: 300, height: 120 }, phone)).toEqual({
      left: 8,
      top: 560 - GAP - 120,
    });
  });

  it('never leaves the window, whatever it is given', () => {
    const phone = { width: 390, height: 600 };
    const card = { width: 300, height: 250 };

    for (const top of [0, 100, 300, 500, 580]) {
      for (const left of [0, 100, 300, 380]) {
        const placed = placeCard({ left, top, width: 40, height: 40 }, card, phone);

        expect(placed.left).toBeGreaterThanOrEqual(MARGIN);
        expect(placed.top).toBeGreaterThanOrEqual(MARGIN);
        expect(placed.left + card.width).toBeLessThanOrEqual(phone.width - MARGIN);
        expect(placed.top + card.height).toBeLessThanOrEqual(phone.height - MARGIN);
      }
    }
  });
});

describe('spotlightBox', () => {
  it('grows the target a little on every side so the ring clears its text', () => {
    expect(spotlightBox({ left: 16, top: 100, width: 224, height: 44 })).toEqual({
      left: 12,
      top: 96,
      width: 232,
      height: 52,
    });
  });

  it('grows it by the padding it is given', () => {
    expect(spotlightBox({ left: 10, top: 10, width: 10, height: 10 }, 0)).toEqual({
      left: 10,
      top: 10,
      width: 10,
      height: 10,
    });
  });
});

describe('sheetEdge', () => {
  const phone = 844;

  it('is the bottom, where a thumb is, for a target in the upper half', () => {
    expect(sheetEdge({ left: 0, top: 100, width: 64, height: 44 }, phone)).toBe('bottom');
  });

  it('is the top for a target in the lower half, so the sheet is not on what it describes', () => {
    expect(sheetEdge({ left: 0, top: 700, width: 64, height: 44 }, phone)).toBe('top');
  });

  it('judges by the middle of the target, not its top', () => {
    // 410 + 22 = 432 is past half of 844 (422), though the top edge is above it.
    expect(sheetEdge({ left: 0, top: 410, width: 64, height: 44 }, phone)).toBe('top');
    expect(sheetEdge({ left: 0, top: 390, width: 64, height: 44 }, phone)).toBe('bottom');
  });
});
