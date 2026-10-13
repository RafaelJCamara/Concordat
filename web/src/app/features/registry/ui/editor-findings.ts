import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import type { EditorFinding } from '../../../domain/registry/editor-finding';

/**
 * What is wrong, or worth knowing, about the text being edited.
 *
 * <b>Two kinds, said in words and not only in colour.</b> A refusal is something the registry
 * will certainly turn down; advice is this client's opinion, which the registry may overrule.
 * They look different and read differently ("The registry will refuse this" against "Worth
 * checking"), because a list that renders both as red errors teaches people to ignore it, and
 * the first time the registry disagrees with a piece of advice the whole panel loses its
 * credibility.
 *
 * Each finding with a position is a button that asks the editor to go there. A line number in
 * prose is something the reader has to find by hand in a 400-line document.
 */
@Component({
  selector: 'cd-editor-findings',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block' },
  template: `
    @if (findings().length > 0) {
      <ul class="space-y-1.5" [attr.aria-label]="label()">
        @for (finding of findings(); track $index) {
          <li
            class="flex flex-col items-start gap-1 text-xs sm:flex-row sm:gap-2"
            [attr.data-code]="finding.code"
            [attr.data-severity]="finding.severity"
          >
            <span
              class="shrink-0 rounded px-1.5 py-0.5 font-medium sm:mt-px"
              [class]="
                finding.severity === 'refusal'
                  ? 'bg-destructive/10 text-destructive'
                  : 'bg-warning/10 text-warning'
              "
            >
              {{
                finding.severity === 'refusal' ? 'The registry will refuse this' : 'Worth checking'
              }}
            </span>
            <span class="min-w-0">
              <span
                [class]="finding.severity === 'refusal' ? 'text-destructive' : 'text-foreground'"
              >
                {{ finding.message }}
              </span>
              @if (finding.position; as position) {
                <button
                  type="button"
                  class="text-muted-foreground hover:text-foreground ml-1 font-mono underline underline-offset-2"
                  (click)="reveal.emit(finding)"
                >
                  line {{ position.line }}, column {{ position.column }}
                </button>
              }
            </span>
          </li>
        }
      </ul>
    }
  `,
})
export class EditorFindings {
  readonly findings = input.required<readonly EditorFinding[]>();

  /** What the list is called to a screen reader. */
  readonly label = input('Problems with the schema');

  /** Asks the editor to move to a finding. */
  readonly reveal = output<EditorFinding>();
}
