import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

/**
 * A subject name, wrapped where the name itself has a seam.
 *
 * <b>Truncating a dotted path removes the only part that identifies it.</b> `truncate` on
 * `acme.e2e.PaymentTaken` in a card that has run out of room yields `acme.e2e.P…` — the
 * namespace, which every neighbouring subject shares, survives intact, and the leaf, which is
 * the entire reason a reader is looking at the string, is what gets cut. Two subjects in the
 * same namespace truncate to the same label.
 *
 * So the name wraps instead, and it wraps at the dots: `<wbr>` marks each segment boundary as
 * a break opportunity, which is where a person reading a dotted path would break it. CSS has
 * no idea a dot is a seam — `break-words` alone would split `PaymentTa` / `ken` — and it stays
 * on as the backstop for the one case the seams cannot help with, a single segment longer than
 * the box.
 *
 * A component rather than a directive or a copied class list, because the rule is one fact
 * about the domain — a subject name is a dotted path — and it now holds on the card and the
 * detail heading alike. Tables are the deliberate exception: a cell that wraps ruins the
 * scanning a table exists for, so those keep `whitespace-nowrap` and scroll.
 */
@Component({
  selector: 'cd-subject-name',
  changeDetection: ChangeDetectionStrategy.OnPush,
  // The face is not the caller's choice. A dotted name is read character by character, and a
  // proportional font makes `acme.orders.v1` and `acme.0rders.v1` look alike.
  host: { class: 'font-mono break-words' },
  // Each segment gets its own element, which is not decoration: Angular collapses a run of
  // whitespace that shares a text node with an interpolation, and preserving the template's
  // own indentation as spaces inside a subject name would spell a different name. Whitespace
  // *between* elements is dropped outright, so the formatter can wrap this however it likes.
  template: `
    @for (segment of segments(); track $index) {
      <span>{{ segment }}</span
      ><wbr />
    }
  `,
})
export class SubjectName {
  readonly name = input.required<string>();

  /**
   * The name, cut after each dot.
   *
   * The separator stays on the segment it follows, so a break puts `acme.` at the end of the
   * line rather than starting the next one with an orphaned dot — the same convention a URL
   * or a file path is broken with.
   */
  protected readonly segments = computed(() =>
    this.name()
      .split(/(?<=\.)/)
      .filter((segment) => segment !== ''),
  );
}
