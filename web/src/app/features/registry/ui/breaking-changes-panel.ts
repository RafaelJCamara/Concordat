import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import type { HeldVersion } from '../../../domain/registry/environment-summary';
import { RelativeTimePipe } from '../../../shared/pipes/relative-time-pipe';
import { Icon } from '../../../shared/ui/icon/icon';
import { StatusBadge } from '../../../shared/ui/status-badge/status-badge';
import { SubjectName } from '../../../shared/ui/subject-name/subject-name';

/**
 * The newest versions the approval gate is holding or turned away.
 *
 * <b>"Breaking" here means what the registry did about it, not what it thinks of the change.</b> A
 * version appears when its change broke the subject's compatibility policy and the gate held it
 * (ADR-017), or when a reviewer then rejected it. An approved one is live and is deliberately
 * absent: the question this panel answers is "what is waiting on a person, and what did we turn
 * away", and an approved change is recorded in the audit log, where who approved it lives.
 *
 * Presentational: rows in, nothing else. Each row links to the version, and a held one is the thing
 * the Approvals page decides.
 */
@Component({
  selector: 'cd-breaking-changes-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, Icon, RelativeTimePipe, StatusBadge, SubjectName],
  host: { class: 'block' },
  template: `
    <section class="border-border bg-card h-full rounded-xl border" aria-labelledby="cd-bc-title">
      <header class="flex items-start justify-between gap-4 p-5 pb-3">
        <div class="min-w-0">
          <h2 id="cd-bc-title" class="text-foreground text-lg font-semibold">Breaking changes</h2>
          <p class="text-muted-foreground mt-0.5 text-sm">
            Held at the approval gate, or rejected.
          </p>
        </div>
        <a
          routerLink="/approvals"
          class="text-muted-foreground hover:text-foreground focus-visible:ring-ring flex shrink-0 items-center gap-1 rounded-sm text-sm transition-colors focus-visible:ring-2 focus-visible:outline-none"
        >
          Approvals
          <cd-icon name="arrow-right" size="0.875rem" />
        </a>
      </header>

      @if (changes().length === 0) {
        <div class="px-5 pb-6">
          <p class="font-medium">Nothing held or rejected</p>
          <p class="text-muted-foreground mt-1 text-sm">
            A change that breaks a subject's compatibility policy is registered and held here for a
            decision. Approved changes are live and are not listed.
          </p>
        </div>
      } @else {
        <ul class="divide-border divide-y" aria-label="Held or rejected versions">
          @for (change of changes(); track change.subject + '/' + change.ordinal) {
            <li class="px-5 py-3">
              <a
                class="hover:bg-muted/50 focus-visible:ring-ring -mx-2 block rounded-md px-2 py-1 focus-visible:ring-2 focus-visible:outline-none"
                [routerLink]="['/subjects', change.subject, 'versions', change.ordinal]"
              >
                <div class="flex flex-wrap items-center gap-x-3 gap-y-1">
                  <span class="text-foreground min-w-0 text-sm font-medium">
                    <cd-subject-name [name]="change.subject" />
                  </span>
                  <span class="text-muted-foreground font-mono text-xs">
                    v{{ change.ordinal }}
                    @if (change.semanticVersion) {
                      <span>· {{ change.semanticVersion }}</span>
                    }
                  </span>
                  @if (change.status === 'AWAITING_APPROVAL') {
                    <cd-status-badge tone="warning" [pulse]="true"
                      >Awaiting approval</cd-status-badge
                    >
                  } @else {
                    <cd-status-badge tone="destructive">Rejected</cd-status-badge>
                  }
                </div>
                <p class="text-muted-foreground mt-1 text-xs">
                  <span [title]="change.registeredAt.toISOString()">
                    Proposed by {{ change.registeredBy }} {{ change.registeredAt | cdRelativeTime }}
                  </span>
                  @if (change.decidedAt; as decidedAt) {
                    <span [title]="decidedAt.toISOString()">
                      · rejected by {{ change.decidedBy }} {{ decidedAt | cdRelativeTime }}
                    </span>
                  }
                </p>
              </a>
            </li>
          }
        </ul>
      }
    </section>
  `,
})
export class BreakingChangesPanel {
  readonly changes = input.required<readonly HeldVersion[]>();
}
