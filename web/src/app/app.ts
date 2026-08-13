import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { HlmSeparator } from '@spartan-ng/helm/separator';
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
  imports: [RouterOutlet, RouterLink, RouterLinkActive, HlmSeparator, ThemeToggle],
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
        <cd-theme-toggle [appearance]="theme.appearance()" (chosen)="theme.choose($event)" />
      </div>
    </header>

    <hlm-separator />

    <main id="main">
      <router-outlet />
    </main>
  `,
})
export class App {
  protected readonly theme = inject(ThemeStore);
  protected readonly environments = inject(ActiveEnvironmentStore);
}
