import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  DOCUMENT,
  computed,
  inject,
  linkedSignal,
  signal,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterOutlet } from '@angular/router';
import { filter, map } from 'rxjs';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { SessionStore } from './core/auth/session-store';
import { SessionApi } from './core/auth/session-api';
import { ActiveEnvironmentStore } from './core/config/active-environment-store';
import { ThemeStore } from './core/config/theme-store';
import { AppSidebar, type NavItem } from './shared/ui/app-sidebar/app-sidebar';
import { Icon } from './shared/ui/icon/icon';
import { ThemeToggle } from './shared/ui/theme-toggle/theme-toggle';

/**
 * The navigation, in the order the sidebar draws it.
 *
 * Two rows, because there are two screens. Contracts, Approvals and Audit are M4.3 and get a
 * row each on the day they get a route — see `NavItem` for why they are not here already.
 *
 * The list lives in the composition root rather than inside `AppSidebar`: a shared
 * presentational component that knows the app's route table is not shared, it is this app's
 * sidebar wearing a shared component's name.
 */
const NAV_ITEMS: readonly NavItem[] = [
  { label: 'Dashboard', icon: 'layout-dashboard', route: '/' },
  { label: 'Subjects', icon: 'file-json', route: '/subjects' },
];

/**
 * The width below which the open rail costs more than it gives.
 *
 * Tailwind's `md` boundary, and it is not a round number picked to look tidy: 256px of a
 * 390px phone is two thirds of the window spent on two links. Everything downstream then
 * fails in the way narrow columns fail — the page heading breaks mid-word, the search box
 * renders three characters, the subject table becomes a scrollbar — and none of it is
 * visible on the machine the screens are drawn on.
 */
const NARROW = '(max-width: 767px)';

/**
 * The application shell.
 *
 * The one component that is allowed to wire `core/` stores into `shared/ui` components:
 * everything below it either owns its own store (a feature) or takes inputs (shared UI).
 *
 * The layout is the prototype's: a full-height sidebar on the left and one scrolling column
 * on the right, with no top bar. Everything a top bar would have carried — environment,
 * account, theme — sits in the sidebar footer instead, so the main column begins on the
 * page's own heading. That is the most recognisable thing about the design and the first
 * thing a header would take away.
 */
@Component({
  selector: 'cd-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterOutlet, RouterLink, HlmAlertImports, HlmButton, AppSidebar, Icon, ThemeToggle],
  host: { class: 'bg-background text-foreground flex min-h-dvh w-full' },
  template: `
    <a
      href="#main"
      class="bg-background focus:ring-ring sr-only focus:not-sr-only focus:absolute focus:top-2 focus:left-2 focus:z-50 focus:rounded-md focus:px-3 focus:py-2 focus:ring-2"
    >
      Skip to content
    </a>

    @if (!chromeless()) {
      <cd-app-sidebar
        [items]="navItems"
        [collapsed]="collapsed()"
        (toggled)="collapsed.set(!collapsed())"
      >
        <div cdSidebarFooter class="space-y-2">
          <!--
            The environment, and the reason it is the most prominent thing down here: every
            number on every screen is scoped to it, so "which environment am I looking at"
            is the question that makes the difference between a routine change and an
            outage. It reads as a label rather than a control because switching is not
            wired yet (M4.3).
          -->
          <div
            class="text-muted-foreground flex items-center gap-2 rounded-lg px-3 py-2 text-xs"
            [class.justify-center]="collapsed()"
            [attr.title]="collapsed() ? 'Environment ' + environments.name() : null"
          >
            <cd-icon name="database" size="0.875rem" />
            @if (!collapsed()) {
              <span class="text-foreground truncate font-mono">{{ environments.name() }}</span>
            }
          </div>

          @if (session.isSignedIn()) {
            <button
              type="button"
              class="text-muted-foreground hover:bg-sidebar-accent hover:text-foreground focus-visible:ring-ring flex w-full items-center gap-2 rounded-lg px-3 py-2 text-xs transition-colors focus-visible:ring-2 focus-visible:outline-none"
              [class.justify-center]="collapsed()"
              [attr.title]="collapsed() ? 'Sign out (' + session.actor() + ')' : null"
              (click)="signOut()"
            >
              <cd-icon name="log-out" size="0.875rem" />
              @if (!collapsed()) {
                <span class="truncate">{{ session.actor() }}</span>
                <span class="ms-auto shrink-0">Sign out</span>
              }
            </button>
          } @else if (session.needsSignIn()) {
            <a
              routerLink="/sign-in"
              class="text-muted-foreground hover:bg-sidebar-accent hover:text-foreground focus-visible:ring-ring flex items-center gap-2 rounded-lg px-3 py-2 text-xs transition-colors focus-visible:ring-2 focus-visible:outline-none"
              [class.justify-center]="collapsed()"
              [attr.title]="collapsed() ? 'Sign in' : null"
            >
              <cd-icon name="key-round" size="0.875rem" />
              @if (!collapsed()) {
                <span>Sign in</span>
              }
            </a>
          }

          @if (!collapsed()) {
            <div class="px-1">
              <cd-theme-toggle [appearance]="theme.appearance()" (chosen)="theme.choose($event)" />
            </div>
          }
        </div>
      </cd-app-sidebar>
    }

    <main id="main" class="min-w-0 flex-1 overflow-x-hidden">
      @if (session.claimed() === false) {
        <!--
          Said out loud rather than left to be discovered. An unclaimed registry answers every
          request as an owner, so it is open to anyone who can reach it — and nothing else in
          the product would ever mention that.
        -->
        <div hlmAlert variant="destructive" class="mx-6 mt-6">
          <h2 hlmAlertTitle>This registry is unclaimed</h2>
          <p hlmAlertDescription>
            Anyone who can reach it can change anything. Create the first account to close it.
          </p>
          <a hlmBtn hlmAlertAction variant="outline" size="sm" routerLink="/sign-in">Set up</a>
        </div>
      }

      <router-outlet />
    </main>
  `,
})
export class App {
  private readonly router = inject(Router);
  private readonly sessions = inject(SessionApi);

