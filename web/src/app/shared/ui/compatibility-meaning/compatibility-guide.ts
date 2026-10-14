import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';
import { HlmButton } from '@spartan-ng/helm/button';
import {
  COMPATIBILITY_DIRECTION_MEANING,
  COMPATIBILITY_DIRECTIONS,
  COMPATIBILITY_FINDING_SURFACE_NOTE,
  COMPATIBILITY_MODE_MEANING,
  COMPATIBILITY_MODES,
  COMPATIBILITY_SURFACE_MEANING,
  COMPATIBILITY_SURFACES,
} from '../../../domain/registry/compatibility';
import { Icon } from '../icon/icon';

let nextId = 0;

interface Entry {
  readonly token: string;
  readonly meaning: string;
}

/**
 * What the compatibility terms on a screen mean, behind one button that opens them in the page.
 *
 * <b>A disclosure, not a tooltip.</b> It is a native `<button>` with `aria-expanded`, so it is
 * reached by Tab, opened by Enter or Space and by a tap, announced as collapsed or expanded, and
 * what it reveals is ordinary text in the flow of the page: nothing floats, so at 390px nothing can
 * hang off the edge of the screen or cover the field below it. A `title` attribute has none of
 * those properties, and an overlay that opens on hover has only the first. The button keeps one name
 * open and closed: `aria-expanded` is what says which, and a name that also changed would be
 * announced as two things at once.
 *
 * <b>It explains only the tokens it is given.</b> A subject pinned to `FULL_TRANSITIVE · SOURCE`
 * is told what those two mean, not all ten words. The caller passes what is on its screen, and a
 * token this build does not know is left out rather than guessed at, since the DTO guards already
 * refuse an unknown token before it reaches a screen.
 *
 * <b>Closed, it puts none of the tokens in the DOM.</b> The panel is created when opened. The e2e
 * specs find a policy by its exact text (`getByText('FULL_TRANSITIVE', { exact: true })`), which
 * matches hidden elements too, and a closed panel that held every token would make each of those
 * ambiguous.
 *
 * The wording is `COMPATIBILITY_*_MEANING` in the domain, one place, pinned there.
 */
@Component({
  selector: 'cd-compatibility-guide',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmButton, Icon],
  host: { class: 'block' },
  template: `
    <button
      hlmBtn
      type="button"
      variant="link"
      size="sm"
      class="h-auto gap-1.5 p-0 font-sans text-xs"
      [attr.aria-expanded]="open()"
      [attr.aria-controls]="open() ? panelId : null"
      (click)="open.set(!open())"
    >
      <cd-icon name="circle-help" size="0.875rem" />
      {{ label() }}
    </button>

    @if (open()) {
      <div
        [id]="panelId"
        class="border-border bg-muted/40 mt-2 space-y-4 rounded-lg border p-3 text-xs"
      >
        @for (group of groups(); track group.heading) {
          <section class="space-y-1.5">
            <h3 class="text-foreground font-medium">{{ group.heading }}</h3>
            @if (group.note) {
              <p class="text-muted-foreground">{{ group.note }}</p>
            }
            <dl class="space-y-1.5">
              @for (entry of group.entries; track entry.token) {
                <div>
                  <dt class="text-foreground font-mono break-all">{{ entry.token }}</dt>
                  <dd class="text-muted-foreground break-words">{{ entry.meaning }}</dd>
                </div>
              }
            </dl>
          </section>
        }
      </div>
    }
  `,
})
export class CompatibilityGuide {
  /** The modes on the screen. */
  readonly modes = input<readonly (string | null)[]>([]);

  /** The surfaces on the screen. */
  readonly surfaces = input<readonly (string | null)[]>([]);

  /** The directions on the screen, which are the ones a finding carries. */
  readonly directions = input<readonly (string | null)[]>([]);

  /**
   * Whether the surfaces are a finding's rather than a policy's.
   *
   * The same token means two things on one screen otherwise: a policy's `WIRE_JSON` is how much
   * must keep working, and a finding's is the narrowest thing it breaks.
   */
  readonly findings = input(false);

  /**
   * What the closed button says, for a screen with two guides, so a screen reader's list of
   * buttons does not read the same words twice for different things.
   */
  readonly label = input('What do these mean?');

  protected readonly open = signal(false);
  protected readonly panelId = `compatibility-guide-${nextId++}`;

  protected readonly groups = computed(() => {
    const groups: { heading: string; note: string; entries: readonly Entry[] }[] = [];

    const modes = entries(this.modes(), COMPATIBILITY_MODES, COMPATIBILITY_MODE_MEANING);
    const surfaces = entries(
      this.surfaces(),
      COMPATIBILITY_SURFACES,
      COMPATIBILITY_SURFACE_MEANING,
    );
    const directions = entries(
      this.directions(),
      COMPATIBILITY_DIRECTIONS,
      COMPATIBILITY_DIRECTION_MEANING,
    );

    if (modes.length > 0) {
      groups.push({ heading: 'Who breaks', note: '', entries: modes });
    }

    if (directions.length > 0) {
      groups.push({ heading: 'Which way it breaks', note: '', entries: directions });
    }

    if (surfaces.length > 0) {
      groups.push({
        heading: 'What breaks',
        note: this.findings() ? COMPATIBILITY_FINDING_SURFACE_NOTE : '',
        entries: surfaces,
      });
    }

    return groups;
  });
}

/**
 * The meanings of the tokens present, once each, in the order the vocabulary lists them, so a
 * screen's explanation reads the same whatever order its data arrived in.
 */
function entries(
  present: readonly (string | null)[],
  vocabulary: readonly string[],
  meanings: Readonly<Record<string, string>>,
): readonly Entry[] {
  return vocabulary
    .filter((token) => present.includes(token))
    .map((token) => ({ token, meaning: meanings[token] ?? '' }));
}
