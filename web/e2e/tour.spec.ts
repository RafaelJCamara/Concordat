import { readFileSync } from 'node:fs';
import { expect, test, type Browser, type Page } from '@playwright/test';
import { TOUR_SEEN_KEY, TOUR_STEPS } from '../src/app/core/config/tour';
import { chooseAppearance, contrastOf, expectNoSidewaysOverflow } from './compatibility-help';
import { OWNER_STORAGE_STATE, READER_STORAGE_STATE } from './global-setup';
import { OWNER } from './registry';
import { signIn } from './sign-in';

/**
 * The guided tour (ADR-051).
 *
 * <b>Every other spec in this suite starts from a browser that has already been shown the tour</b>
 * (`global-setup.ts` and `signIn` write the flag), because a card that opens by itself over the
 * first page of each of them would be in the way of their clicks. This is the one spec that wants
 * the opposite, and it gets it without signing in again: it takes the saved owner or reader state,
 * which holds the session cookie, and removes only the "seen" flag from it. That is a returning
 * visitor who has cleared their site data except the session, which the app resumes by itself —
 * and it costs none of the auth limiter's budget. One test signs in through the real form, to prove
 * the tour waits for it.
 *
 * What it reads against: the real shell, the real registry, a real keyboard. Nothing is stubbed.
 */

const STEPS = TOUR_STEPS.map((step) => step.title);

/** A saved state with the "seen" flag taken out: a first-time visitor who is still signed in. */
function withoutTheFlag(path: string) {
  const state = JSON.parse(readFileSync(path, 'utf8')) as {
    cookies: unknown[];
    origins: { origin: string; localStorage: { name: string; value: string }[] }[];
  };

  return {
    ...state,
    origins: state.origins.map((origin) => ({
      ...origin,
      localStorage: origin.localStorage.filter((entry) => entry.name !== TOUR_SEEN_KEY),
    })),
  };
}

async function visitor(
  browser: Browser,
  who: 'owner' | 'reader',
  options: { narrow?: boolean; appearance?: 'light' | 'dark' } = {},
): Promise<Page> {
  const context = await browser.newContext({
    storageState: withoutTheFlag(who === 'owner' ? OWNER_STORAGE_STATE : READER_STORAGE_STATE),
    viewport: options.narrow ? { width: 390, height: 844 } : { width: 1280, height: 800 },
    hasTouch: options.narrow,
  });
  const page = await context.newPage();

  if (options.appearance) {
    await chooseAppearance(page, options.appearance);
  }

  return page;
}

const card = (page: Page) => page.getByRole('dialog');
const named = (page: Page, title: string) => page.getByRole('dialog', { name: title });
const seen = (page: Page) => page.evaluate((key) => localStorage.getItem(key), TOUR_SEEN_KEY);
const target = (page: Page, key: string) => page.locator(`[data-tour="${key}"]`).first();

/**
 * Presses Next until the card shows `title`, with the pointer, as the buttons are meant to be used.
 *
 * Waits for the heading to change after each press: a step on another screen only shows once that
 * screen has arrived, and a second press while the first is still on its way is a different journey.
 */
async function goTo(page: Page, title: string) {
  const heading = card(page).getByRole('heading');

  for (let i = 0; i < 10; i++) {
    const shown = ((await heading.textContent()) ?? '').trim();

    if (shown === title) {
      return;
    }

    await card(page).getByRole('button', { name: 'Next' }).click();
    await expect(heading).not.toHaveText(shown);
  }
  throw new Error(`The tour never reached "${title}"`);
}

