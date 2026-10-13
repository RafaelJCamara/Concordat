import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { HlmButton } from '@spartan-ng/helm/button';
import type { SchemaVersion } from '../../../domain/registry/subject';
import { StatusBadge } from '../../../shared/ui/status-badge/status-badge';

/**
 * Starting the next version from an earlier one, or saying there is nothing to start from.
 *
 * <b>"No versions yet" is a state, not an absence.</b> On a subject with nothing registered
 * the control to clone from has no meaning, so it is not drawn — and what replaces it says
 * what is about to happen (this will be v1) and that the next move is to write one, so the
 * empty editor underneath reads as an invitation rather than as something that failed to
 * load.
 *
 * <b>Replacing what is in the editor takes two clicks.</b> Loading an earlier version over
 * work in progress discards that work, and nothing here can bring it back. The first click
 * asks, in words that name what will be lost; only the second does it. When the editor is
 * empty there is nothing to lose and it is one click.
 */
@Component({
  selector: 'cd-version-starter',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmButton, StatusBadge],
  host: { class: 'block' },
  template: `
    @if (versions().length === 0) {
      <div class="border-border bg-card rounded-lg border p-4 text-sm">
        <p class="text-foreground font-medium">No versions yet</p>
        <p class="text-muted-foreground mt-1">
          This will be <span class="font-mono">v1</span>. There is nothing to start from, so write
          the schema below or paste one in.
        </p>
      </div>
    } @else {
      <div class="space-y-2">
        <div class="flex flex-wrap items-end gap-2">
          <div class="space-y-1.5">
            <label class="text-sm font-medium" for="starting-point">Start from</label>
            <select
              id="starting-point"
              class="border-input bg-background focus-visible:ring-ring flex h-9 rounded-md border px-3 py-1 font-mono text-sm focus-visible:ring-1 focus-visible:outline-none"
              [value]="selected()"
              (change)="choose($event)"
            >
              @for (version of versions(); track version.ordinal) {
                <option [value]="version.ordinal" [selected]="version.ordinal === selected()">
                  v{{ version.ordinal }}{{ describe(version) }}
                </option>
              }
            </select>
          </div>

          @if (!asking()) {
            <button
              hlmBtn
              type="button"
              variant="outline"
              size="sm"
              [disabled]="loading()"
              (click)="load()"
            >
              {{ loading() ? 'Loading…' : 'Load into the editor' }}
            </button>
          } @else {
            <div
              class="flex flex-wrap items-center gap-2"
              role="group"
              aria-label="Confirm replacing the editor's content"
            >
              <span class="text-sm">This replaces what you have written. Replace it?</span>
              <button hlmBtn type="button" variant="destructive" size="sm" (click)="confirm()">
                Replace
              </button>
              <button hlmBtn type="button" variant="ghost" size="sm" (click)="asking.set(false)">
                Keep mine
              </button>
            </div>
          }
        </div>

        @if (held(); as ordinal) {
          <p class="text-muted-foreground text-xs">
            <cd-status-badge tone="warning">Awaiting approval</cd-status-badge>
            v{{ ordinal }} is a proposal that is not live yet. Starting from it revises that
            proposal; starting from the current version replaces it.
          </p>
        }

        @if (failure(); as message) {
          <p class="text-destructive text-xs" role="alert">
            The document could not be loaded: {{ message }}
          </p>
        }
      </div>
    }
  `,
})
export class VersionStarter {
  /** Every version, newest first. */
  readonly versions = input.required<readonly SchemaVersion[]>();

  /** The version offered first: the one consumers resolve. */
  readonly defaultOrdinal = input<number | null>(null);

  /** Whether the editor holds anything that loading would overwrite. */
  readonly editorHasContent = input(false);

  readonly loading = input(false);

  /** Why the last load failed, in the registry's words. */
  readonly failure = input<string | null>(null);

  /** The chosen version, once confirmed if confirmation was needed. */
  readonly startFrom = output<number>();

  /** Set once somebody picks something; until then the default stands. */
  private readonly picked = signal<number | null>(null);

  protected readonly asking = signal(false);

  protected readonly selected = computed(
    () => this.picked() ?? this.defaultOrdinal() ?? this.versions()[0]?.ordinal ?? 0,
  );

  /** The ordinal of the chosen version when it is a proposal awaiting a decision. */
  protected readonly held = computed(() => {
    const version = this.versions().find((v) => v.ordinal === this.selected());

    return version?.status === 'AWAITING_APPROVAL' ? version.ordinal : null;
  });

  protected describe(version: SchemaVersion): string {
    const parts: string[] = [];

    if (version.semanticVersion !== null) {
      parts.push(version.semanticVersion);
    }

    if (version.ordinal === this.defaultOrdinal()) {
      parts.push('current');
    }

    if (version.status !== 'ACTIVE') {
      parts.push(version.status.toLowerCase().replace('_', ' '));
    }

    return parts.length === 0 ? '' : ` · ${parts.join(' · ')}`;
  }

  protected choose(event: Event): void {
    this.picked.set(Number((event.target as HTMLSelectElement).value));
    // A confirmation was for the version that was chosen when it was asked.
    this.asking.set(false);
  }

  protected load(): void {
    if (this.editorHasContent()) {
      this.asking.set(true);
      return;
    }

    this.startFrom.emit(this.selected());
  }

  protected confirm(): void {
    this.asking.set(false);
    this.startFrom.emit(this.selected());
  }
}
