import {
  ChangeDetectionStrategy,
  Component,
  computed,
  input,
  linkedSignal,
  type Signal,
} from '@angular/core';
import type { Divergence } from '../../../domain/registry/compatibility';
import type { DiffRow, SchemaDiffReady } from '../../../domain/registry/schema-diff';
import { StatusBadge } from '../../../shared/ui/status-badge/status-badge';

/**
 * What the registry said about a line, with the findings that say the same thing once.
 *
 * Protobuf and Avro report a change that breaks both ways twice, once per direction, in the same
 * words. On a line that is two identical boxes; here it is one, naming both directions.
 */
interface DrawnFinding {
  readonly kind: string;
  readonly directions: readonly string[];
  readonly surface: string;
  readonly message: string;
}

/**
 * The most lines one click on a fold shows.
 *
 * A fold can hide tens of thousands of lines, and drawing them all would be the one way left to
 * freeze the page on a schema the worker handled in milliseconds. A reader who wants one more
 * screenful gets it; a reader who wants fifty thousand lines wants the file.
 */
export const UNFOLD_CHUNK = 500;

/** A row as the template draws it: the diff's row, and the findings resolved from their ids. */
interface DrawnRow {
  readonly row: DiffRow;
  readonly findings: readonly DrawnFinding[];
}

/** A stretch of the view as the template draws it. */
type DrawnSegment =
  | { readonly kind: 'rows'; readonly rows: readonly DrawnRow[] }
  | { readonly kind: 'folded'; readonly index: number; readonly count: number };

/**
 * A held change to a schema as a pull request shows it: lines removed and lines added, the
 * unchanged ones folded away, and the registry's findings on the lines they are about (ADR-050).
 *
 * <b>Unified, not side by side.</b> A split view needs twice the width to say the same thing, and
 * a reviewer may well be on a phone. One column also gives a finding somewhere to sit — directly
 * under its line, at full width — which two columns would have to pick a side for.
 *
 * <b>A change is marked by more than colour.</b> Every row has a `+`, `-` or blank in its own
 * column, and a screen reader is told "Added" or "Removed" before the line; the tint behind it is
 * the third signal and the only one that depends on seeing the difference between red and green.
 * The line numbers and signs are `aria-hidden` and the words are not, because "line 12, line 12,
 * plus" read out ahead of every line is noise and "Added:" is not.
 *
 * <b>The text is rendered as text.</b> Schema text is another team's content, and each line is
 * interpolated into its own element, never into `innerHTML` (the same rule as `cd-schema-view`).
 *
 * <b>The region scrolls, the page does not.</b> A long line scrolls inside the diff, which is
 * focusable and named so a keyboard user can reach the overflow; the findings are sticky to the
 * left edge and as wide as the visible region, so they stay readable while the code scrolls
 * beneath them.
 */
