import { expect, type Locator, type Page } from '@playwright/test';

/**
 * The schema editor, as a test sees it.
 *
 * One place that knows what the editor is, so a spec says "put this text in" and does not care
 * whether Monaco or the textarea it falls back to is behind it.
 *
 * <b>Monaco is not a textarea and `fill` does not work on it.</b> Chromium gives it an
 * `EditContext` element — a `div` — which Playwright refuses to fill. Text goes in by selecting
 * what is there and inserting, which is how a paste arrives, and comes back out of the rendered
 * lines. That read is deliberately done from the DOM rather than from a hook in the product: a
 * `window.monaco` that exists so a test can call `getValue()` is a global in production that
 * exists so a test can call `getValue()`.
 */
const RICH = '.monaco-editor';

/** The element a person types into, whichever editor it is. */
export function schemaEditor(page: Page): Locator {
  return page.getByRole('textbox', { name: 'Schema document' });
}

/** Whether Monaco has taken over from the textarea. */
export async function isRich(page: Page): Promise<boolean> {
  return (await page.locator(RICH).count()) > 0;
}

/** Lets zoneless change detection flush, so what is on screen reflects the text just entered. */
export async function settle(page: Page): Promise<void> {
  await page.evaluate(
    () =>
      new Promise<void>((resolve) =>
        requestAnimationFrame(() => requestAnimationFrame(() => resolve())),
      ),
  );
}

/**
 * Waits until the editor is what it is going to be: Monaco, or — if it could not load — the
 * textarea with its notice. Without this a test can start typing into the textarea in the
 * instant Monaco replaces it.
 */
export async function waitForEditor(page: Page): Promise<void> {
  await expect(
    page
      .locator(RICH)
      .or(page.getByText(/could not be loaded, so this is the plain editor/))
      .first(),
  ).toBeVisible({ timeout: 20_000 });
}

/** Waits for the page to have read its subject and for the editor to have settled. */
export async function waitForSubject(page: Page): Promise<void> {
  await expect(page.getByRole('heading', { name: 'Register a version' })).toBeVisible();
  // The "Start from" control and the "No versions yet" panel are both drawn only once the
  // subject has loaded, so either one is the signal.
  await expect(page.locator('cd-version-starter')).toBeVisible();
  await waitForEditor(page);
}

/** Puts text in the editor, replacing whatever is there, and leaves the editor. */
export async function setSchema(page: Page, text: string): Promise<void> {
  if (await isRich(page)) {
    await page.locator(RICH).first().click();
    await page.keyboard.press('ControlOrMeta+A');

    if (text === '') {
      await page.keyboard.press('Delete');
    } else {
      // A paste, because that is how a schema reaches this editor: from a file, a terminal, a
      // pull request. `keyboard.insertText` is the other candidate and the wrong one — it is
      // delivered as input-method composition, which Monaco applies incrementally, and a 140 KB
      // document took over a minute where the same document pasted takes under half a second.
      // Playwright was slow, not the editor; the number is worth having written down.
      await page.evaluate((pasted) => {
        const input = document.querySelector('.native-edit-context, textarea.inputarea');
        const data = new DataTransfer();

        data.setData('text/plain', pasted);
        input?.dispatchEvent(
          new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true }),
        );
      }, text);
    }

    // Leave by clicking something inert. Tab would insert a tab; that is the editor's job.
    await page.getByRole('heading', { name: 'Register a version' }).click();
  } else {
    const editor = schemaEditor(page);

    await editor.fill(text);
    await editor.blur();
  }

  await settle(page);
}

/** What the editor holds. */
export async function schemaText(page: Page): Promise<string> {
  if (!(await isRich(page))) {
    return schemaEditor(page).inputValue();
  }

  // Monaco renders only the lines in view, each as an absolutely positioned element, and writes
  // a space as a no-break space. Enough for any document a test types; not for one that scrolls.
  return page.evaluate(() => {
    const lines = [...document.querySelectorAll<HTMLElement>('.monaco-editor .view-line')];

    lines.sort((a, b) => parseFloat(a.style.top) - parseFloat(b.style.top));

    return lines.map((line) => (line.textContent ?? '').replaceAll(' ', ' ')).join('\n');
  });
}

/** The codes of the findings on screen, by severity. */
export async function findingCodes(
  page: Page,
  severity: 'refusal' | 'advice',
): Promise<readonly string[]> {
  return page
    .locator(`[data-severity="${severity}"]`)
    .evaluateAll((items) => items.map((i) => i.getAttribute('data-code') ?? ''));
}

/**
 * The line the caret is on, 1-based, in Monaco.
 *
 * Read from the highlighted line's position among the rendered ones, since Monaco does not
 * expose the caret to the DOM any other way.
 */
export async function caretLine(page: Page): Promise<number> {
  return page.evaluate(() => {
    const current = document.querySelector<HTMLElement>('.monaco-editor .current-line');
    const lines = [...document.querySelectorAll<HTMLElement>('.monaco-editor .view-line')];

    lines.sort((a, b) => parseFloat(a.style.top) - parseFloat(b.style.top));

    if (current === null || lines.length === 0) {
      return -1;
    }

    const lineHeight = lines[0]!.getBoundingClientRect().height;
    const first = lines[0]!.getBoundingClientRect().top;

    return Math.round((current.getBoundingClientRect().top - first) / lineHeight) + 1;
  });
}
