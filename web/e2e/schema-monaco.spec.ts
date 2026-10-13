import { expect, test, type Page } from '@playwright/test';
import {
  findingCodes,
  isRich,
  schemaText,
  setSchema,
  waitForEditor,
  waitForSubject,
} from './schema-editor';

/**
 * Monaco, as a user's browser gets it.
 *
 * What is under test is not the editor — Microsoft tests that — but the four promises this app
 * makes about having one:
 *
 * - **Self-hosted** ([ADR-026](../../docs/adr/026-self-hosted-web-fonts.md)): everything it needs
 *   is a file of this origin, and loading it makes no request to anyone else.
 * - **Lazy**: a person who never opens this screen never downloads it.
 * - **Degradable**: if it cannot be fetched the screen is a textarea that works, and says so.
 * - **Accessible**: it has a name, and Tab can be made to leave it.
 *
 * Run against the dev server these are necessary, not sufficient: the stylesheet in particular
 * once worked in `ng serve` and was missing from a production build, because the dev server
 * injects what the build does not link. `e2e/README.md` says how the same specs run against the
 * production image.
 */
const SUBJECT = 'acme.e2e.OrderCreated';
const NEW_VERSION = `/subjects/${SUBJECT}/versions/new`;

function originOf(url: string): string {
  return new URL(url).origin;
}

/** Everything the page asks for, with where it asked. */
function watch(page: Page): { requests: string[]; origin: () => string } {
  const requests: string[] = [];

  page.on('request', (request) => requests.push(request.url()));

  return { requests, origin: () => originOf(page.url()) };
}