test.describe('a first visit', () => {
  test('starts by itself, tells the story in order, and puts the person back where they were', async ({
    browser,
  }) => {
    const page = await visitor(browser, 'owner');
    await page.goto('/subjects');

    // The administrator sees every step, and the counter says so.
    await expect(named(page, STEPS[0]!)).toBeVisible();
    await expect(card(page)).toContainText(`Step 1 of ${STEPS.length}`);

    for (const [index, title] of STEPS.entries()) {
      await expect(named(page, title)).toBeVisible();
      await expect(card(page)).toContainText(`Step ${index + 1} of ${STEPS.length}`);

      const last = index === STEPS.length - 1;
      await card(page)
        .getByRole('button', { name: last ? 'Done' : 'Next' })
        .click();
    }

    await expect(card(page)).toHaveCount(0);
    // The API keys step took the person to the settings; the end of the tour takes them back.
    await expect(page).toHaveURL(/\/subjects$/);
    expect(await seen(page)).toBe('1');

    await page.context().close();
  });

  test('visits the real screens it talks about', async ({ browser }) => {
    const page = await visitor(browser, 'owner');
    await page.goto('/');

    await goTo(page, 'API keys let services in');

    // Not text about a screen: the screen, with the tab it is talking about under the ring.
    await expect(page).toHaveURL(/\/settings\/api-keys$/);
    await expect(target(page, 'settings-api-keys')).toHaveAttribute('aria-current', 'page');

    await page.context().close();
  });

  test('points a ring at the thing each step is about', async ({ browser }) => {
    const page = await visitor(browser, 'owner');
    await page.goto('/');
    await expect(card(page)).toBeVisible();

    for (const step of TOUR_STEPS) {
      await goTo(page, step.title);
      await expect(page.locator('[data-tour-ring]')).toBeVisible();

      // The ring glides to its target, so it is read once it has arrived.
      await expect
        .poll(async () => {
          const ring = (await page.locator('[data-tour-ring]').boundingBox())!;
          const box = (await target(page, step.target).boundingBox())!;

          // The ring is the target grown by a few pixels on each side, so it contains it.
          return (
            ring.x <= box.x + 0.5 &&
            ring.y <= box.y + 0.5 &&
            ring.x + ring.width >= box.x + box.width - 0.5 &&
            ring.y + ring.height >= box.y + box.height - 0.5
          );
        }, step.id)
        .toBe(true);

      const box = (await target(page, step.target).boundingBox())!;
      // The card never overlaps what it is about.
      const at = (await card(page).boundingBox())!;
      const overlaps =
        at.x < box.x + box.width &&
        at.x + at.width > box.x &&
        at.y < box.y + box.height &&
        at.y + at.height > box.y;
      expect(overlaps, `${step.id}: the card covers its target`).toBe(false);
    }

    await page.context().close();
  });

  test('is remembered, and comes back when the browser is cleared', async ({ browser }) => {
    const page = await visitor(browser, 'owner');
    await page.goto('/');
    await expect(card(page)).toBeVisible();
    await page.keyboard.press('Tab');
    await page.keyboard.press('Escape');
    await expect(card(page)).toHaveCount(0);
    expect(await seen(page)).toBe('1');

    // Another visit: it has been seen.
    await page.reload();
    await expect(page.getByRole('heading', { name: 'Dashboard' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Take the tour' })).toBeVisible();
    await expect(card(page)).toHaveCount(0);

    // A cleared cache is a first visit again, and the session cookie is still there.
    await page.evaluate(() => localStorage.clear());
    await page.reload();
    await expect(named(page, STEPS[0]!)).toBeVisible();

    await page.context().close();
  });

  test('is remembered even if the person leaves in the middle', async ({ browser }) => {
    const page = await visitor(browser, 'owner');
    await page.goto('/');
    await goTo(page, STEPS[2]!);

    await page.reload();

    await expect(page.getByRole('heading', { name: 'Dashboard' })).toBeVisible();
    await expect(card(page)).toHaveCount(0);

    await page.context().close();
  });

  test('does not start over the sign-in screen, and starts once someone has signed in', async ({
    browser,
  }) => {
    // Nothing saved at all: a person who has never been here.
    // Explicitly empty: a context made here inherits the config's saved owner state otherwise.
    const context = await browser.newContext({ storageState: { cookies: [], origins: [] } });
    const page = await context.newPage();

    await page.goto('/sign-in');
    await expect(page.getByRole('textbox', { name: 'Email' })).toBeVisible();
    await expect(card(page)).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Take the tour' })).toHaveCount(0);

    await signIn(page, OWNER, { tour: 'fresh' });

    await expect(named(page, STEPS[0]!)).toBeVisible();
    await context.close();
  });
});

test.describe('a person who cannot administer', () => {
  test('is not shown a step for a screen they cannot open, and is not counted for it', async ({
    browser,
  }) => {
    const page = await visitor(browser, 'reader');
    const visited: string[] = [];
    page.on('framenavigated', (frame) => {
      if (frame === page.mainFrame()) visited.push(new URL(frame.url()).pathname);
    });
    await page.goto('/');

    const expected = TOUR_STEPS.filter((step) => step.id !== 'api-keys');
    await expect(card(page)).toContainText(`Step 1 of ${expected.length}`);

    for (const [index, step] of expected.entries()) {
      await expect(named(page, step.title)).toBeVisible();
      await expect(card(page)).toContainText(`Step ${index + 1} of ${expected.length}`);
      await card(page)
        .getByRole('button', { name: index === expected.length - 1 ? 'Done' : 'Next' })
        .click();
    }

    await expect(card(page)).toHaveCount(0);
    expect(visited.filter((path) => path.includes('api-keys'))).toEqual([]);
    expect(page.url()).not.toContain('settings');

    await page.context().close();
  });
});

test.describe('a step with nothing to point at', () => {
  test('is skipped without a word, and the counter does not count it', async ({ browser }) => {
    const page = await visitor(browser, 'owner');
    await page.goto('/');
    await expect(card(page)).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(card(page)).toHaveCount(0);

    // The approvals entry is gone from the page, as it would be for a role that hides it.
    await page.evaluate(() => document.querySelector('[data-tour="nav-approvals"]')?.remove());
    await page.getByRole('button', { name: 'Take the tour' }).click();

    const expected = TOUR_STEPS.filter((step) => step.id !== 'approvals');
    await expect(card(page)).toContainText(`Step 1 of ${expected.length}`);

    await card(page).getByRole('button', { name: 'Next' }).click();
    await expect(card(page)).toContainText('Step 2 of');
    await card(page).getByRole('button', { name: 'Next' }).click();

    // Contracts, where approvals would have been.
    await expect(named(page, 'What a route enforces')).toBeVisible();
    await expect(card(page)).toContainText(`Step 3 of ${expected.length}`);

    await page.context().close();
  });
});

test.describe('with a keyboard', () => {
  test('is reached, driven and left without a pointer, and never traps', async ({ browser }) => {
    const page = await visitor(browser, 'owner');
    await page.goto('/subjects');

    // Opening moves focus to the card, so the first Tab is its first button.
    await expect(card(page)).toBeFocused();
    await page.keyboard.press('Tab');
    await expect(card(page).getByRole('button', { name: 'Skip tour' })).toBeFocused();
    await page.keyboard.press('Tab');
    await expect(card(page).getByRole('button', { name: 'Next' })).toBeFocused();

    // Enter on Next moves on, and focus stays on the button that was pressed.
    await page.keyboard.press('Enter');
    await expect(named(page, STEPS[1]!)).toBeVisible();
    await expect(card(page).getByRole('button', { name: 'Next' })).toBeFocused();

    // The step is announced politely while focus has not moved.
    await expect(page.locator('cd-tour-popover').getByRole('status')).toContainText(
      `Step 2 of ${STEPS.length}: ${STEPS[1]!}`,
    );

    // Back is before Next in the order, and going back to the first step, where Back is not
    // drawn, must not lose focus to the page.
    await page.keyboard.press('Shift+Tab');
    await expect(card(page).getByRole('button', { name: 'Back' })).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(named(page, STEPS[0]!)).toBeVisible();
    await expect(card(page)).toBeFocused();

    // Tab out of the end of the card: focus goes on to the rest of the page, not back into it.
    await page.keyboard.press('Tab');
    await page.keyboard.press('Tab');
    await expect(card(page).getByRole('button', { name: 'Next' })).toBeFocused();
    await page.keyboard.press('Tab');
    expect(
      await page.evaluate(() => document.activeElement?.closest('[role="dialog"]')),
    ).toBeNull();

    await page.context().close();
  });

  test('is left with Escape from the card, and the page is the same as before', async ({
    browser,
  }) => {
    const page = await visitor(browser, 'owner');
    await page.goto('/subjects');
    await expect(card(page)).toBeFocused();
    await page.keyboard.press('Tab');
    await page.keyboard.press('Tab');
    await page.keyboard.press('Enter');
    await expect(named(page, STEPS[1]!)).toBeVisible();

    await page.keyboard.press('Escape');

    await expect(card(page)).toHaveCount(0);
    await expect(page.locator('[data-tour-ring]')).toHaveCount(0);
    expect(await seen(page)).toBe('1');
    await expect(page).toHaveURL(/\/subjects$/);

    await page.context().close();
  });

  test('leaves an Escape that was meant for something else on the page alone', async ({
    browser,
  }) => {
    const page = await visitor(browser, 'owner');
    await page.goto('/subjects');
    await expect(card(page)).toBeVisible();

    // The person has moved into the page, onto a link, and pressed Escape there. Nothing on a
    // link handles it, so only the tour could: and focus is not on the tour, so it must not.
    await page.locator('cd-app-sidebar').getByRole('link', { name: 'Contracts' }).focus();
    await page.keyboard.press('Escape');
    await expect(card(page)).toBeVisible();

    // And a field that handles Escape itself (the search box clears) is left to it as well.
    await page.getByRole('searchbox').or(page.getByRole('textbox')).first().focus();
    await page.keyboard.press('Escape');
    await expect(card(page)).toBeVisible();

    await page.context().close();
  });

  test('is replayed from a button that is reached by Tab, and focus returns to it', async ({
    browser,
  }) => {
    const page = await visitor(browser, 'owner');
    await page.goto('/');
    await page.keyboard.press('Escape');
    await expect(card(page)).toHaveCount(0);

    const help = page.getByRole('button', { name: 'Take the tour' });
    await help.focus();
    await page.keyboard.press('Enter');
    await expect(named(page, STEPS[0]!)).toBeVisible();
    await expect(card(page)).toContainText(`Step 1 of ${STEPS.length}`);

    await page.keyboard.press('Escape');

    await expect(card(page)).toHaveCount(0);
    await expect(help).toBeFocused();

    await page.context().close();
  });

  test('can be skipped from every step', async ({ browser }) => {
    const page = await visitor(browser, 'owner');
    await page.goto('/');
    await expect(card(page)).toBeVisible();

    for (let step = 0; step < STEPS.length; step++) {
      await page.getByRole('button', { name: 'Take the tour' }).click();
      await goTo(page, STEPS[step]!);

      await card(page).getByRole('button', { name: 'Skip tour' }).click();

      await expect(card(page)).toHaveCount(0);
    }

    await page.context().close();
  });
});

test.describe('it does not stand in the way of the page', () => {
  test('the page underneath still takes clicks while the tour is open', async ({ browser }) => {
    const page = await visitor(browser, 'owner');
    await page.goto('/');
    await expect(card(page)).toBeVisible();

    // Playwright refuses to click an element another element sits over, so this is also the
    // check that nothing the tour draws covers the page.
    await page.locator('cd-app-sidebar').getByRole('link', { name: 'Subjects' }).click();

    await expect(page).toHaveURL(/\/subjects$/);
    // And the tour is still there to be dismissed, not closed by the click.
    await expect(card(page)).toBeVisible();

    await page.context().close();
  });

  test('is not announced as modal and leaves the page available to assistive technology', async ({
    browser,
  }) => {
    const page = await visitor(browser, 'owner');
    await page.goto('/');
    await expect(card(page)).toBeVisible();

    await expect(card(page)).not.toHaveAttribute('aria-modal', /.*/);
    expect(await page.locator('[inert]').count()).toBe(0);
    expect(await page.locator('main[aria-hidden], cd-app-sidebar[aria-hidden]').count()).toBe(0);
    await expect(page.locator('[data-tour-ring]')).toHaveAttribute('aria-hidden', 'true');

    await page.context().close();
  });

  test('makes no request, raises no error and breaks no Content-Security-Policy', async ({
    browser,
  }) => {
    const page = await visitor(browser, 'owner');
    const problems: string[] = [];
    page.on('console', (message) => {
      if (message.type() === 'error') problems.push(message.text());
    });
    page.on('pageerror', (error) => problems.push(error.message));
    await page.addInitScript(() => {
      document.addEventListener('securitypolicyviolation', (event) =>
        console.error(`CSP ${event.violatedDirective} ${event.blockedURI}`),
      );
    });

    const requests: string[] = [];
    page.on('request', (request) => requests.push(request.url()));

    await page.goto('/');
    const before = requests.length;
    await goTo(page, 'API keys let services in');
    await goTo(page, STEPS[STEPS.length - 1]!);
    await card(page).getByRole('button', { name: 'Done' }).click();

    expect(problems).toEqual([]);
    // Nothing the tour loads is an asset of its own: no script, no stylesheet, no font.
    const loaded = requests.slice(before).filter((url) => /\.(js|css|woff2?)(\?|$)/.test(url));
    expect(loaded.filter((url) => /tour/i.test(url))).toEqual([]);

    await page.context().close();
  });
});

test.describe('when storage is blocked', () => {
  test('works, and starts once for the page and not on every navigation', async ({ browser }) => {
    const page = await visitor(browser, 'owner');
    await page.addInitScript(() => {
      const refuse = () => {
        throw new DOMException('blocked', 'SecurityError');
      };
      Storage.prototype.getItem = refuse;
      Storage.prototype.setItem = refuse;
    });
    await page.goto('/');

    await expect(named(page, STEPS[0]!)).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(card(page)).toHaveCount(0);

    // Navigating inside the app is not a new load: it must not nag.
    await page.locator('cd-app-sidebar').getByRole('link', { name: 'Subjects' }).click();
    await expect(page).toHaveURL(/\/subjects$/);
    await page.locator('cd-app-sidebar').getByRole('link', { name: 'Approvals' }).click();
    await expect(page).toHaveURL(/\/approvals$/);
    await expect(page.getByRole('heading', { name: 'Approvals' })).toBeVisible();
    await expect(card(page)).toHaveCount(0);

    // And it can still be asked for.
    await page.getByRole('button', { name: 'Take the tour' }).click();
    await expect(named(page, STEPS[0]!)).toBeVisible();

    await page.context().close();
  });
});

test.describe('on a phone', () => {
  test('is a sheet along the bottom, every step is reachable, and nothing overflows', async ({
    browser,
  }) => {
    const page = await visitor(browser, 'owner', { narrow: true });
    await page.goto('/');
    await expect(card(page)).toBeVisible();

    for (const [index, step] of TOUR_STEPS.entries()) {
      await expect(named(page, step.title)).toBeVisible();

      const sheet = (await card(page).boundingBox())!;
      expect(sheet.x, step.id).toBeGreaterThanOrEqual(0);
      expect(sheet.x + sheet.width, step.id).toBeLessThanOrEqual(390);
      expect(sheet.y + sheet.height, step.id).toBeLessThanOrEqual(844);
      // A sheet across the window (390 less the 8px margins), not a card beside the target.
      expect(sheet.width, step.id).toBeGreaterThanOrEqual(370);
      await expectNoSidewaysOverflow(page, card(page));

      // Still pointing at its target, and the target is on screen.
      const box = (await target(page, step.target).boundingBox())!;
      expect(box.y + box.height, step.id).toBeLessThanOrEqual(844);
      expect(box.y, step.id).toBeGreaterThanOrEqual(0);
      await expect(page.locator('[data-tour-ring]')).toBeVisible();

      // The sheet does not sit over the thing it is about.
      expect(sheet.y >= box.y + box.height || sheet.y + sheet.height <= box.y, step.id).toBe(true);

      // Touch targets of a usable size.
      for (const button of await card(page).getByRole('button').all()) {
        const size = (await button.boundingBox())!;
        expect(size.height, step.id).toBeGreaterThanOrEqual(24);
      }

      await card(page)
        .getByRole('button', { name: index === TOUR_STEPS.length - 1 ? 'Done' : 'Next' })
        .click();
    }

    await expect(card(page)).toHaveCount(0);

    await page.context().close();
  });

  test('can be replayed from the collapsed rail, where the button has no words drawn', async ({
    browser,
  }) => {
    const page = await visitor(browser, 'owner', { narrow: true });
    await page.goto('/');
    await page.getByRole('button', { name: 'Skip tour' }).click();
    await expect(card(page)).toHaveCount(0);

    const help = page.getByRole('button', { name: 'Take the tour' });
    await expect(help).toBeVisible();
    await expect(help).toHaveText('');
    expect((await help.boundingBox())!.width).toBeGreaterThanOrEqual(24);

    await help.tap();
    await expect(named(page, STEPS[0]!)).toBeVisible();

    await page.context().close();
  });

  test('keeps working when the window is turned from a phone into a desktop', async ({
    browser,
  }) => {
    const page = await visitor(browser, 'owner', { narrow: true });
    await page.goto('/');
    await expect(card(page)).toBeVisible();
    expect((await card(page).boundingBox())!.width).toBeGreaterThan(350);

    await page.setViewportSize({ width: 1280, height: 800 });

    // A card beside its target now, not a sheet across the window.
    await expect.poll(async () => (await card(page).boundingBox())!.width).toBeLessThan(340);
    const ring = (await page.locator('[data-tour-ring]').boundingBox())!;
    const at = (await card(page).boundingBox())!;
    expect(at.x).toBeGreaterThanOrEqual(ring.x + ring.width);

    await page.context().close();
  });
});

for (const appearance of ['light', 'dark'] as const) {
  test.describe(`in ${appearance} mode`, () => {
    test('every step is readable', async ({ browser }) => {
      const page = await visitor(browser, 'owner', { appearance });
      await page.goto('/');
      await expect(card(page)).toBeVisible();

      for (const [index, step] of TOUR_STEPS.entries()) {
        await expect(named(page, step.title)).toBeVisible();

        const ratios = await contrastOf(card(page).locator('p, h2, button'));
        expect(ratios.length).toBeGreaterThan(3);
        for (const ratio of ratios) {
          expect(ratio, `${appearance} ${step.id}`).toBeGreaterThanOrEqual(4.5);
        }

        // The card is its own surface: not the colour of the page behind it.
        const surfaces = await page.evaluate(() => {
          const dialog = document.querySelector('[role="dialog"]')!;
          return [
            getComputedStyle(dialog).backgroundColor,
            getComputedStyle(document.querySelector('main')!).backgroundColor,
          ];
        });
        expect(surfaces[0]).not.toBe('rgba(0, 0, 0, 0)');

        await card(page)
          .getByRole('button', { name: index === TOUR_STEPS.length - 1 ? 'Done' : 'Next' })
          .click();
      }

      await page.context().close();
    });
  });
}

test.describe('motion', () => {
  test('draws no transition for someone who asked for less motion', async ({ browser }) => {
    const page = await visitor(browser, 'owner');
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await page.goto('/');
    await expect(card(page)).toBeVisible();

    const durations = await page.evaluate(() => {
      const ring = document.querySelector('[data-tour-ring]')!;
      const style = getComputedStyle(ring);
      return [style.transitionDuration, style.transitionProperty];
    });

    // `motion-safe:` classes only apply when no reduction is asked for.
    expect(Number.parseFloat(durations[0]!)).toBeLessThan(0.001);

    await page.context().close();
  });

  test('glides to its target for everyone else', async ({ browser }) => {
    const page = await visitor(browser, 'owner');
    await page.emulateMedia({ reducedMotion: 'no-preference' });
    await page.goto('/');
    await expect(card(page)).toBeVisible();

    const duration = await page
      .locator('[data-tour-ring]')
      .evaluate((ring) => getComputedStyle(ring).transitionDuration);

    expect(duration).toBe('0.2s');

    await page.context().close();
  });
});
