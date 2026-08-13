import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
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
 * Presentational: inputs in, nothing else. No store, no HTTP, no router — the ESLint
 * boundaries rule enforces that, and the reason is that this is the layer a design change
 * rewrites. A component that also knows how to fetch cannot be restyled without a
 * regression test for fetching.
 */
@Component({
  selector: 'cd-subject-table',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmTableImports, HlmBadge, RelativeTimePipe],
  template: `
    <div hlmTableContainer>
      <table hlmTable>
        <caption hlmCaption>
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
              <td hlmTd class="font-mono font-medium">{{ row.subject.name }}</td>
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
