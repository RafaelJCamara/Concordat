import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { App } from './app';
import { SessionStore } from './core/auth/session-store';
import { provideConcordatConfig } from './core/config/app-config';
import { TOUR_SEEN_KEY } from './core/config/tour';

@Component({ template: '' })
class Blank {}

/**
 * The unclaimed-instance banner (decision 27).
 *
 * `AllowAnonymousUntilClaimed` means an unclaimed registry answers every unauthenticated
 * request as an owner, which is what makes a first run usable and also means it is open to
 * anyone who can reach it. The API now says so in its log on a repeating timer; this is the
 * half a person actually looks at. Neither existed before, so nothing anywhere mentioned the
 * state — which was the whole of the residual risk.
 */
describe('App', () => {
  let session: InstanceType<typeof SessionStore>;

  const render = () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  };

  const banner = (root: HTMLElement) =>
    [...root.querySelectorAll('h2')].find((h) => h.textContent?.includes('unclaimed')) ?? null;

  beforeEach(() => {
    // The shell injects ThemeStore, which asks the platform whether dark mode is preferred.
    // The test environment has no matchMedia, and a shell that cannot be constructed cannot be
    // asserted on — so it is stubbed here rather than the component being reshaped to suit a
    // test.
    Object.defineProperty(window, 'matchMedia', {
      writable: true,
      value: (query: string) => ({
        matches: false,
        media: query,
        onchange: null,
        addEventListener: () => {},
        removeEventListener: () => {},
        addListener: () => {},
        removeListener: () => {},
        dispatchEvent: () => false,
      }),
    });

    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideConcordatConfig({ defaultEnvironment: 'dev' })],
    });
    session = TestBed.inject(SessionStore);
  });

  it('warns when the instance is unclaimed and answers an unauthenticated caller as an owner', () => {
    // The opt-in (ADR-047): the API says so by granting the unclaimed caller scopes.
    session.observeInstance({
      claimed: false,
      actor: 'unclaimed-instance',
      scopes: ['subject:write', 'org:admin'],
    });

    expect(banner(render())).not.toBeNull();
  });

  it('says nothing on an unclaimed instance that is closed, which sends the visitor to setup instead', () => {
    // A fresh install (ADR-047) answers nobody, so "anyone can change anything" would be false.
    session.observeInstance({ claimed: false, actor: null, scopes: [] });

    expect(banner(render())).toBeNull();
  });

  it('says nothing once the instance is claimed', () => {
    session.observeInstance({ claimed: true, actor: null, scopes: [] });

    expect(banner(render())).toBeNull();
  });

  it('says nothing before the instance has answered', () => {
    // `claimed` starts null, not false. Rendering a security warning while the answer is still
    // in flight would flash it on every page load of a perfectly well-configured registry, and
    // a warning people learn to ignore is worse than none.
    expect(banner(render())).toBeNull();
  });
});

/**
 * The guided tour in the shell (ADR-051): when it starts by itself, where it never does, and the
 * way back to it.
 *
 * `TourStore` has its own spec for what it does once running; what is asserted here is the part
 * only the shell can get wrong, which is deciding that the person is in the app.
 */
