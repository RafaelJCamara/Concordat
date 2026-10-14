import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import {
  COMPATIBILITY_MODE_MEANING,
  COMPATIBILITY_SURFACE_MEANING,
} from '../../../domain/registry/compatibility';

/**
 * What the compatibility mode or surface chosen in a form means, in one sentence, always visible.
 *
 * <b>Visible text and `aria-describedby`, not a tooltip.</b> The prototype explained these on
 * hover. Hover does not exist on a phone, a `title` is not reachable from the keyboard and is read
 * by few screen readers, and an overlay that opens on focus covers the next field of a form on a
 * 390px screen. A sentence under the select is read on focus (the caller points the select's
 * `aria-describedby` at {@link textId}), seen on touch, and never in the way. The environment
 * settings already do this for the registration policy; this is the same pattern, so the two
 * selects beside it do not behave differently.
 *
 * <b>It never changes the select's accessible name.</b> The e2e specs find these selects by their
 * label (`getByLabel('Mode')`, which matches by substring), and a description is not a name, so a
 * sentence here cannot make that locator match twice.
 *
 * The wording is `COMPATIBILITY_MODE_MEANING` and `COMPATIBILITY_SURFACE_MEANING`: one place for it,
 * pinned against the token lists. An empty value (the "inherit" choice) or a token this build does
 * not know renders nothing, because the honest alternative to a sentence is none.
 */
@Component({
  selector: 'cd-compatibility-meaning',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block' },
  template: `
    <p [id]="textId()" class="text-muted-foreground text-xs break-words" [hidden]="!text()">
      {{ text() }}
    </p>
  `,
})
export class CompatibilityMeaning {
  /** Which axis the value is on. */
  readonly axis = input.required<'mode' | 'surface'>();

  /** The selected token, or an empty string or null for none. */
  readonly value = input<string | null>(null);

  /** The id of the sentence, for the select's `aria-describedby`. Unique on the page. */
  readonly textId = input.required<string>();

  protected readonly text = computed(() => {
    const value = this.value();
    const meanings: Readonly<Record<string, string>> =
      this.axis() === 'mode' ? COMPATIBILITY_MODE_MEANING : COMPATIBILITY_SURFACE_MEANING;

    return value !== null && Object.hasOwn(meanings, value) ? meanings[value] : '';
  });
}
