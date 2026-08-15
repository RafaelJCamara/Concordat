import { expect, test, type Page } from '@playwright/test';

/**
 * The design system, asserted rather than eyeballed.
 *
 * <b>This suite exists because the last regression here was invisible to every other test.</b>
 * `styles/tokens.css` shipped for a whole milestone carrying a placeholder palette — a light
 * blue theme that was never the design — while 188 Angular unit tests and 1,489 .NET tests
 * stayed green. Nothing anywhere named a colour, so nothing could notice that all of them were
 * wrong. The look of a product is a contract with its users in the same way its wire format is
 * a contract with its clients, and it was the only one with no test.
 *
 * What each test below pins is a decision someone made on purpose (ADR-006: port the
 * prototype's tokens verbatim), not an accident of the current CSS. A value that changes here
 * should change because someone redesigned something, and then the diff says so.
 *
 * <b>There are deliberately no pixel screenshots.</b> `toHaveScreenshot` stores a baseline per
 * platform, this suite runs on Windows locally and `ubuntu-latest` in CI, and the two render
 * text differently enough to differ on thousands of pixels. The honest options were a suite
 * that fails on every CI run until someone commits Linux baselines from a container, or a
 * `maxDiffPixelRatio` loose enough to sleep through a real change. Computed styles are exact,
 * identical on both platforms, and say what they mean when they fail: "primary is no longer the
 * prototype's teal" beats "1,297 pixels differ".
 */

/** Reads a custom property off `<html>`, where both theme blocks are defined. */
async function token(page: Page, name: string): Promise<string> {
  return page.evaluate(
    (property) => getComputedStyle(document.documentElement).getPropertyValue(property).trim(),
    name,
  );
}

/**
 * Loads a route as a browser that has never been here would.
 *
 * <b>The storage is cleared before the app runs, not after.</b> The obvious spelling — load,
 * `evaluate(() => localStorage.clear())`, reload — races the router: `goto` resolves on the
 * document, Angular then navigates on its own (`/` and `**` both resolve elsewhere), and the
 * evaluate lands in a context that has just been torn down. It passes perhaps nine times in
 * ten, which is worse than never, because the tenth is a CI failure that reruns green and
 * teaches everyone to press the button again.
 *
 * The init script self-disarms through `sessionStorage`, which survives a reload but not a
 * new context. So the first load of each test sees empty storage and every reload after it
 * sees whatever the app has since written — which is exactly what "survives a reload" needs
 * to mean something. It also drops a whole page load per test.
 */
async function visitFresh(page: Page, path: string): Promise<void> {
  await page.addInitScript(() => {
    try {
      if (!sessionStorage.getItem('e2e.storage-cleared')) {
        localStorage.clear();
        sessionStorage.setItem('e2e.storage-cleared', '1');
      }
    } catch {
      /* A context that refuses storage has nothing stored to clear. */
    }
  });

  await page.goto(path);
}

test.describe('the ported palette', () => {
  test('is the prototype’s, value for value', async ({ page }) => {
    await visitFresh(page, '/');

    // The three that carry the identity: the teal everything points with, the near-black navy
    // ground, and the sidebar sitting one step darker than the page so the rail reads as
    // behind it rather than on it. Spelled as the prototype spells them.
    expect(await token(page, '--primary')).toBe('hsl(173 80% 50%)');
    expect(await token(page, '--background')).toBe('hsl(222 47% 6%)');
    expect(await token(page, '--sidebar')).toBe('hsl(222 47% 5%)');

    // The corner radius is part of the voice too — 0.625rem is visibly softer than shadcn's
    // 0.5rem default, and Spartan derives every other radius in the app from it.
    expect(await token(page, '--radius')).toBe('0.625rem');
  });

  test('actually reaches the page, not just the stylesheet', async ({ page }) => {
    await visitFresh(page, '/');

    // A token can be defined correctly and bridged wrongly. Tailwind v4 resolves `bg-background`
    // through Spartan's `@theme inline` map, and if that link breaks the custom properties above
    // still assert clean while every surface renders unstyled. hsl(222 47% 6%) is this rgb.
    await expect(page.locator('body')).toHaveCSS('background-color', 'rgb(8, 12, 22)');
  });

  test('loads the prototype’s two faces', async ({ page }) => {
    await visitFresh(page, '/');

    // Inter for the interface, JetBrains Mono for anything that is a schema, a subject name or
    // an environment. The monospace is not decoration — a dotted subject name is read
    // character by character, and a proportional font makes `acme.orders.v1` and
    // `acme.0rders.v1` look alike.
    await expect(page.locator('body')).toHaveCSS('font-family', /^Inter,/);

    const subjectName = page.locator('.font-mono').first();
    await expect(subjectName).toHaveCSS('font-family', /^"?JetBrains Mono"?,/);

    // …and the faces actually arrived. The two assertions above pass on the declared stack
    // whether or not a single byte of font was fetched, so on their own they would stay green
    // through exactly the failure that matters: the files 404ing and every screen silently
    // falling back to Arial.
    await page.waitForFunction(() => document.fonts.status === 'loaded');

    const loaded = await page.evaluate(() => ({
      inter: document.fonts.check('1rem Inter'),
      mono: document.fonts.check('1rem "JetBrains Mono"'),
    }));

    expect(loaded).toEqual({ inter: true, mono: true });
  });

  test('fetches them from this origin and asks nobody else for anything', async ({
    page,
    baseURL,
  }) => {
    // ADR-026, as a test. This is a self-hosted registry: an air-gapped install is an ordinary
    // deployment, and a page that pulls its typography from `fonts.gstatic.com` renders in
    // fallback faces there while sending every reader's IP to a third party everywhere else.
    // Angular hides the regression well — `ng build` inlines a Google Fonts link and
    // `ng serve` does not — so the check belongs against the dev server, which is where this
    // suite runs and where the reintroduction would otherwise go unnoticed.
    const origin = new URL(baseURL ?? 'http://localhost:4300').origin;
    const foreign: string[] = [];

    page.on('request', (request) => {
      if (!request.url().startsWith(origin) && !request.url().startsWith('data:')) {
        foreign.push(request.url());
      }
    });

    await visitFresh(page, '/');
    await page.waitForFunction(() => document.fonts.status === 'loaded');

    expect(foreign).toEqual([]);
  });
});