describe('App guided tour', () => {
  let session: InstanceType<typeof SessionStore>;
  let router: Router;

  const originalMatchMedia = window.matchMedia;

  function stubMatchMedia(narrow: boolean) {
    Object.defineProperty(window, 'matchMedia', {
      writable: true,
      value: (query: string) => ({
        matches: narrow && query.includes('max-width'),
        media: query,
        onchange: null,
        addEventListener: () => {},
        removeEventListener: () => {},
        addListener: () => {},
        removeListener: () => {},
        dispatchEvent: () => false,
      }),
    });
  }

  /** Lets what waits for a render (the tour starts after the one that draws the rail) happen. */
  async function settle(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }) {
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  /** Renders the shell. */
  async function render() {
    const fixture = TestBed.createComponent(App);
    await settle(fixture);
    return { fixture, root: fixture.nativeElement as HTMLElement };
  }

  const dialog = (root: HTMLElement) => root.querySelector('[role="dialog"]');
  const help = (root: HTMLElement) =>
    root.querySelector<HTMLButtonElement>('button[data-tour="help"]');
  const named = (root: HTMLElement, name: string) =>
    [...root.querySelectorAll('button')].find((b) => b.textContent?.trim() === name) ?? null;

  const signedIn = (change: { mustChangePassword?: boolean } = {}) =>
    session.signIn({
      credential: 'c',
      actor: 'a@example.com',
      scopes: ['subject:read'],
      ...change,
    });

  beforeEach(() => {
    localStorage.clear();
    stubMatchMedia(false);
    // jsdom lays nothing out: every element is said to take up room, as the real rail does.
    vi.spyOn(HTMLElement.prototype, 'getClientRects').mockReturnValue({
      length: 1,
    } as DOMRectList);

    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: '**', component: Blank }]),
        provideConcordatConfig({ defaultEnvironment: 'dev' }),
      ],
    });
    session = TestBed.inject(SessionStore);
    router = TestBed.inject(Router);
  });

  afterEach(() => {
    window.matchMedia = originalMatchMedia;
    vi.restoreAllMocks();
    localStorage.clear();
  });

  it('starts for a person who is signed in and has not seen it, on the dashboard step', async () => {
    signedIn();
    const { root } = await render();

    expect(dialog(root)).not.toBeNull();
    expect(dialog(root)?.textContent).toContain('The state of the registry');
    expect(dialog(root)?.textContent).toContain('Step 1 of');
  });

  it('puts the tour on the navigation by attribute, not by anything that looks like a class', async () => {
    signedIn();
    const { root } = await render();

    for (const target of [
      'nav-dashboard',
      'nav-subjects',
      'nav-approvals',
      'nav-contracts',
      'nav-settings',
      'help',
    ]) {
      expect(root.querySelector(`[data-tour="${target}"]`), target).not.toBeNull();
    }
  });

  it('does not start for a browser that has seen it', async () => {
    localStorage.setItem(TOUR_SEEN_KEY, '1');
    signedIn();
    const { root } = await render();

    expect(dialog(root)).toBeNull();
  });

  it('does not start when nobody is signed in', async () => {
    session.observeInstance({ claimed: true, actor: null, scopes: [] });
    const { root } = await render();

    expect(dialog(root)).toBeNull();
    expect(help(root)).toBeNull();
  });

  it('does not start on the setup screen of an instance nobody has claimed', async () => {
    session.observeInstance({ claimed: false, actor: null, scopes: [] });
    const { root } = await render();

    expect(dialog(root)).toBeNull();
    expect(help(root)).toBeNull();
  });

  it('does not start over the sign-in screen', async () => {
    signedIn();
    await router.navigateByUrl('/sign-in');
    const { root } = await render();

    expect(dialog(root)).toBeNull();
    // The shell is gone there altogether, and so is the way back to the tour.
    expect(root.querySelector('cd-app-sidebar')).toBeNull();
  });

  it('starts, once, when a person who was on the sign-in screen moves into the app', async () => {
    signedIn();
    await router.navigateByUrl('/sign-in');
    const { fixture, root } = await render();
    expect(dialog(root)).toBeNull();

    // The sign-in screen must not have used up the one start this page load gets.
    await router.navigateByUrl('/subjects');
    await settle(fixture);

    expect(dialog(root)).not.toBeNull();
  });

  it('does not start while the only screen that works is the one that replaces a password', async () => {
    signedIn({ mustChangePassword: true });
    const { root } = await render();

    expect(dialog(root)).toBeNull();
    expect(help(root)).toBeNull();
  });

  it('starts once the forced password change is done', async () => {
    signedIn({ mustChangePassword: true });
    const { fixture, root } = await render();
    expect(dialog(root)).toBeNull();

    session.passwordReplaced(['subject:read']);
    await settle(fixture);

    expect(dialog(root)).not.toBeNull();
  });

  it('starts after a sign-in that happens once the shell is up', async () => {
    session.observeInstance({ claimed: true, actor: null, scopes: [] });
    const { fixture, root } = await render();
    expect(dialog(root)).toBeNull();

    signedIn();
    await settle(fixture);

    expect(dialog(root)).not.toBeNull();
  });

  it('offers the tour from a button with a name, to someone who has seen it', async () => {
    localStorage.setItem(TOUR_SEEN_KEY, '1');
    signedIn();
    const { root } = await render();

    expect(help(root)?.textContent).toContain('Take the tour');
  });

  it('names the button on the collapsed rail, where its words are not drawn', async () => {
    localStorage.setItem(TOUR_SEEN_KEY, '1');
    stubMatchMedia(true);
    signedIn();
    const { root } = await render();

    expect(help(root)?.textContent?.trim()).toBe('');
    expect(help(root)?.getAttribute('aria-label')).toBe('Take the tour');
    expect(help(root)?.getAttribute('title')).toBe('Take the tour');
  });

  it('starts again from the button', async () => {
    localStorage.setItem(TOUR_SEEN_KEY, '1');
    signedIn();
    const { fixture, root } = await render();

    help(root)?.click();
    await settle(fixture);

    expect(dialog(root)?.textContent).toContain('The state of the registry');
  });

  it('ends on Skip, and gives focus back to the button that asked for the tour', async () => {
    localStorage.setItem(TOUR_SEEN_KEY, '1');
    signedIn();
    const { fixture, root } = await render();

    help(root)?.focus();
    help(root)?.click();
    await settle(fixture);
    expect(dialog(root)).not.toBeNull();

    named(root, 'Skip tour')?.click();
    await settle(fixture);

    expect(dialog(root)).toBeNull();
    expect(document.activeElement).toBe(help(root));
  });

  it('walks the steps with Next and ends with Done', async () => {
    signedIn();
    const { fixture, root } = await render();
    const total = Number(/of (\d+)/.exec(dialog(root)?.textContent ?? '')?.[1]);

    // The reader holds no administrator scope, so the API keys step is not among them.
    expect(total).toBe(6);

    for (let step = 1; step < total; step++) {
      named(root, 'Next')?.click();
      await settle(fixture);
      expect(dialog(root)?.textContent).toContain(`Step ${step + 1} of ${total}`);
    }

    named(root, 'Done')?.click();
    await settle(fixture);

    expect(dialog(root)).toBeNull();
  });

  it('does not start again after it has ended, however many times the shell is asked', async () => {
    signedIn();
    const { fixture, root } = await render();

    named(root, 'Skip tour')?.click();
    await settle(fixture);

    await router.navigateByUrl('/subjects');
    await settle(fixture);
    await router.navigateByUrl('/approvals');
    await settle(fixture);

    expect(dialog(root)).toBeNull();
  });

  it('is a sheet along the bottom on a narrow window', async () => {
    stubMatchMedia(true);
    signedIn();
    const { root } = await render();

    expect(dialog(root)?.className).toContain('inset-x-2');
  });
});
