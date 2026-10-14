import { expect, type Locator, type Page } from '@playwright/test';

/**
 * What the compatibility explanations are checked by, in the specs that visit a screen showing one.
 *
 * The wording itself is `COMPATIBILITY_*_MEANING` in the app's domain, imported by the specs from
 * there rather than retyped, so the one place it is written is also the one place a spec reads it
 * from. The unit specs pin that constant against the token lists; these pin that a real page, in a
 * real browser, puts it where a keyboard, a thumb and a screen reader can reach it.
 */

/** Loads the next page in the appearance a person chose, which the app reads before it paints. */
export async function chooseAppearance(page: Page, appearance: 'light' | 'dark'): Promise<void> {
  await page.addInitScript((value) => {
    try {
      localStorage.setItem('concordat.appearance', value);
    } catch {
      /* A context that refuses storage falls back to the system setting, which is light here. */
    }
  }, appearance);
}

/**
 * Opens a disclosure from the keyboard, the way somebody who cannot point does: focus it, press the
 * key. A `click()` would pass for a `div` with a handler, which is exactly the control this
 * replaces `title` with.
 */
export async function openByKeyboard(
  page: Page,
  button: Locator,
  key: 'Enter' | 'Space' = 'Enter',
): Promise<void> {
  await button.focus();
  await expect(button).toBeFocused();
  await expect(button).toHaveAttribute('aria-expanded', 'false');
  await page.keyboard.press(key);
  await expect(button).toHaveAttribute('aria-expanded', 'true');
}

/**
 * That nothing hangs off the right edge of the screen, and the page does not scroll sideways.
 *
 * Both, because they fail separately: a long unbroken token can overflow its own box while the
 * page's scroll width is hidden by an ancestor, and a page can scroll sideways with every box
 * inside it. The viewport is whatever the test set.
 */
export async function expectNoSidewaysOverflow(page: Page, inside: Locator): Promise<void> {
  const viewport = page.viewportSize()!.width;

  const widths = await page.evaluate(() => ({
    document: document.documentElement.scrollWidth,
    body: document.body.scrollWidth,
  }));
  expect(widths.document).toBeLessThanOrEqual(viewport);
  expect(widths.body).toBeLessThanOrEqual(viewport);

  const boxes = await inside.evaluateAll((elements) =>
    elements.map((element) => {
      const box = element.getBoundingClientRect();

      return {
        text: (element.textContent ?? '').trim().slice(0, 30),
        left: box.left,
        right: box.right,
      };
    }),
  );
  expect(boxes.length).toBeGreaterThan(0);

  for (const box of boxes) {
    expect(box.left, box.text).toBeGreaterThanOrEqual(0);
    expect(box.right, box.text).toBeLessThanOrEqual(viewport + 0.5);
  }
}

/**
 * The WCAG contrast ratio of each element's text against what is really behind it.
 *
 * <b>Measured by painting.</b> The panel's background is the page's colour tinted with a
 * translucent token, and a computed style may be spelled in any colour syntax the browser likes, so
 * the ratio is not taken from a string. Each ancestor's background is painted, outermost first, onto
 * one canvas pixel, and the browser does the blending and the parsing; the text colour is painted
 * over it. Light and dark are two different pairs of tokens, and a ratio that holds in one says
 * nothing about the other.
 */
export async function contrastOf(locator: Locator): Promise<number[]> {
  return locator.evaluateAll((elements) => {
    const canvas = document.createElement('canvas');
    canvas.width = 1;
    canvas.height = 1;
    const context = canvas.getContext('2d', { willReadFrequently: true })!;

    const paint = (colours: readonly string[]): [number, number, number] => {
      context.clearRect(0, 0, 1, 1);

      for (const colour of colours) {
        context.fillStyle = colour;
        context.fillRect(0, 0, 1, 1);
      }

      const [r, g, b] = context.getImageData(0, 0, 1, 1).data;

      return [r ?? 0, g ?? 0, b ?? 0];
    };

    const luminance = ([r, g, b]: readonly number[]): number => {
      const channel = (value: number) => {
        const unit = value / 255;

        return unit <= 0.04045 ? unit / 12.92 : ((unit + 0.055) / 1.055) ** 2.4;
      };

      return 0.2126 * channel(r ?? 0) + 0.7152 * channel(g ?? 0) + 0.0722 * channel(b ?? 0);
    };

    return elements.map((element) => {
      const backgrounds: string[] = [];

      for (let node: Element | null = element; node !== null; node = node.parentElement) {
        backgrounds.unshift(getComputedStyle(node).backgroundColor);
      }

      const behind = luminance(paint(backgrounds));
      const text = luminance(paint([...backgrounds, getComputedStyle(element).color]));
      const [lighter, darker] = text > behind ? [text, behind] : [behind, text];

      return (lighter + 0.05) / (darker + 0.05);
    });
  });
}
