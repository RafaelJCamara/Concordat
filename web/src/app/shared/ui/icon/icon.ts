import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/**
 * Every icon the interface draws. Adding one means adding a case below.
 *
 * A closed union rather than `string`, so a typo is a compile error instead of an empty
 * 24×24 hole that only shows up in a screenshot.
 */
export type IconName =
  | 'arrow-right'
  | 'bell'
  | 'chevron-down'
  | 'chevron-left'
  | 'chevron-right'
  | 'circle-help'
  | 'clock'
  | 'database'
  | 'file-check'
  | 'file-json'
  | 'git-branch'
  | 'key-round'
  | 'layout-dashboard'
  | 'link-2'
  | 'log-out'
  | 'monitor'
  | 'moon'
  | 'plus'
  | 'rabbit'
  | 'refresh-cw'
  | 'search'
  | 'settings'
  | 'shield-check'
  | 'sun'
  | 'triangle-alert'
  | 'users';

/**
 * A lucide icon, drawn inline.
 *
 * The prototype used `lucide-react`. The geometry below is that same library's, at the
 * version the prototype pins (0.462.0, ISC), copied in rather than depended on — the
 * pattern this repo already follows for Spartan's helm components, and the reason DESIGN
 * §9 tells us to drop the prototype's unused dependency surface rather than mirror it. An
 * icon set is a few hundred bytes of path data per glyph; a package is a version to track.
 *
 * <b>Every glyph is a literal in the template, and none of it goes through `innerHTML`.</b>
 * The obvious implementation — a name-to-markup map bound with `[innerHTML]` — needs
 * `bypassSecurityTrustHtml` to survive Angular's sanitizer stripping SVG, and that call is
 * exactly the shape of the hole ADR-006 says this port exists to close. A `@switch` of
 * static markup compiles to the same DOM with nothing to bypass, and it costs verbosity in
 * one file that no one has to read twice.
 *
 * Sizing and colour come from the caller: the SVG is `w-full h-full` inside a box the
 * parent sizes, and `stroke="currentColor"` means it inherits text colour like a glyph.
 * That is what lets `text-primary` on a wrapper tint the icon with no icon-specific API.
 */
