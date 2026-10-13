import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import {
  formatSemanticVersion,
  type LabelSuggestions as Suggestions,
  type SemanticVersion,
} from '../../../domain/registry/semantic-version';

/**
 * One-click labels for the next version, from the label the registry will compare against.
 *
 * <b>Offered, never applied.</b> The field is optional and the right bump depends on whether
 * the change is breaking, which only the registry's verdict knows — and which it checks, so
 * a patch bump on a breaking change is refused rather than believed. Pre-filling the field
 * with a guess would put a value on the wire the author never chose; three buttons put the
 * choice where it belongs and make the common one cheap.
 *
 * With no live label there is nothing to increase on, so the one thing offered is a first
 * release. `0.x` is deliberately not offered: the registry refuses a breaking first version
 * labelled with major zero, and a suggestion that sets someone up for that refusal is worse
 * than none.
 */
@Component({
  selector: 'cd-label-suggestions',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block' },
  template: `
    <div class="flex flex-wrap items-center gap-1.5 text-xs">
      @if (from(); as label) {
        <span class="text-muted-foreground">
          Highest live label <span class="text-foreground font-mono">{{ format(label) }}</span
          >. Next:
        </span>
      } @else {
        <span class="text-muted-foreground">No labelled version yet. First release:</span>
      }

      @for (option of options(); track option.kind) {
        <button
          type="button"
          class="border-border hover:bg-accent rounded-md border px-2 py-0.5 font-mono transition-colors"
          [attr.aria-label]="option.kind + ' bump to ' + option.text"
          (click)="pick.emit(option.text)"
        >
          {{ option.text }}
        </button>
      }
    </div>
  `,
})
export class LabelSuggestions {
  /** The label the registry compares against, or null when no live version has one. */
  readonly from = input<SemanticVersion | null>(null);

  readonly suggestions = input<Suggestions | null>(null);

  /** The label to put in the field. */
  readonly pick = output<string>();

  protected readonly options = computed(() => {
    const value = this.suggestions();

    if (value === null) {
      return [{ kind: 'first release', text: '1.0.0' }];
    }

    return [
      { kind: 'patch', text: formatSemanticVersion(value.patch) },
      { kind: 'minor', text: formatSemanticVersion(value.minor) },
      { kind: 'major', text: formatSemanticVersion(value.major) },
    ];
  });

  protected format = formatSemanticVersion;
}
