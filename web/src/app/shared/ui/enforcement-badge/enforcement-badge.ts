import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { EnforcementMode } from '../../../domain/contracts/contract';
import { StatusBadge, type StatusTone } from '../status-badge/status-badge';

const TONES: Record<EnforcementMode, StatusTone> = {
  OFF: 'neutral',
  MONITOR: 'info',
  ENFORCE: 'success',
};

/** What each mode does, in words, for a tooltip and for anyone who cannot tell from the token. */
export const ENFORCEMENT_MEANING: Record<EnforcementMode, string> = {
  OFF: 'Governs nothing.',
  MONITOR: 'Reports a violation and lets the message through.',
  ENFORCE: 'Refuses a message that violates it.',
};

/**
 * A contract's enforcement mode.
 *
 * The tone is for scanning a list and the word is for reading it: `OFF` and `ENFORCE` are not
 * ordered by colour, so the token is always shown, and what it means is in the tooltip.
 */
@Component({
  selector: 'cd-enforcement-badge',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [StatusBadge],
  host: { class: 'inline-flex' },
  template: `
    <cd-status-badge [tone]="tone()" [attr.title]="meaning()">{{ mode() }}</cd-status-badge>
  `,
})
export class EnforcementBadge {
  readonly mode = input.required<EnforcementMode>();

  protected readonly tone = computed(() => TONES[this.mode()]);
  protected readonly meaning = computed(() => ENFORCEMENT_MEANING[this.mode()]);
}