  protected readonly theme = inject(ThemeStore);
  protected readonly environments = inject(ActiveEnvironmentStore);
  protected readonly session = inject(SessionStore);

  protected readonly navItems = NAV_ITEMS;

  private readonly narrow = watchNarrow();

  /**
   * Whether the rail is showing icons only. View state, and nowhere else's business — see the
   * note on `AppSidebar`.
   *
   * <b>A `linkedSignal` and not a plain one, so the window gets the first word and the reader
   * gets the last.</b> It starts collapsed on a narrow viewport and re-collapses whenever the
   * window crosses back under {@link NARROW}, which is what makes the app usable on a phone;
   * between those crossings the toggle sets it freely, so someone who deliberately opens the
   * rail on a small screen keeps it open. A plain signal seeded once would leave a rotated
   * tablet in whichever state it booted in, and a `computed` would take the toggle away
   * entirely on the screens that need it most.
   */
  protected readonly collapsed = linkedSignal(() => this.narrow());

  /**
   * The URL, as a signal.
   *
   * Seeded with `router.url` rather than left undefined because the first `NavigationEnd`
   * fires after the shell's first render: without the seed a reload onto `/sign-in` paints
   * the sidebar for one frame and then removes it.
   */
  private readonly url = toSignal(
    this.router.events.pipe(
      filter((event): event is NavigationEnd => event instanceof NavigationEnd),
      map((event) => event.urlAfterRedirects),
    ),
    { initialValue: this.router.url },
  );

  /**
   * Whether to drop the shell and give the page the whole window.
   *
   * Only sign-in. A navigation rail is an offer to go somewhere, and every destination on
   * it answers 401 until this form is filled in — so on this one screen the sidebar is five
   * links to nowhere framing the single thing the user is actually able to do.
   */
  protected readonly chromeless = computed(() => this.url().startsWith('/sign-in'));

  /**
   * Drops the credential locally and clears the session cookie.
   *
   * The API key itself is left to expire: revoking it on sign-out would mean one row deleted per
   * browser tab closed, and a user who needs it gone sooner revokes it from the keys screen. The
   * cookie does need a round trip, because script cannot delete an httpOnly one — and the local
   * state is dropped either way, since a sign-out that failed because the network was down must
   * still sign you out of the tab in front of you.
   */
  protected signOut(): void {
    this.sessions.signOut().subscribe({
      next: () => this.leave(),
      error: () => this.leave(),
    });
  }

  private leave(): void {
    this.session.expire();
    void this.router.navigateByUrl('/sign-in');
  }
}

/**
 * Whether the window is currently narrower than {@link NARROW}, as a signal.
 *
 * Watched rather than read once, for the same reason `ThemeStore` watches
 * `prefers-color-scheme`: a window is resized and a phone is rotated, and a layout that only
 * consulted the viewport at bootstrap would be wrong for the rest of the session. Must be
 * called from an injection context.
 *
 * `matchMedia` is reached through `DOCUMENT.defaultView` and treated as optional, because
 * neither is guaranteed — the unit tests run in a jsdom that has no window until one is
 * stubbed, and a shell that cannot be constructed cannot be asserted on.
 */
function watchNarrow() {
  const view = inject(DOCUMENT).defaultView;
  const query = view?.matchMedia(NARROW);
  const narrow = signal(query?.matches ?? false);

  if (query) {
    const onChange = (event: MediaQueryListEvent) => narrow.set(event.matches);
    query.addEventListener('change', onChange);
    inject(DestroyRef).onDestroy(() => query.removeEventListener('change', onChange));
  }

  return narrow.asReadonly();
}
