import { ChangeDetectionStrategy, Component, computed, effect, inject, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import type { SchemaVersion } from '../../../domain/registry/subject';
import { Icon } from '../../../shared/ui/icon/icon';
import { RelativeTimePipe } from '../../../shared/pipes/relative-time-pipe';
import { StatusBadge, type StatusTone } from '../../../shared/ui/status-badge/status-badge';
import { VersionDetailStore } from '../application/version-detail-store';
import { SchemaView } from '../ui/schema-view';

const STATUS_TONES: Record<SchemaVersion['status'], StatusTone> = {
  ACTIVE: 'success',
  AWAITING_APPROVAL: 'warning',
  REJECTED: 'destructive',
  DISMISSED: 'neutral',
};

/**
 * One version: who registered it, what it says, and what it superseded.
 *
 * The URL is `/subjects/:name/versions/:ordinal`, which is the link people actually paste.
 * `:ordinal` accepts the literal `latest` as well as a number, matching the API — a link to
 * `.../versions/latest` keeps meaning "whatever is current" after the next registration,
 * and resolving it goes through the subject's gated pointer rather than `max(ordinal)`
 * (ADR-017).
 */
@Component({
  selector: 'cd-version-detail-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [VersionDetailStore],
  imports: [
    RouterLink,
    HlmAlertImports,
    HlmButton,
    HlmSkeleton,
    Icon,
    RelativeTimePipe,
    SchemaView,
    StatusBadge,
  ],
  template: `
    <section class="mx-auto w-full max-w-6xl space-y-6 p-6 lg:p-8">
      <a
        [routerLink]="['/subjects', name()]"
        class="text-muted-foreground hover:text-foreground inline-flex items-center gap-1 text-sm transition-colors"
      >
        <cd-icon name="chevron-left" size="0.875rem" />
        <span class="font-mono">{{ name() }}</span>
      </a>

      @if (store.loading()) {
        <div class="space-y-4" aria-busy="true" aria-live="polite">
          <div hlmSkeleton class="h-9 w-64"></div>
          <div hlmSkeleton class="h-24 w-full rounded-xl"></div>
          <div hlmSkeleton class="h-96 w-full rounded-xl"></div>
        </div>
      } @else if (store.version(); as version) {
        <header class="flex flex-col gap-4 sm:flex-row sm:items-start sm:justify-between">
          <div>
            <h1 class="text-foreground font-mono text-2xl font-bold tracking-tight">
              v{{ version.ordinal }}
              @if (version.semanticVersion) {
                <span class="text-muted-foreground text-lg">· {{ version.semanticVersion }}</span>
              }
            </h1>
            <p class="text-muted-foreground mt-1 text-sm">
              Registered by {{ version.registeredBy }}
              <span [title]="version.registeredAt.toISOString()">
                {{ version.registeredAt | cdRelativeTime }}
              </span>
            </p>
          </div>

          <div class="flex shrink-0 flex-wrap items-center gap-2">
            <cd-status-badge [tone]="tone()">{{ version.status }}</cd-status-badge>
            @if (store.isLatest()) {
              <cd-status-badge tone="info">latest</cd-status-badge>
            }
            @if (version.deprecated) {
              <cd-status-badge tone="neutral">DEPRECATED</cd-status-badge>
            }
          </div>
        </header>

        @if (version.changelog) {
          <div class="border-border bg-card rounded-xl border p-5">
            <h2 class="text-muted-foreground mb-2 text-xs">Changelog</h2>
            <p class="text-foreground text-sm whitespace-pre-wrap">{{ version.changelog }}</p>
          </div>
        }

        <!--
          The "compare with the previous version" offer lands with CompatibilityDiffPage and
          its route, for the reason given on SubjectDetailPage: a link to a route that does
          not exist yet hits the wildcard and redirects to the dashboard. The store's
          previous() is already computed and waiting for it — and it is deliberately null on
          v1, because a permanently disabled compare control on the first version of every
          subject is furniture that never does anything.
        -->

        @if (store.loadingDocument()) {
          <div hlmSkeleton class="h-96 w-full rounded-xl" aria-busy="true"></div>
        } @else if (store.error(); as error) {
          <!--
            Rendered below the metadata rather than instead of it. The version's own facts
            came from a request that succeeded; only the document is missing, and blanking
            the whole screen would throw away what the reader can still use.
          -->
          <div hlmAlert variant="destructive">
            <h2 hlmAlertTitle>Could not load the schema document</h2>
            <p hlmAlertDescription>{{ error.detail }}</p>
            <button hlmBtn hlmAlertAction variant="outline" size="sm" (click)="reload()">
              Try again
            </button>
          </div>
        } @else if (store.document(); as document) {
          <cd-schema-view [document]="document" />
        }
      } @else if (store.error(); as error) {
        <div hlmAlert variant="destructive">
          <h2 hlmAlertTitle>Could not load this version</h2>
          <p hlmAlertDescription>{{ error.detail }}</p>
          <button hlmBtn hlmAlertAction variant="outline" size="sm" (click)="reload()">
            Try again
          </button>
        </div>
      }
    </section>
  `,
})
export class VersionDetailPage {
  /** The subject name, bound from the route path. */
  readonly name = input.required<string>();

  /** The ordinal, or the literal `latest`. A string because it is both. */
  readonly ordinal = input.required<string>();

  protected readonly store = inject(VersionDetailStore);
  protected readonly environments = inject(ActiveEnvironmentStore);

  protected readonly tone = computed<StatusTone>(() => {
    const version = this.store.version();
    return version === null ? 'neutral' : STATUS_TONES[version.status];
  });

  constructor() {
    effect(() => {
      this.environments.name();
      this.store.load({ subject: this.name(), ordinal: this.ordinal() });
    });
  }

  protected reload(): void {
    this.store.load({ subject: this.name(), ordinal: this.ordinal() });
  }
}
