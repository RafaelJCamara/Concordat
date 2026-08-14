import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSeparator } from '@spartan-ng/helm/separator';
import { SessionStore } from './core/auth/session-store';
import { SessionApi } from './core/auth/session-api';
import { ActiveEnvironmentStore } from './core/config/active-environment-store';
import { ThemeStore } from './core/config/theme-store';
import { ThemeToggle } from './shared/ui/theme-toggle/theme-toggle';

/**
 * The application shell.
 *
 * The one component that is allowed to wire `core/` stores into `shared/ui` components:
 * everything below it either owns its own store (a feature) or takes inputs (shared UI).
 */
@Component({
  selector: 'cd-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterOutlet,
    RouterLink,
    RouterLinkActive,
    HlmAlertImports,
    HlmButton,
    HlmSeparator,
    ThemeToggle,
  ],
  template: `
    <a
      href="#main"
      class="bg-background focus:ring-ring sr-only focus:not-sr-only focus:absolute focus:top-2 focus:left-2 focus:z-50 focus:rounded-md focus:px-3 focus:py-2 focus:ring-2"
    >
      Skip to content
    </a>

    <header class="flex items-center gap-4 px-6 py-3">
      <span class="font-semibold tracking-tight">Concordat</span>

      <nav aria-label="Main" class="flex items-center gap-3 text-sm">
        <a
          routerLink="/subjects"
          routerLinkActive="text-foreground"
          class="text-muted-foreground hover:text-foreground transition-colors"
        >
          Subjects
        </a>
      </nav>

      <div class="ms-auto flex items-center gap-3">
        <span class="text-muted-foreground font-mono text-xs">
          {{ environments.name() }}
        </span>

        @if (session.isSignedIn()) {
          <span class="text-muted-foreground text-xs">{{ session.actor() }}</span>
          <button
            type="button"
            class="text-muted-foreground hover:text-foreground text-sm transition-colors"
            (click)="signOut()"
          >
            Sign out
          </button>
        } @else if (session.needsSignIn()) {
          <a
            routerLink="/sign-in"
            class="text-muted-foreground hover:text-foreground text-sm transition-colors"
          >
            Sign in
          </a>
        }

        <cd-theme-toggle [appearance]="theme.appearance()" (chosen)="theme.choose($event)" />
      </div>
    </header>

    <hlm-separator />

    @if (session.claimed() === false) {
      <!--
        Said out loud rather than left to be discovered. An unclaimed registry answers every
        request as an owner, so it is open to anyone who can reach it — and nothing else in
        the product would ever mention that.
      -->
      <div hlmAlert variant="destructive" class="mx-6 mt-4">
        <h2 hlmAlertTitle>This registry is unclaimed</h2>
        <p hlmAlertDescription>
          Anyone who can reach it can change anything. Create the first account to close it.
        </p>
        <a hlmBtn hlmAlertAction variant="outline" size="sm" routerLink="/sign-in">Set up</a>
      </div>
    }

    <main id="main">
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
