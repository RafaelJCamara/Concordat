import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { IfScope } from '../../../core/auth/if-scope';
import { ORG_ADMIN_SCOPES } from '../../../domain/identity/scope';

/**
 * The tabs across the top of every settings screen.
 *
 * <b>Members and API keys are absent, not disabled, for anyone without `org:admin`</b>
 * (ADR-018): the whole group is admin-only on the server, reads included, so a tab to it would
 * lead somewhere that answers 403 to everybody else. They live in `feature/` and not in `ui/`
 * because they read the session through `*cdIfScope`, and `ui/` may not depend on `core/`.
 *
 * <b>Account is for everybody who is signed in</b> (ADR-048): everyone owns their own password, so it
 * asks for no scope.
 *
 * The page heading above says "Settings" and these say where in it you are — `aria-current` on
 * the active one, not only a colour.
 */
@Component({
  selector: 'cd-settings-nav',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, RouterLinkActive, IfScope],
  host: { class: 'block' },
  template: `
    <nav aria-label="Settings" class="border-border flex flex-wrap gap-1 border-b">
      <a
        routerLink="/settings/environment"
        routerLinkActive="!border-primary !text-foreground"
        ariaCurrentWhenActive="page"
        class="text-muted-foreground hover:text-foreground -mb-px border-b-2 border-transparent px-3 py-2 text-sm font-medium transition-colors"
      >
        Environment
      </a>
      <a
        routerLink="/settings/brokers"
        routerLinkActive="!border-primary !text-foreground"
        ariaCurrentWhenActive="page"
        class="text-muted-foreground hover:text-foreground -mb-px border-b-2 border-transparent px-3 py-2 text-sm font-medium transition-colors"
      >
        Brokers
      </a>
      <a
        *cdIfScope="orgAdmin"
        routerLink="/settings/members"
        routerLinkActive="!border-primary !text-foreground"
        ariaCurrentWhenActive="page"
        class="text-muted-foreground hover:text-foreground -mb-px border-b-2 border-transparent px-3 py-2 text-sm font-medium transition-colors"
      >
        Members
      </a>
      <a
        *cdIfScope="orgAdmin"
        routerLink="/settings/api-keys"
        routerLinkActive="!border-primary !text-foreground"
        ariaCurrentWhenActive="page"
        class="text-muted-foreground hover:text-foreground -mb-px border-b-2 border-transparent px-3 py-2 text-sm font-medium transition-colors"
      >
        API keys
      </a>
      <a
        routerLink="/settings/notifications"
        routerLinkActive="!border-primary !text-foreground"
        ariaCurrentWhenActive="page"
        class="text-muted-foreground hover:text-foreground -mb-px border-b-2 border-transparent px-3 py-2 text-sm font-medium transition-colors"
      >
        Notifications
      </a>
      <a
        routerLink="/settings/account"
        routerLinkActive="!border-primary !text-foreground"
        ariaCurrentWhenActive="page"
        class="text-muted-foreground hover:text-foreground -mb-px border-b-2 border-transparent px-3 py-2 text-sm font-medium transition-colors"
      >
        Account
      </a>
    </nav>
  `,
})
export class SettingsNav {
  protected readonly orgAdmin = ORG_ADMIN_SCOPES;
}
