import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { HlmBadge } from '@spartan-ng/helm/badge';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { latestVersion, pendingVersions, type Subject } from '../../../domain/registry/subject';
import { RelativeTimePipe } from '../../../shared/pipes/relative-time-pipe';

interface SubjectRow {
  readonly subject: Subject;
  readonly latestOrdinal: string;
  readonly latestSemver: string | null;
  readonly registeredAt: Date | null;
  readonly pending: number;
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
 */
@Component({
  selector: 'cd-subject-table',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmTableImports, HlmBadge, RouterLink, RelativeTimePipe],
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
          wrong. The mt-0 cancels the helm default's top margin, which was written for a
          caption sitting outside a panel; caption-bottom on the table puts it below the
          rows, so it reads as the panel's footer.
        -->
        <caption hlmCaption class="border-border mt-0 border-t px-4 py-3 text-left">
          Subjects in this environment, with the version the
          <code class="font-mono">latest</code>
          pointer resolves to.
        </caption>
        <thead hlmTHead>
          <tr hlmTr>
            <th hlmTh scope="col">Subject</th>
            <th hlmTh scope="col">Format</th>
            <th hlmTh scope="col">Owner</th>
            <th hlmTh scope="col">Latest</th>
            <th hlmTh scope="col">Registered</th>
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
              </td>
              <td hlmTd [title]="row.registeredAt?.toISOString() ?? ''">
                {{ row.registeredAt | cdRelativeTime }}
              </td>
              <td hlmTd class="space-x-1">
                @if (row.subject.lifecycle !== 'ACTIVE') {
                  <span hlmBadge variant="secondary">{{ row.subject.lifecycle }}</span>
                }
                @if (row.pending > 0) {
                  <span hlmBadge variant="destructive"> {{ row.pending }} awaiting approval </span>
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

      return {
        subject,
        // An em dash, not "0" or "none": a subject with no active version is the ordinary
        // state right after creation, and a zero reads like a count that went wrong.
        latestOrdinal: latest === null ? '—' : `v${latest.ordinal}`,
        latestSemver: latest?.semanticVersion ?? null,
        registeredAt: latest?.registeredAt ?? null,
        pending: pendingVersions(subject).length,
      };
    }),
  );
}
