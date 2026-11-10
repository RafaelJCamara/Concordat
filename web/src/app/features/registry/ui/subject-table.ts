import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { latestVersion, pendingVersions, type Subject } from '../../../domain/registry/subject';
import { RelativeTimePipe } from '../../../shared/pipes/relative-time-pipe';
import { Icon } from '../../../shared/ui/icon/icon';
import { StatusBadge } from '../../../shared/ui/status-badge/status-badge';

interface SubjectRow {
  readonly subject: Subject;
  readonly latestOrdinal: string;
  readonly latestSemver: string | null;
  /** When the latest version was registered: when the subject last changed, which is not its age. */
  readonly registeredAt: Date | null;
  /** When the subject was created (ADR-049): what the list is ordered by. Null from a registry that predates it. */
  readonly createdAt: Date | null;
  /** The created date to the minute, in UTC, as text: the order made visible, which "5 minutes ago" is not. */
  readonly createdText: string;
  readonly pending: number;
  /**
   * Where the pill goes: the approvals screen for this subject, and for the one held version when there
   * is exactly one (ADR-052). With several the screen lists them, filtered to the subject, and the reader
   * picks.
   */
  readonly review: { readonly subject: string; readonly version?: number };
  /**
   * What a screen reader is told the pill does. Begins with the words the pill shows, so that the name
   * contains the label (WCAG 2.5.3) and somebody who speaks what they see to a voice control reaches it.
   */
  readonly reviewLabel: string;
}

/** `2026-11-09 14:03 UTC`: sorts as it reads, and the zone is stated because the relative time beside it is local. */
function minuteInUtc(date: Date): string {
  return `${date.toISOString().slice(0, 16).replace('T', ' ')} UTC`;
}

/**
 * The subject list, as a table.
 *
 * Presentational: inputs in, nothing else. No store and no HTTP — the ESLint boundaries rule
 * enforces both, and the reason is that this is the layer a design change rewrites. A
 * component that also knows how to fetch cannot be restyled without a regression test for
 * fetching.
 *
 * `routerLink` is the one exception, and it is markup rather than behaviour: see the note on
 * `VersionTable` for why a table cell cannot take its anchor from the screen above it the way
 * `SubjectCard` does.
 *
 * **The rows are in the order they were given, which is newest first** (ADR-049): the registry
 * orders them and the table does not sort. It says so, in the caption and in the `Created` column's
 * `aria-sort`, because an order nobody can see is an order nobody can check.
 */
