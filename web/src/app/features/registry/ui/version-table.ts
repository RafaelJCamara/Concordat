import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { HlmTableImports } from '@spartan-ng/helm/table';
import type { SchemaVersion } from '../../../domain/registry/subject';
import { RelativeTimePipe } from '../../../shared/pipes/relative-time-pipe';
import { StatusBadge, type StatusTone } from '../../../shared/ui/status-badge/status-badge';

/** How a version's status reads, and how it is toned. */
const STATUS_TONES: Record<SchemaVersion['status'], StatusTone> = {
  ACTIVE: 'success',
  AWAITING_APPROVAL: 'warning',
  REJECTED: 'destructive',
  // A proposal withdrawn before anyone reviewed it. Not a failure, so not `destructive`:
  // toning it like a rejection would have a reader hunting for a decision nobody made.
  DISMISSED: 'neutral',
};

interface VersionRow {
  readonly version: SchemaVersion;
  readonly tone: StatusTone;
  readonly isLatest: boolean;
}

/**
 * A subject's versions, as a table.
 *
 * Presentational: inputs in, nothing else. No store, no HTTP.
 *
 * <b>It does use `routerLink`, unlike the card.</b> The distinction is mechanical rather
 * than principled: a card is one element, so the screen that owns the routing can wrap it in
 * an anchor from outside and this layer never learns where it goes. A table cell cannot be
 * wrapped from outside without putting an anchor around a `<tr>`, which is not valid HTML.
 * The alternative — emit an ordinal and let the page navigate imperatively — is what the
 * "no router" rule is really aimed at, and it would cost the thing an anchor is for: middle
 * click, open in a new tab, copy link, and a status bar that shows where the row goes.
 */
@Component({
  selector: 'cd-version-table',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmTableImports, RouterLink, RelativeTimePipe, StatusBadge],
  template: `
    <div hlmTableContainer class="border-border bg-card rounded-xl border">
      <table
        hlmTable
        class="[&_td:first-child]:ps-4 [&_td:last-child]:pe-4 [&_th:first-child]:ps-4 [&_th:last-child]:pe-4"
      >
        <caption hlmCaption class="border-border mt-0 border-t px-4 py-3 text-left">
          Every version registered against this subject, newest first.
          <code class="font-mono">latest</code>
          is the pointer the registry gates, not the highest ordinal.
        </caption>
        <thead hlmTHead>
          <tr hlmTr>
            <th hlmTh scope="col">Version</th>
            <th hlmTh scope="col">Semantic</th>
            <th hlmTh scope="col">Schema</th>
            <th hlmTh scope="col">Registered</th>
            <th hlmTh scope="col">By</th>
            <th hlmTh scope="col">Status</th>
          </tr>
        </thead>
        <tbody hlmTBody>
          @for (row of rows(); track row.version.ordinal) {
            <tr hlmTr>
              <td hlmTd class="font-mono font-medium">
                <a
                  [routerLink]="['/subjects', subject(), 'versions', row.version.ordinal]"
                  class="hover:text-primary focus-visible:ring-ring rounded-sm focus-visible:ring-2 focus-visible:outline-none"
                >
                  v{{ row.version.ordinal }}
                </a>
                @if (row.isLatest) {
                  <span
                    class="border-border text-muted-foreground ms-2 rounded-full border px-2 py-0.5 font-mono text-xs"
                  >
                    latest
                  </span>
                }
              </td>
              <td hlmTd class="font-mono">{{ row.version.semanticVersion ?? '—' }}</td>
              <!--
                Truncated to the first 12 hex characters, with the whole id in the title. A
                content-addressed id is 32 characters of noise that would dominate the row,
                and the prefix is what people actually compare against a log line.
              -->
              <td hlmTd class="font-mono text-xs" [title]="row.version.schemaId">
                {{ row.version.schemaId.slice(0, 12) }}…
              </td>
              <td hlmTd [title]="row.version.registeredAt.toISOString()">
                {{ row.version.registeredAt | cdRelativeTime }}
              </td>
              <td hlmTd>{{ row.version.registeredBy }}</td>
              <td hlmTd class="space-x-1">
                <cd-status-badge [tone]="row.tone">{{ row.version.status }}</cd-status-badge>
                @if (row.version.deprecated) {
                  <cd-status-badge tone="neutral">DEPRECATED</cd-status-badge>
                }
              </td>
            </tr>
          }
        </tbody>
      </table>
    </div>
  `,
})
export class VersionTable {
  readonly subject = input.required<string>();
  readonly versions = input.required<readonly SchemaVersion[]>();

  /**
   * The ordinal `latest` resolves to, or null.
   *
   * Passed in rather than computed here. The gate means `latest` is the server's choice and
   * not `max(ordinal)` (ADR-017); a presentational component that worked it out would be
   * guessing, and would badge the wrong row the moment a version is awaiting approval.
   */
  readonly latest = input<number | null>(null);

  protected readonly rows = computed<readonly VersionRow[]>(() =>
    this.versions().map((version) => ({
      version,
      tone: STATUS_TONES[version.status],
      isLatest: version.ordinal === this.latest(),
    })),
  );
}