test.describe('the theme', () => {
  test('starts dark for a visitor who has never chosen', async ({ page }) => {
    await visitFresh(page, '/');

    // Dark rather than `system`, because the prototype shipped no light palette and this is the
    // interface the product was drawn as. A `system` default would hand most first-time
    // visitors — whose OS is light — a variant no screenshot of this product shows.
    await expect(page.locator('html')).toHaveClass(/\bdark\b/);
  });

  test('switches to a light palette on the same hue', async ({ page }) => {
    await visitFresh(page, '/');
    await page.getByRole('button', { name: 'Light' }).click();

    await expect(page.locator('html')).not.toHaveClass(/\bdark\b/);

    // Still teal, and still 80% saturated — the light theme is the same design with the lights
    // on, not a second design. Only the lightness moves, and it moves far enough that white
    // text on it clears WCAG AA.
    expect(await token(page, '--primary')).toBe('hsl(173 80% 27%)');
    expect(await token(page, '--background')).toBe('hsl(210 40% 98%)');
  });

  test('survives a reload, without a flash of the other one', async ({ page }) => {
    await visitFresh(page, '/');
    await page.getByRole('button', { name: 'Light' }).click();
    await expect(page.locator('html')).not.toHaveClass(/\bdark\b/);

    await page.reload();

    // `main.ts` applies the stored choice before `bootstrapApplication`. If it ran after, the
    // class would land one frame late and every load would flash the wrong theme — visible in
    // person, invisible to a unit test, and exactly the kind of thing that ships.
    await expect(page.locator('html')).not.toHaveClass(/\bdark\b/);
  });
});