@Component({
  selector: 'cd-icon',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    // `inline-flex` and an explicit box, so an icon sits on a text baseline without the
    // descender gap an inline `svg` would leave.
    class: 'inline-flex shrink-0 items-center justify-center',
    '[style.width]': 'size()',
    '[style.height]': 'size()',
  },
  template: `
    <svg
      xmlns="http://www.w3.org/2000/svg"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      [attr.stroke-width]="weight()"
      stroke-linecap="round"
      stroke-linejoin="round"
      class="h-full w-full"
      [attr.aria-hidden]="label() === null ? true : null"
      [attr.role]="label() === null ? null : 'img'"
      [attr.aria-label]="label()"
    >
      @switch (name()) {
        @case ('arrow-right') {
          <path d="M5 12h14" />
          <path d="m12 5 7 7-7 7" />
        }
        @case ('bell') {
          <path d="M6 8a6 6 0 0 1 12 0c0 7 3 9 3 9H3s3-2 3-9" />
          <path d="M10.3 21a1.94 1.94 0 0 0 3.4 0" />
        }
        @case ('chevron-down') {
          <path d="m6 9 6 6 6-6" />
        }
        @case ('chevron-left') {
          <path d="m15 18-6-6 6-6" />
        }
        @case ('chevron-right') {
          <path d="m9 18 6-6-6-6" />
        }
        @case ('circle-help') {
          <circle cx="12" cy="12" r="10" />
          <path d="M9.09 9a3 3 0 0 1 5.83 1c0 2-3 3-3 3" />
          <path d="M12 17h.01" />
        }
        @case ('clock') {
          <circle cx="12" cy="12" r="10" />
          <polyline points="12 6 12 12 16 14" />
        }
        @case ('database') {
          <ellipse cx="12" cy="5" rx="9" ry="3" />
          <path d="M3 5V19A9 3 0 0 0 21 19V5" />
          <path d="M3 12A9 3 0 0 0 21 12" />
        }
        @case ('file-check') {
          <path d="M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7Z" />
          <path d="M14 2v4a2 2 0 0 0 2 2h4" />
          <path d="m9 15 2 2 4-4" />
        }
        @case ('file-json') {
          <path d="M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7Z" />
          <path d="M14 2v4a2 2 0 0 0 2 2h4" />
          <path d="M10 12a1 1 0 0 0-1 1v1a1 1 0 0 1-1 1 1 1 0 0 1 1 1v1a1 1 0 0 0 1 1" />
          <path d="M14 18a1 1 0 0 0 1-1v-1a1 1 0 0 1 1-1 1 1 0 0 1-1-1v-1a1 1 0 0 0-1-1" />
        }
        @case ('git-branch') {
          <line x1="6" x2="6" y1="3" y2="15" />
          <circle cx="18" cy="6" r="3" />
          <circle cx="6" cy="18" r="3" />
          <path d="M18 9a9 9 0 0 1-9 9" />
        }
        @case ('key-round') {
          <path
            d="M2.586 17.414A2 2 0 0 0 2 18.828V21a1 1 0 0 0 1 1h3a1 1 0 0 0 1-1v-1a1 1 0 0 1 1-1h1a1 1 0 0 0 1-1v-1a1 1 0 0 1 1-1h.172a2 2 0 0 0 1.414-.586l.814-.814a6.5 6.5 0 1 0-4-4z"
          />
          <circle cx="16.5" cy="7.5" r=".5" fill="currentColor" />
        }
        @case ('layout-dashboard') {
          <rect width="7" height="9" x="3" y="3" rx="1" />
          <rect width="7" height="5" x="14" y="3" rx="1" />
          <rect width="7" height="9" x="14" y="12" rx="1" />
          <rect width="7" height="5" x="3" y="16" rx="1" />
        }
        @case ('link-2') {
          <path d="M9 17H7A5 5 0 0 1 7 7h2" />
          <path d="M15 7h2a5 5 0 1 1 0 10h-2" />
          <line x1="8" x2="16" y1="12" y2="12" />
        }
        @case ('log-out') {
          <path d="M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4" />
          <polyline points="16 17 21 12 16 7" />
          <line x1="21" x2="9" y1="12" y2="12" />
        }
        @case ('monitor') {
          <rect width="20" height="14" x="2" y="3" rx="2" />
          <line x1="8" x2="16" y1="21" y2="21" />
          <line x1="12" x2="12" y1="17" y2="21" />
        }
        @case ('moon') {
          <path d="M12 3a6 6 0 0 0 9 9 9 9 0 1 1-9-9Z" />
        }
        @case ('plus') {
          <path d="M5 12h14" />
          <path d="M12 5v14" />
        }
        @case ('rabbit') {
          <path d="M13 16a3 3 0 0 1 2.24 5" />
          <path d="M18 12h.01" />
          <path
            d="M18 21h-8a4 4 0 0 1-4-4 7 7 0 0 1 7-7h.2L9.6 6.4a1 1 0 1 1 2.8-2.8L15.8 7h.2c3.3 0 6 2.7 6 6v1a2 2 0 0 1-2 2h-1a3 3 0 0 0-3 3"
          />
          <path d="M20 8.54V4a2 2 0 1 0-4 0v3" />
          <path d="M7.612 12.524a3 3 0 1 0-1.6 4.3" />
        }
        @case ('refresh-cw') {
          <path d="M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8" />
          <path d="M21 3v5h-5" />
          <path d="M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16" />
          <path d="M8 16H3v5" />
        }
        @case ('search') {
          <circle cx="11" cy="11" r="8" />
          <path d="m21 21-4.3-4.3" />
        }
        @case ('settings') {
          <path
            d="M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2z"
          />
          <circle cx="12" cy="12" r="3" />
        }
        @case ('shield-check') {
          <path
            d="M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z"
          />
          <path d="m9 12 2 2 4-4" />
        }
        @case ('sun') {
          <circle cx="12" cy="12" r="4" />
          <path d="M12 2v2" />
          <path d="M12 20v2" />
          <path d="m4.93 4.93 1.41 1.41" />
          <path d="m17.66 17.66 1.41 1.41" />
          <path d="M2 12h2" />
          <path d="M20 12h2" />
          <path d="m6.34 17.66-1.41 1.41" />
          <path d="m19.07 4.93-1.41 1.41" />
        }
        @case ('triangle-alert') {
          <path d="m21.73 18-8-14a2 2 0 0 0-3.48 0l-8 14A2 2 0 0 0 4 21h16a2 2 0 0 0 1.73-3" />
          <path d="M12 9v4" />
          <path d="M12 17h.01" />
        }
        @case ('users') {
          <path d="M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2" />
          <circle cx="9" cy="7" r="4" />
          <path d="M22 21v-2a4 4 0 0 0-3-3.87" />
          <path d="M16 3.13a4 4 0 0 1 0 7.75" />
        }
      }
    </svg>
  `,
})
export class Icon {
  readonly name = input.required<IconName>();

  /** Any CSS length. `1rem` tracks the surrounding text; a fixed `1.25rem` does not. */
  readonly size = input<string>('1.25rem');

  /**
   * An accessible name, for the rare icon that carries meaning on its own.
   *
   * Null by default, which marks the icon `aria-hidden`. That is the right default because
   * almost every icon here sits beside its own visible label — "Dashboard" next to the
   * dashboard glyph — and announcing both makes a screen reader say everything twice. Pass
   * a label only when the icon *is* the label, as on an icon-only button.
   */
  readonly label = input<string | null>(null);

  /**
   * Stroke width on lucide's 24px grid, where 2 is the drawn default.
   *
   * Worth turning down for a large icon: a stroke that reads as confident at 20px reads as
   * heavy at 48px, the same reason display type is tracked tighter than body type.
   */
  readonly weight = input<number>(2);
}
