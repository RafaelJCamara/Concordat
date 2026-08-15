import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { Router } from '@angular/router';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmCardImports } from '@spartan-ng/helm/card';
import { SessionStore } from '../../../core/auth/session-store';
import { Icon } from '../../../shared/ui/icon/icon';
import { SignInStore } from '../application/sign-in-store';

/**
 * The sign-in screen, which doubles as first-run setup.
 *
 * One screen rather than two, because the two differ by a single fact the server already
 * knows — whether any account exists. A separate `/setup` route would be reachable after
 * setup, would have to redirect, and would be one more URL to get wrong.
 */
@Component({
  selector: 'cd-sign-in-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [SignInStore],
  imports: [HlmCardImports, HlmAlertImports, HlmButton, Icon],
  template: `
    <!--
      Centred in the window rather than sitting at the top of a column. The shell drops the
      sidebar on this route, so this page is the whole viewport, and a form pinned to the
      top of an otherwise empty screen reads as a page that failed to finish loading.
    -->
    <section class="mx-auto flex min-h-dvh w-full max-w-md flex-col justify-center gap-6 p-6">
      <!--
        The wordmark, which every other screen gets from the sidebar. Without it this is an
        unlabelled password box, and an unlabelled password box is the shape of a phishing
        page — the one screen where saying which product is asking actually matters.
      -->
      <div class="flex items-center justify-center gap-3">
        <span class="bg-primary/10 text-primary flex size-9 items-center justify-center rounded-lg">
          <cd-icon name="rabbit" size="1.375rem" />
        </span>
        <span>
          <span class="text-foreground block leading-tight font-semibold">Concordat</span>
          <span class="text-muted-foreground block text-xs leading-tight">Schema registry</span>
        </span>
      </div>

      <div hlmCard class="p-6">
        <header class="space-y-1">
          <h1 class="text-2xl font-semibold tracking-tight">
            {{ claiming() ? 'Set up Concordat' : 'Sign in' }}
          </h1>
          <p class="text-muted-foreground text-sm">
            @if (claiming()) {
              This registry has no accounts yet. The first one you create owns it, and this page
              will not offer to do it again.
            } @else {
              Use the email and password an owner gave you.
            }
          </p>
        </header>

        @if (store.message(); as message) {
          <div hlmAlert variant="destructive" class="mt-4">
            <h2 hlmAlertTitle>{{ claiming() ? 'Could not set up' : 'Could not sign in' }}</h2>
            <p hlmAlertDescription>{{ message }}</p>
          </div>
        }

        <form class="mt-6 space-y-4" (submit)="submit($event)">
          <div class="space-y-1.5">
            <label class="text-sm font-medium" for="email">Email</label>
            <input
              id="email"
              name="email"
              type="email"
              autocomplete="username"
              required
              [value]="email()"
              (input)="email.set(value($event))"
              class="border-input bg-background focus-visible:ring-ring flex h-9 w-full rounded-md border px-3 py-1 text-sm focus-visible:ring-1 focus-visible:outline-none"
            />
          </div>

          <div class="space-y-1.5">
            <label class="text-sm font-medium" for="password">Password</label>
            <input
              id="password"
              name="password"
              type="password"
              [attr.autocomplete]="claiming() ? 'new-password' : 'current-password'"
              required
              [value]="password()"
              (input)="password.set(value($event))"
              class="border-input bg-background focus-visible:ring-ring flex h-9 w-full rounded-md border px-3 py-1 text-sm focus-visible:ring-1 focus-visible:outline-none"
            />
            @if (claiming()) {
              <p class="text-muted-foreground text-xs">
                At least 12 characters. There are no character rules — length is what helps.
              </p>
            }
          </div>

          <button hlmBtn type="submit" class="w-full" [disabled]="store.submitting()">
            {{ submitLabel() }}
          </button>
        </form>
      </div>
    </section>
  `,
})
export class SignInPage {
  private readonly router = inject(Router);
  private readonly session = inject(SessionStore);

  protected readonly store = inject(SignInStore);

  /** Where to go afterwards, bound from `?returnTo=` by the router. */
  readonly returnTo = input<string>();

  protected readonly email = signal('');
  protected readonly password = signal('');

  /** Whether this is first-run setup rather than a sign-in. */
  protected readonly claiming = () => this.session.claimed() === false;

  protected readonly submitLabel = () => {
    if (this.store.submitting()) {
      return this.claiming() ? 'Setting up…' : 'Signing in…';
    }

    return this.claiming() ? 'Create owner account' : 'Sign in';
  };

  constructor() {
    // Navigating from an effect rather than inside the store keeps routing out of state:
    // the store records that it succeeded, and the screen decides what that means.
    effect(() => {
      if (this.store.succeeded()) {
        void this.router.navigateByUrl(this.safeReturnTo());
      }
    });
  }

  /**
   * `returnTo` is attacker-craftable (a phishing link can set it), not just router-produced.
   * `navigateByUrl` treats its argument as an in-app route rather than a browser navigation, so
   * `?returnTo=https://evil.com` is not an open redirect today -- but that safety is an
   * implementation detail of which Router API happens to be in use here, not a property of the
   * value itself. Accepting only a same-app path removes the latent class outright: `//host` is
   * a protocol-relative URL a browser would treat as a navigation to `host`, which is why a
   * single leading slash is required and a second one is refused.
   */
  private safeReturnTo(): string {
    const target = this.returnTo();
    return target && /^\/(?!\/)/.test(target) ? target : '/subjects';
  }

  protected submit(event: Event): void {
    event.preventDefault();

    const email = this.email().trim();
    const password = this.password();

    if (email.length === 0 || password.length === 0) {
      return;
    }

    if (this.claiming()) {
      this.store.bootstrap({ email, password });
    } else {
      this.store.signIn({ email, password });
    }
  }

  /** Reads an input's value without casting at every call site. */
  protected value(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }
}