test.describe('the shell', () => {
  test('is a sidebar, and the main column starts on the page heading', async ({ page }) => {
    await visitFresh(page, '/');

    const nav = page.getByRole('navigation', { name: 'Main' });
    await expect(nav).toBeVisible();
    await expect(nav.getByRole('link', { name: 'Dashboard' })).toBeVisible();
    await expect(nav.getByRole('link', { name: 'Subjects' })).toBeVisible();

    // The layout decision, stated as a test: everything a top bar would have carried lives in
    // the sidebar instead, so the first thing in the main column is the page's own <h1>.
    await expect(page.getByRole('heading', { level: 1, name: 'Dashboard' })).toBeVisible();
  });

  test('marks the current page for a screen reader, not only in teal', async ({ page }) => {
    await visitFresh(page, '/subjects');

    // The active row is tinted, given a pulsing dot, and carries aria-current. Only the last of
    // those survives having no colour vision, which is the reason it is here.
    // Scoped to the nav. "Subjects" is also the name of a stat card on the dashboard, which
    // links to the same route — an unscoped role query matches both and fails on strict mode
    // the first time somebody runs this from the landing screen.
    const nav = page.getByRole('navigation', { name: 'Main' });

    await expect(nav.getByRole('link', { name: 'Subjects' })).toHaveAttribute(
      'aria-current',
      'page',
    );
    await expect(nav.getByRole('link', { name: 'Dashboard' })).not.toHaveAttribute(
      'aria-current',
      'page',
    );
  });

  test('collapses to a rail and expands again', async ({ page }) => {
    await visitFresh(page, '/');

    const sidebar = page.locator('cd-app-sidebar');
    const nav = page.getByRole('navigation', { name: 'Main' });
    const toggle = page.getByRole('button', { name: 'Collapse sidebar' });

    await expect(toggle).toHaveAttribute('aria-expanded', 'true');
    expect((await sidebar.boundingBox())?.width).toBe(256);

    await toggle.click();

    // The labels go, the icons stay, and the control renames itself — a toggle that still says
    // "Collapse" once collapsed is the most common way this component is got wrong.
    const expand = page.getByRole('button', { name: 'Expand sidebar' });
    await expect(expand).toHaveAttribute('aria-expanded', 'false');
    await expect(page.getByText('Schema registry')).toHaveCount(0);
    await expect.poll(async () => (await sidebar.boundingBox())?.width).toBe(64);

    // Still reachable by name with the text gone. The collapsed row is an icon and the icon is
    // aria-hidden, so the only thing left carrying the name is the `title` — drop it and the
    // rail becomes five identically unnamed links to a screen reader.
    await expect(nav.getByRole('link', { name: 'Subjects' })).toBeVisible();

    await expand.click();

    await expect(page.getByText('Schema registry')).toBeVisible();
    await expect.poll(async () => (await sidebar.boundingBox())?.width).toBe(256);
  });

  test('starts as a rail on a phone, and still opens if asked', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await visitFresh(page, '/');

    // 256px of a 390px window is two thirds of it spent on two links, and everything
    // downstream fails the way narrow columns fail: the heading breaks mid-word, the search
    // box renders three characters. None of that is visible at the width screens are drawn at,
    // which is why it is asserted at a width nobody develops on.
    const sidebar = page.locator('cd-app-sidebar');
    await expect.poll(async () => (await sidebar.boundingBox())?.width).toBe(64);

    // The window gets the first word, not the last. Someone who deliberately opens the rail on
    // a small screen keeps it open — a `computed` on the viewport would take the toggle away
    // exactly where it is most likely to be wanted.
    await page.getByRole('button', { name: 'Expand sidebar' }).click();
    await expect.poll(async () => (await sidebar.boundingBox())?.width).toBe(256);
  });

  test('is dropped entirely on sign-in', async ({ page }) => {
    await visitFresh(page, '/sign-in');

    // A navigation rail is an offer to go somewhere, and every destination on it answers 401
    // until this form is filled in. The wordmark stays, because an unlabelled password box is
    // the shape of a phishing page.
    await expect(page.getByRole('navigation', { name: 'Main' })).toHaveCount(0);
    await expect(page.getByText('Schema registry')).toBeVisible();
  });
});

test.describe('the icon set', () => {
  test('is hidden from assistive tech wherever a visible label already says it', async ({
    page,
  }) => {
    await visitFresh(page, '/');

    // Every nav row is a glyph beside its own word. Announcing both makes a screen reader say
    // "Dashboard Dashboard", which is why `cd-icon` defaults to aria-hidden and takes a label
    // only when the icon *is* the label.
    const navIcons = page.getByRole('navigation', { name: 'Main' }).locator('cd-icon svg');
    const count = await navIcons.count();
    expect(count).toBeGreaterThan(0);

    for (let index = 0; index < count; index++) {
      await expect(navIcons.nth(index)).toHaveAttribute('aria-hidden', 'true');
    }
  });

  test('renders geometry rather than an empty box', async ({ page }) => {
    await visitFresh(page, '/');

    // `cd-icon` switches on a closed union of names, so an unknown name renders an empty
    // `<svg>` — invisible in a screenshot and easy to miss in review. Every icon on the
    // landing screen having at least one child is the cheapest possible guard against that.
    const empties = await page
      .locator('cd-icon svg')
      .evaluateAll((nodes) => nodes.filter((node) => node.children.length === 0).length);

    expect(empties).toBe(0);
  });
});

test.describe('every screen', () => {
  for (const path of ['/', '/subjects', '/sign-in']) {
    test(`renders ${path} without a console error`, async ({ page }) => {
      // A page that renders while logging a failed request is a page that is quietly degraded.
      // Cheap to assert, and it generalises: every screen M4.3 adds gets this for free by
      // being added to the list above.
      //
      // <b>Failed requests are collected from the network, not the console.</b> Chromium logs a
      // bad subresource as the bare string "Failed to load resource: the server responded with
      // a status of 404 ()" — no URL, no method, nothing anyone can act on. The response
      // listener has all three, so the console list drops that one message rather than
      // reporting the same failure twice and worse.
      // Every response, from anywhere. This was once filtered to same-origin, because
      // `index.html` linked Google Fonts and a bad second from `fonts.gstatic.com` would have
      // failed a test about *this* app being healthy. ADR-026 removed that request, and with
      // no third party left to excuse, the filter would only have hidden one coming back.
      const failed: string[] = [];
      page.on('response', (response) => {
        if (response.status() >= 400) {
          failed.push(`${response.status()} ${response.request().method()} ${response.url()}`);
        }
      });

      const errors: string[] = [];
      page.on('console', (message) => {
        if (message.type() === 'error' && !message.text().startsWith('Failed to load resource')) {
          errors.push(message.text());
        }
      });
      page.on('pageerror', (error) => errors.push(error.message));

      await visitFresh(page, path);
      await expect(page.getByRole('heading', { level: 1 })).toBeVisible();

      expect(failed).toEqual([]);
      expect(errors).toEqual([]);
    });
  }
});
