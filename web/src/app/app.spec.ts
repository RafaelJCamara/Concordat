import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { beforeEach, describe, expect, it } from 'vitest';
import { App } from './app';
import { SessionStore } from './core/auth/session-store';
import { provideConcordatConfig } from './core/config/app-config';

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

  it('warns when the instance is unclaimed', () => {
    session.observeInstance({ claimed: false, actor: null, scopes: [] });

    expect(banner(render())).not.toBeNull();
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
