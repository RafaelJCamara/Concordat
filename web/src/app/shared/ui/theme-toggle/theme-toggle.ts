import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { HlmButton } from '@spartan-ng/helm/button';

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
 * Three buttons rather than a two-state switch because `system` is a distinct choice, not
 * the absence of one — a toggle cannot express "follow the OS" without a third position or
 * a hidden long-press, and the user who wants it is the user who switches at dusk.
 */
@Component({
  selector: 'cd-theme-toggle',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmButton],
  template: `
    <div role="group" aria-label="Colour theme" class="inline-flex gap-1">
      @for (option of options; track option.value) {
        <button
          hlmBtn
          size="sm"
          [variant]="appearance() === option.value ? 'secondary' : 'ghost'"
          [attr.aria-pressed]="appearance() === option.value"
          (click)="chosen.emit(option.value)"
        >
          {{ option.label }}
        </button>
      }
    </div>
  `,
})
export class ThemeToggle {
  readonly appearance = input.required<Appearance>();
  readonly chosen = output<Appearance>();

  protected readonly options: readonly { value: Appearance; label: string }[] = [
    { value: 'light', label: 'Light' },
    { value: 'dark', label: 'Dark' },
    { value: 'system', label: 'System' },
  ];
}
