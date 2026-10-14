import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { type AuditEntry, isKnownAuditAction } from '../../../domain/governance/audit';
import { RelativeTimePipe } from '../../../shared/pipes/relative-time-pipe';
import { StatusBadge, type StatusTone } from '../../../shared/ui/status-badge/status-badge';

/**
 * How an action is toned. Presentation only: an action absent from this table is rendered
 * neutral and unchanged, which is the same rule `DivergenceList` follows for `kind`.
 *
 * Read as reading rather than as colour — a rejection and a retirement are the entries a
 * reviewer scans a log for, a submission is a thing waiting, and everything else is the
 * ordinary background of a registry being used.
 */
const TONES: Readonly<Record<string, StatusTone>> = {
  VERSION_APPROVED: 'success',
  VERSION_REJECTED: 'destructive',
  SUBJECT_RETIRED: 'destructive',
  BROKER_REMOVED: 'destructive',
  BROKER_CREDENTIAL_REMOVED: 'destructive',
  API_KEY_REVOKED: 'destructive',
  VERSION_SUBMITTED: 'warning',
  VERSION_DISMISSED: 'neutral',
};

/**
 * The audit trail, as a table, newest first.
 *
 * Presentational: entries in, nothing else. The exact time is in a `title` beside the relative
 * one — "2 months ago" is the right thing to scan a log with and the wrong thing to reconcile
 * an incident against, and an audit log is precisely where the next question is "when exactly?".
 */
@Component({
  selector: 'cd-audit-table',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmTableImports, RelativeTimePipe, StatusBadge],
  template: `
    <div hlmTableContainer class="border-border bg-card rounded-xl border">
      <table
        hlmTable
        class="[&_td:first-child]:ps-4 [&_td:last-child]:pe-4 [&_th:first-child]:ps-4 [&_th:last-child]:pe-4"
      >
        <caption hlmCaption class="border-border mt-0 border-t px-4 py-3 text-left">
          Newest first. The trail records what happened, not what was attempted: a refused request
          leaves no entry.
        </caption>
        <thead hlmTHead>
          <tr hlmTr>
            <th hlmTh scope="col">When</th>
            <th hlmTh scope="col">Action</th>
            <th hlmTh scope="col">Actor</th>
            <th hlmTh scope="col">Target</th>
            <th hlmTh scope="col">Detail</th>
          </tr>
        </thead>
        <tbody hlmTBody>
          @for (entry of entries(); track $index) {
            <tr hlmTr>
              <td hlmTd class="whitespace-nowrap" [title]="entry.at.toISOString()">
                {{ entry.at | cdRelativeTime }}
              </td>
              <td hlmTd>
                <cd-status-badge
                  [tone]="toneOf(entry.action)"
                  [attr.title]="hintOf(entry.action)"
                  >{{ entry.action }}</cd-status-badge
                >
              </td>
              <td hlmTd class="font-mono text-xs">{{ entry.actor }}</td>
              <td hlmTd class="font-mono font-medium break-all">{{ entry.target }}</td>
              <td hlmTd class="text-muted-foreground min-w-48 whitespace-normal">
                {{ entry.detail }}
              </td>
            </tr>
          }
        </tbody>
      </table>
    </div>
  `,
})
export class AuditTable {
  readonly entries = input.required<readonly AuditEntry[]>();

  protected toneOf(action: string): StatusTone {
    // Own properties only. The action is whatever the registry holds (ADR-036), and a plain
    // object answers `constructor` and `toString` with what every object inherits, which is not
    // a tone and rendered a badge with no colour at all.
    return Object.hasOwn(TONES, action) ? TONES[action]! : 'neutral';
  }

  /**
   * What the badge says when this app cannot read the action: that it is not an ordinary one, and
   * that the text is the registry's. Nothing for an action it knows.
   *
   * Tone stays neutral on purpose, because a severity guessed for a kind of event nobody here has
   * read the meaning of would be a mislabel; what a newer registry's destructive action must not
   * do is pass for the neutral background of a registry being used.
   */
  protected hintOf(action: string): string | null {
    return isKnownAuditAction(action)
      ? null
      : 'This version of the app does not know this action. It is shown as the registry sent it.';
  }
}