@Component({
  selector: 'cd-subject-table',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmTableImports, Icon, RouterLink, RelativeTimePipe, StatusBadge],
  template: `
    <!--
      The card is the prototype's row treatment: every list on every screen there sits on a
      raised, hairline-bordered panel rather than directly on the page. A bare table reads
      as a report; the panel is most of what makes it read as an application.
    -->
    <div hlmTableContainer class="border-border bg-card rounded-xl border">
      <!--
        Helm's cells carry p-2, which is right for a table on a plain page and leaves the
        outermost column flush against the panel's edge once there is one. The two rules
        below inset only the first and last cell, so the grid keeps its own rhythm inside.
      -->
      <table
        hlmTable
        class="[&_td:first-child]:ps-4 [&_td:last-child]:pe-4 [&_th:first-child]:ps-4 [&_th:last-child]:pe-4"
      >
        <!--
          Kept visible rather than made screen-reader-only. It is the only place that says
          "Latest" is a pointer the server gates rather than the highest number present
          (ADR-017), which is the one thing about this table a reader can otherwise get
          wrong, and the only place that says what order the rows are in (ADR-049). The mt-0
          cancels the helm default's top margin, which was written for a caption sitting
          outside a panel; caption-bottom on the table puts it below the rows, so it reads as
          the panel's footer.
        -->
        <caption hlmCaption class="border-border mt-0 border-t px-4 py-3 text-left">
          Newest first: ordered by when each subject was created, then by name. The
          <code class="font-mono">latest</code>
          column is the version the pointer resolves to.
        </caption>
        <thead hlmTHead>
          <tr hlmTr>
            <th hlmTh scope="col">Subject</th>
            <th hlmTh scope="col">Format</th>
            <th hlmTh scope="col">Owner</th>
            <th hlmTh scope="col">Latest</th>
            <!--
              The column the list is ordered by, and said to be (aria-sort), so that "newest first" can be
              checked against the dates in the rows and not only read in the footer. It is the subject's own
              creation date: the 'Registered' column that stood here was the latest version's, which moves
              whenever somebody registers a version and is not how old the subject is.
            -->
            <th hlmTh scope="col" aria-sort="descending">Created</th>
            <th hlmTh scope="col">State</th>
          </tr>
        </thead>
        <tbody hlmTBody>
          @for (row of rows(); track row.subject.name) {
            <tr hlmTr>
              <td hlmTd class="font-mono font-medium">
                <a
                  [routerLink]="['/subjects', row.subject.name]"
                  class="hover:text-primary focus-visible:ring-ring rounded-sm focus-visible:ring-2 focus-visible:outline-none"
                >
                  {{ row.subject.name }}
                </a>
              </td>
              <td hlmTd>{{ row.subject.format }}</td>
              <td hlmTd>{{ row.subject.owner }}</td>
              <td hlmTd>
                {{ row.latestOrdinal }}
                @if (row.latestSemver) {
                  <span class="text-muted-foreground">· {{ row.latestSemver }}</span>
                }
                <!--
                  What the old 'Registered' column held, kept where it is read: when that version was
                  registered, which is when the subject last changed. Beside the version it describes, and
                  not in a column that looked like the subject's age.
                -->
                @if (row.registeredAt) {
                  <span
                    class="text-muted-foreground block text-xs"
                    data-testid="registered"
                    [title]="'Registered ' + row.registeredAt.toISOString()"
                  >
                    registered {{ row.registeredAt | cdRelativeTime }}
                  </span>
                }
              </td>
              <td hlmTd class="whitespace-nowrap" data-testid="created">
                @if (row.createdAt) {
                  <time
                    [attr.datetime]="row.createdAt.toISOString()"
                    [title]="row.createdAt.toISOString()"
                  >
                    {{ row.createdAt | cdRelativeTime }}
                  </time>
                  <span class="text-muted-foreground block text-xs">{{ row.createdText }}</span>
                } @else {
                  <span
                    class="text-muted-foreground"
                    title="This registry does not report when a subject was created"
                    >—</span
                  >
                }
              </td>
              <!--
                Through StatusBadge rather than helm's badge directly, which is not a styling
                preference: this cell asked for the destructive variant on a held version, and
                red is what this app paints REJECTED. The list screen — the one most people
                arrive on — was telling them a change waiting for a decision had been refused.
                A tone named for the reading cannot make that mistake quietly.
              -->
              <td hlmTd class="space-x-1">
                @if (row.subject.lifecycle !== 'ACTIVE') {
                  <cd-status-badge tone="neutral">{{ row.subject.lifecycle }}</cd-status-badge>
                }
                @if (row.pending > 0) {
                  <!--
                    A link, because it goes somewhere: the held version waiting on a decision, on the
                    approvals screen (ADR-052). A real anchor and not a clickable span, so it is in the tab
                    order, opens in a new tab and is announced as a link. The pill inside is the same
                    StatusBadge every other state is, and not a second one; what says it can be clicked is
                    the ring on hover and focus, the underlined words and the arrow, none of which the inert
                    lifecycle badge beside it has. Its name starts with the words it shows, so somebody who
                    speaks them to a voice control reaches it.
                  -->
                  <a
                    data-testid="review-link"
                    [attr.aria-label]="row.reviewLabel"
                    [routerLink]="['/approvals']"
                    [queryParams]="row.review"
                    class="group focus-visible:ring-ring hover:ring-warning/50 inline-flex rounded-full outline-none transition-shadow hover:ring-2 focus-visible:ring-2"
                  >
                    <cd-status-badge tone="warning">
                      <span
                        class="underline-offset-2 group-hover:underline group-focus-visible:underline"
                        >{{ row.pending }} awaiting approval</span
                      >
                      <cd-icon name="arrow-right" size="0.75rem" />
                    </cd-status-badge>
                  </a>
                }
              </td>
            </tr>
          }
        </tbody>
      </table>
    </div>
  `,
})
export class SubjectTable {
  readonly subjects = input.required<readonly Subject[]>();

  protected readonly rows = computed<readonly SubjectRow[]>(() =>
    this.subjects().map((subject) => {
      const latest = latestVersion(subject);
      const held = pendingVersions(subject);
      const only = held.length === 1 ? held[0] : undefined;

      return {
        subject,
        // An em dash, not "0" or "none": a subject with no active version is the ordinary
        // state right after creation, and a zero reads like a count that went wrong.
        latestOrdinal: latest === null ? '—' : `v${latest.ordinal}`,
        latestSemver: latest?.semanticVersion ?? null,
        registeredAt: latest?.registeredAt ?? null,
        createdAt: subject.createdAt ?? null,
        createdText: subject.createdAt ? minuteInUtc(subject.createdAt) : '',
        pending: held.length,
        review:
          only === undefined
            ? { subject: subject.name }
            : { subject: subject.name, version: only.ordinal },
        reviewLabel: `${held.length} awaiting approval: review the held ${
          held.length === 1 ? 'version' : 'versions'
        } of ${subject.name}`,
      };
    }),
  );
}
