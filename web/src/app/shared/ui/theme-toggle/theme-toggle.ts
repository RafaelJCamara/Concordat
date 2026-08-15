import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { Icon, type IconName } from '../icon/icon';

/**
 * Structurally the same as `core/config/theme.ts`'s `Appearance`, and declared here rather
 * than imported so that `shared/` keeps depending on nothing. Three literals restated is a
 * cheaper coupling than a shared-UI component that reaches into application config; if the
 * two ever disagree, the assignment in the shell stops compiling, which is where the
 * mismatch belongs.
 */
type Appearance = 'light' | 'dark' | 'system';

/**
 * Light / dark / system, as a segmented control.
 *
 * Presentational on purpose, even though there is exactly one store it could ever talk to:
 * `shared/ui` is the layer that gets restyled and reused, and the moment one component in
 * it injects a store, "shared" stops being true. The app shell owns the wiring.
 *
 * Three positions rather than a two-state switch because `system` is a distinct choice, not
 * the absence of one — a toggle cannot express "follow the OS" without a third position or
 * a hidden long-press, and the user who wants it is the user who switches at dusk.
 *
 * <b>Icons, not words, and the labels move to `aria-label`.</b> This sits in a sidebar that
 * collapses to 64px, where "Light Dark System" cannot fit and would either wrap into three
 * lines or force the rail wider than the design. A sun, a moon and a monitor are the
 * conventional spelling of exactly this control, so nothing is lost by the trade — and
 * `title` keeps the words one hover away for anyone who does not read the icons that way.
 */
@Component({
  selector: 'cd-theme-toggle',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon],
  template: `
    <div
      role="group"
      aria-label="Colour theme"
      class="bg-muted/50 inline-flex items-center gap-0.5 rounded-lg p-0.5"
    >
      @for (option of options; track option.value) {
        <button
          type="button"
          [attr.aria-label]="option.label"
          [attr.aria-pressed]="appearance() === option.value"
          [title]="option.label"
          class="focus-visible:ring-ring flex size-7 items-center justify-center rounded-md transition-colors focus-visible:ring-2 focus-visible:outline-none"
          [class]="
            appearance() === option.value
              ? 'bg-background text-foreground shadow-sm'
              : 'text-muted-foreground hover:text-foreground'
          "
          (click)="chosen.emit(option.value)"
        >
          <cd-icon [name]="option.icon" size="0.875rem" />
        </button>
      }
    </div>
  `,
})
export class ThemeToggle {
  readonly appearance = input.required<Appearance>();
  readonly chosen = output<Appearance>();

  protected readonly options: readonly { value: Appearance; label: string; icon: IconName }[] = [
    { value: 'light', label: 'Light', icon: 'sun' },
    { value: 'dark', label: 'Dark', icon: 'moon' },
    { value: 'system', label: 'System', icon: 'monitor' },
  ];
}