@Component({
  selector: 'cd-schema-diff',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [StatusBadge],
  host: { class: 'block' },
  template: `
    @let diff = result();

    <p class="text-muted-foreground mb-2 text-xs" data-testid="diff-totals">
      <span class="text-success font-mono font-semibold">+{{ diff.added }}</span>
      {{ diff.added === 1 ? 'line' : 'lines' }} added,
      <span class="text-destructive font-mono font-semibold">−{{ diff.removed }}</span>
      removed
      @if (diff.unplaced.length > 0) {
        · {{ diff.unplaced.length }}
        {{ diff.unplaced.length === 1 ? 'finding is' : 'findings are' }} not tied to a line
      }
    </p>

    @if (diff.added === 0 && diff.removed === 0) {
      <p class="text-muted-foreground text-sm">
        No line differs once both documents are laid out the same way (indentation, key order, line
        endings). The registry still holds them as two schemas; the summary says what its rules make
        of that.
      </p>
    } @else {
      <!--
        A named, focusable region: the block scrolls, and a scrollable region a keyboard cannot
        focus is a WCAG 2.1.1 failure. Container type is on the scroller so a finding can be as
        wide as what is visible (100cqw) and not as wide as the longest line.
      -->
      <div
        role="region"
        tabindex="0"
        class="border-border bg-card focus-visible:ring-ring @container max-h-[40rem] overflow-auto rounded-lg border font-mono text-xs leading-5 focus-visible:ring-2 focus-visible:outline-none"
        [attr.aria-label]="label()"
        data-testid="diff-region"
      >
        <div class="min-w-max">
          @for (segment of drawn(); track $index) {
            @if (segment.kind === 'rows') {
              @for (item of segment.rows; track $index) {
                <div class="flex" [class]="TINTS[item.row.kind]" [attr.data-row]="item.row.kind">
                  <span
                    aria-hidden="true"
                    class="text-muted-foreground w-10 shrink-0 px-2 text-right select-none"
                    >{{ item.row.oldNumber }}</span
                  >
                  <span
                    aria-hidden="true"
                    class="text-muted-foreground w-10 shrink-0 px-2 text-right select-none"
                    >{{ item.row.newNumber }}</span
                  >
                  <span
                    aria-hidden="true"
                    class="w-5 shrink-0 text-center font-bold select-none"
                    [class]="SIGN_COLOURS[item.row.kind]"
                    >{{ SIGNS[item.row.kind] }}</span
                  >
                  <code class="pr-4 whitespace-pre"
                    ><span class="sr-only">{{ SPOKEN[item.row.kind] }}</span
                    >{{ item.row.text }}</code
                  >
                </div>

                @for (finding of item.findings; track $index) {
                  <div
                    role="note"
                    class="border-warning bg-muted text-foreground sticky left-0 my-1 ml-2 w-[calc(100cqw-1rem)] rounded-md @md:ml-[5.25rem] @md:w-[calc(100cqw-6.25rem)] border-l-4 px-3 py-2 font-sans whitespace-normal"
                    data-testid="diff-finding"
                  >
                    <span class="sr-only">Registry finding on the line above: </span>
                    <div class="flex flex-wrap items-center gap-2">
                      <cd-status-badge tone="neutral">{{ finding.kind }}</cd-status-badge>
                      <span class="text-muted-foreground font-mono text-xs">
                        {{ finding.directions.join(' + ') }} · {{ finding.surface }}
                      </span>
                    </div>
                    <p class="mt-1 text-sm leading-snug">{{ finding.message }}</p>
                  </div>
                }
              }
            } @else {
              <button
                type="button"
                class="text-muted-foreground hover:text-foreground focus-visible:ring-ring bg-muted/50 hover:bg-muted sticky left-0 flex w-[100cqw] items-center gap-2 px-3 py-1 text-left font-sans text-xs focus-visible:ring-2 focus-visible:outline-none focus-visible:ring-inset"
                [attr.aria-label]="
                  'Show ' + segment.count + ' unchanged ' + (segment.count === 1 ? 'line' : 'lines')
                "
                (click)="open(segment.index)"
              >
                <span aria-hidden="true">⋯</span>
                {{ segment.count }} unchanged {{ segment.count === 1 ? 'line' : 'lines' }}
              </button>
            }
          }
        </div>
      </div>
    }

    @if (unplaced().length > 0) {
      <div class="mt-3" data-testid="diff-unplaced">
        <h4 class="text-foreground mb-1 text-xs font-semibold">Findings not tied to a line</h4>
        <p class="text-muted-foreground mb-2 text-xs">
          The registry named a place in the schema that is not in either document as it was
          registered, so these have no line to sit on.
        </p>
        <ul class="space-y-2">
          @for (finding of unplaced(); track $index) {
            <li class="border-border rounded-lg border p-3">
              <div class="flex flex-wrap items-center gap-2">
                <code class="text-foreground font-mono text-xs break-all">{{ finding.path }}</code>
                <cd-status-badge tone="neutral">{{ finding.kind }}</cd-status-badge>
                <span class="text-muted-foreground font-mono text-xs">
                  {{ finding.direction }} · {{ finding.surface }}
                </span>
              </div>
              <p class="text-muted-foreground mt-1.5 text-sm">{{ finding.message }}</p>
            </li>
          }
        </ul>
      </div>
    }
  `,
})
export class SchemaDiff {
  /** The line diff, as the worker made it. */
  readonly result = input.required<SchemaDiffReady>();

  /**
   * The registry's findings for the pair. A finding's id in {@link result} is its index here.
   */
  readonly divergences = input.required<readonly Divergence[]>();