test.describe('the code editor', () => {
  test('is a file of this origin, and asks nobody else for anything', async ({ page }) => {
    const seen = watch(page);

    await page.goto(NEW_VERSION);
    await waitForSubject(page);
    await expect(page.locator('.monaco-editor')).toBeVisible();

    const origin = seen.origin();
    const foreign = seen.requests.filter(
      (url) => !url.startsWith('data:') && !url.startsWith('blob:') && originOf(url) !== origin,
    );

    expect(foreign).toEqual([]);
    expect(seen.requests.some((url) => /\/monaco\/monaco-[0-9a-f]+\.css$/.test(url))).toBe(true);
  });

  test('is styled, and its icons are drawn from a font of this origin', async ({ page }) => {
    // The failure this guards is quiet: an editor without its stylesheet still takes text, as
    // an unpositioned pile of elements, and every other test would pass.
    await page.goto(NEW_VERSION);
    await waitForSubject(page);

    const size = await page.locator('.monaco-editor').first().boundingBox();

    expect(size?.height).toBeGreaterThan(200);
    expect(size?.width).toBeGreaterThan(300);

    await page.locator('.monaco-editor').first().click();
    await page.keyboard.press('ControlOrMeta+F');
    await expect(page.locator('.find-widget.visible')).toBeVisible();

    // A codicon is a glyph from the font; if the font did not load it is a box or nothing.
    const icon = page.locator('.find-widget .codicon').first();

    await expect(icon).toBeVisible();
    expect(
      await icon.evaluate((element) => getComputedStyle(element, '::before').fontFamily),
    ).toContain('codicon');
    expect(await page.evaluate(() => document.fonts.check('16px codicon'))).toBe(true);
  });

  test('sits in the interface: its background is the page’s, in either theme', async ({ page }) => {
    await page.goto(NEW_VERSION);
    await waitForSubject(page);

    const colours = () =>
      page.evaluate(() => {
        const read = (selector: string) =>
          getComputedStyle(document.querySelector(selector)!).backgroundColor;

        return { page: read('body'), editor: read('.monaco-editor-background') };
      });

    await page.getByRole('button', { name: 'Dark', exact: true }).click();
    await expect.poll(async () => (await colours()).editor === (await colours()).page).toBe(true);

    const dark = await colours();

    await page.getByRole('button', { name: 'Light', exact: true }).click();
    await expect
      .poll(async () => {
        const light = await colours();

        return light.editor === light.page && light.editor !== dark.editor;
      })
      .toBe(true);
  });

  test('is not downloaded by a screen that has no use for it', async ({ page }) => {
    const seen = watch(page);

    for (const path of ['/', '/subjects', `/subjects/${SUBJECT}`, '/approvals']) {
      await page.goto(path);
      await page.waitForLoadState('networkidle');
    }

    expect(seen.requests.filter((url) => url.includes('/monaco/'))).toEqual([]);
    expect(page.workers()).toHaveLength(0);
    expect(await page.locator('.monaco-editor').count()).toBe(0);
  });

  test('draws the problems it is told about as underlines', async ({ page }) => {
    await page.goto(NEW_VERSION);
    await waitForSubject(page);
    await setSchema(page, '{"type":"object","type":"string"}');

    await expect(page.locator('.squiggly-error').first()).toBeVisible();
    expect(await findingCodes(page, 'refusal')).toEqual(['schema_malformed']);

    await setSchema(page, '{"type":"object"}');

    await expect(page.locator('.squiggly-error')).toHaveCount(0);
  });

  test('has a name, and says what is wrong with it to assistive technology', async ({ page }) => {
    await page.goto(NEW_VERSION);
    await waitForSubject(page);
    await setSchema(page, '{"a":');

    const input = page.getByRole('textbox', { name: 'Schema document' });

    await expect(input).toBeVisible();
    await expect(input).toHaveAttribute('aria-invalid', 'true');
    expect(await input.getAttribute('aria-describedby')).toContain('schema-help');
  });

  test('can be left with the keyboard, once Ctrl+M says so', async ({ page }) => {
    // A code editor treats Tab as a character, which traps a keyboard user inside it. The
    // hint under the editor says what lifts that, and it has to actually do it.
    await page.goto(NEW_VERSION);
    await waitForSubject(page);
    await setSchema(page, '{}');

    await page.locator('.monaco-editor').first().click();
    await page.keyboard.press('Tab');

    // Tab was text: two spaces, the editor's indent.
    await expect.poll(() => schemaText(page)).toBe('{}  ');

    await page.keyboard.press('ControlOrMeta+M');
    await page.keyboard.press('Tab');

    // Now it moved focus, and not into the document.
    await expect
      .poll(() => page.evaluate(() => document.activeElement?.closest('.monaco-editor') === null))
      .toBe(true);
  });

  test('keeps Ctrl+Z for the person who loads a version over their work and regrets it', async ({
    page,
  }) => {
    await page.goto(NEW_VERSION);
    await waitForSubject(page);
    await setSchema(page, '{"mine":"unsaved work"}');

    await page.getByRole('button', { name: 'Load into the editor' }).click();
    await page.getByRole('button', { name: 'Replace', exact: true }).click();
    await expect.poll(() => schemaText(page)).not.toContain('unsaved work');

    await page.locator('.monaco-editor').first().click();
    await page.keyboard.press('ControlOrMeta+Z');

    await expect.poll(() => schemaText(page)).toBe('{"mine":"unsaved work"}');
  });
});

test.describe('when the code editor cannot be fetched', () => {
  test('the screen is a textarea that works, and says so in a sentence', async ({ page }) => {
    // The stylesheet is part of loading: an editor without it is worse than the textarea, so
    // a stylesheet that fails is a load that failed. Aborting it is a real network failure, not
    // a stubbed answer.
    await page.route(/\/monaco\/monaco-[0-9a-f]+\.css$/, (route) => route.abort());

    await page.goto(NEW_VERSION);
    await waitForEditor(page);

    expect(await isRich(page)).toBe(false);
    await expect(page.getByText(/could not be loaded, so this is the plain editor/)).toBeVisible();

    // It is a working editor, with every check the rich one has.
    await setSchema(page, '{"type": ');

    await expect(page.getByText(/not well-formed json/i)).toBeVisible();
    await expect(page.getByRole('button', { name: 'Register', exact: true })).toBeDisabled();

    await setSchema(page, '{"type":"object"}');

    await expect(page.getByRole('button', { name: 'Register', exact: true })).toBeEnabled();
    await expect(page.getByRole('button', { name: 'Format' })).toBeVisible();
  });
});