  /** What the scrolling region is called, for a screen reader: which two versions it compares. */
  readonly label = input<string>('Schema changes');

  /** What each kind of row shows in its sign column. */
  protected readonly SIGNS: Record<DiffRow['kind'], string> = {
    context: '',
    added: '+',
    removed: '-',
  };

  /**
   * The tint behind a row. The third signal, after the sign and the spoken word, and the only one
   * that needs to tell red from green. Whole class names, so the stylesheet generator finds them.
   */
  protected readonly TINTS: Record<DiffRow['kind'], string> = {
    context: '',
    added: 'bg-success/10',
    removed: 'bg-destructive/10',
  };

  protected readonly SIGN_COLOURS: Record<DiffRow['kind'], string> = {
    context: '',
    added: 'text-success',
    removed: 'text-destructive',
  };

  /** What a screen reader hears before the line. Context is plain text and needs no label. */
  protected readonly SPOKEN: Record<DiffRow['kind'], string> = {
    context: '',
    added: 'Added: ',
    removed: 'Removed: ',
  };

  /**
   * How many lines of each folded stretch the reader has opened, by position. Starts empty again
   * whenever a new diff arrives, because a position in the old one means nothing in the new.
   */
  private readonly opened = linkedSignal<SchemaDiffReady, ReadonlyMap<number, number>>({
    source: this.result,
    computation: () => new Map<number, number>(),
  });

  protected readonly drawn: Signal<readonly DrawnSegment[]> = computed(() => {
    const { segments, lines } = this.result();
    const findings = this.divergences();
    const opened = this.opened();

    const resolve = (row: DiffRow): DrawnRow => ({
      row,
      findings: merge(
        row.findings
          .map((id) => findings[id])
          .filter((finding): finding is Divergence => finding !== undefined),
      ),
    });

    return segments.flatMap((segment, index): DrawnSegment[] => {
      if (segment.kind === 'rows') {
        return [{ kind: 'rows', rows: segment.rows.map(resolve) }];
      }

      const revealed = Math.min(opened.get(index) ?? 0, segment.count);
      const folded: DrawnSegment[] =
        revealed < segment.count
          ? [{ kind: 'folded', index, count: segment.count - revealed }]
          : [];

      if (revealed === 0) {
        return folded;
      }

      // Unchanged lines are the same in both documents, so the held version's own are used.
      return [
        {
          kind: 'rows',
          rows: Array.from({ length: revealed }, (_, offset) =>
            resolve({
              kind: 'context',
              oldNumber: segment.oldStart + offset,
              newNumber: segment.newStart + offset,
              text: lines[segment.newStart - 1 + offset] ?? '',
              findings: [],
            }),
          ),
        },
        ...folded,
      ];
    });
  });

  /** The findings the diff could not put on a line. */
  protected readonly unplaced: Signal<readonly Divergence[]> = computed(() => {
    const findings = this.divergences();

    return this.result()
      .unplaced.map((id) => findings[id])
      .filter((finding): finding is Divergence => finding !== undefined);
  });

  /**
   * Opens the next stretch of a fold: all of it if it is short, and {@link UNFOLD_CHUNK} lines of
   * it if it is not, leaving the rest folded behind a button that says how many remain.
   */
  protected open(index: number): void {
    this.opened.update((current) =>
      new Map(current).set(index, (current.get(index) ?? 0) + UNFOLD_CHUNK),
    );
  }
}

/** Findings that differ only in direction become one, listing the directions in the order met. */
function merge(findings: readonly Divergence[]): readonly DrawnFinding[] {
  const merged = new Map<
    string,
    { -readonly [K in keyof DrawnFinding]: DrawnFinding[K] } & { directions: string[] }
  >();

  for (const finding of findings) {
    const key = JSON.stringify([finding.kind, finding.surface, finding.message]);
    const existing = merged.get(key);

    if (existing === undefined) {
      merged.set(key, {
        kind: finding.kind,
        surface: finding.surface,
        message: finding.message,
        directions: [finding.direction],
      });
    } else if (!existing.directions.includes(finding.direction)) {
      existing.directions.push(finding.direction);
    }
  }

  return [...merged.values()];
}
