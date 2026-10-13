import { ChangeDetectionStrategy, Component, effect, inject, untracked } from '@angular/core';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import {
  AUDIT_ACTIONS,
  AUDIT_DEFAULT_LIMIT,
  AUDIT_MAX_LIMIT,
  type AuditAction,
  type AuditQuery,
} from '../../../domain/governance/audit';
import { AuditStore } from '../application/audit-store';
import { AuditTable } from '../ui/audit-table';

const INPUT =
  'border-input bg-background focus-visible:ring-ring flex h-9 w-full rounded-md border px-3 py-1 text-sm focus-visible:ring-1 focus-visible:outline-none';

/**
 * The audit trail: who did what to which thing, and when.
 *
 * <b>Readable by anyone who can read anything.</b> The endpoint admits any read scope, because
 * the trail spans every resource type and a reader should be able to browse all of it, same as
 * the registry itself. There are no controls here that write.
 *
 * <b>The filter is applied, not watched.</b> The form submits; nothing reacts to a keystroke.
 * Dates are whole local days — "since the 3rd" means from the start of that day where the reader
 * is, and "until the 5th" runs to the end of it — because a date picker that quietly meant UTC
 * midnight would put entries on the wrong side of a day for everyone not on UTC.
 *
 * <b>It says when the answer was cut off.</b> The registry returns the newest `limit` entries
 * with no cursor, so a full page and a complete one look the same unless somebody counts.
 */
@Component({
  selector: 'cd-audit-log-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [AuditStore],
  imports: [ReactiveFormsModule, HlmAlertImports, HlmButton, HlmSkeleton, AuditTable],
  template: `
    <section class="mx-auto w-full max-w-6xl space-y-6 p-6 lg:p-8">
      <header>
        <h1 class="text-foreground text-2xl font-bold tracking-tight">Audit log</h1>
        <p class="text-muted-foreground mt-1">
          What changed, and who changed it, in
          @if (form.controls.scope.value === 'environment') {
            <span class="font-mono">{{ environments.name() }}</span>
          } @else {
            every environment
          }
        </p>
      </header>

      <form
        [formGroup]="form"
        (ngSubmit)="apply()"
        class="border-border bg-card grid gap-4 rounded-xl border p-5 sm:grid-cols-2 lg:grid-cols-3"
      >
        <div class="space-y-1.5">
          <label class="text-sm font-medium" for="audit-scope">Where</label>
          <select id="audit-scope" formControlName="scope" [class]="input">
            <option value="environment">This environment</option>
            <option value="all">Every environment</option>
          </select>
        </div>

        <div class="space-y-1.5">
          <label class="text-sm font-medium" for="audit-action">Action</label>
          <select id="audit-action" formControlName="action" [class]="input">
            <option value="">Any action</option>
            @for (action of actions; track action) {
              <option [value]="action">{{ action }}</option>
            }
          </select>
        </div>

        <div class="space-y-1.5">
          <label class="text-sm font-medium" for="audit-target">Target</label>
          <input
            id="audit-target"
            formControlName="target"
            placeholder="orders.created"
            autocomplete="off"
            [class]="input + ' font-mono'"
          />
        </div>

        <div class="space-y-1.5">
          <label class="text-sm font-medium" for="audit-since">Since</label>
          <input id="audit-since" type="date" formControlName="since" [class]="input" />
        </div>

        <div class="space-y-1.5">
          <label class="text-sm font-medium" for="audit-until">Until</label>
          <input id="audit-until" type="date" formControlName="until" [class]="input" />
        </div>

        <div class="space-y-1.5">
          <label class="text-sm font-medium" for="audit-limit">Show</label>
          <select id="audit-limit" formControlName="limit" [class]="input">
            @for (option of limits; track option) {
              <option [value]="option">newest {{ option }}</option>
            }
          </select>
        </div>

        <div class="flex items-center gap-2 sm:col-span-2 lg:col-span-3">
          <button type="submit" hlmBtn size="sm">Apply</button>
          <button type="button" hlmBtn variant="ghost" size="sm" (click)="reset()">Reset</button>
        </div>
      </form>

      @if (store.error(); as error) {
        <div hlmAlert variant="destructive">
          <h2 hlmAlertTitle>Could not load the audit log</h2>
          <p hlmAlertDescription>{{ error.detail }}</p>
          <button hlmBtn hlmAlertAction variant="outline" size="sm" (click)="apply()">
            Try again
          </button>
        </div>
      } @else if (store.loading()) {
        <div class="space-y-2" aria-busy="true" aria-live="polite">
          @for (row of skeletonRows; track row) {
            <div hlmSkeleton class="h-10 w-full"></div>
          }
        </div>
      } @else if (store.isEmpty()) {
        <div class="border-border bg-card rounded-xl border p-10 text-center">
          <p class="font-medium">No entries match</p>
          <p class="text-muted-foreground mx-auto mt-1 max-w-xl text-sm">
            The trail records what happened, not what was attempted — a refused request leaves no
            entry, so an empty result for an action is not evidence that nobody tried it.
          </p>
        </div>
      } @else {
        <cd-audit-table [entries]="store.entries()" />

        @if (store.truncated()) {
          <p class="text-muted-foreground text-sm" role="status">
            Showing the newest {{ store.entries().length }}. There may be older entries; narrow the
            dates or the target to reach them.
          </p>
        }
      }
    </section>
  `,
})
export class AuditLogPage {
  protected readonly store = inject(AuditStore);
  protected readonly environments = inject(ActiveEnvironmentStore);

  protected readonly input = INPUT;
  protected readonly actions = AUDIT_ACTIONS;
  protected readonly limits = [AUDIT_DEFAULT_LIMIT, 250, AUDIT_MAX_LIMIT];
  protected readonly skeletonRows = [1, 2, 3, 4, 5];

  protected readonly form = inject(FormBuilder).nonNullable.group({
    scope: ['environment' as AuditQuery['scope']],
    action: [''],
    target: [''],
    since: [''],
    until: [''],
    limit: [String(AUDIT_DEFAULT_LIMIT)],
  });

  constructor() {
    // On arrival, and again when the environment changes under a screen scoped to it. The form
    // is read untracked: typing in it must not reload, only submitting does.
    effect(() => {
      this.environments.name();
      untracked(() => this.apply());
    });
  }

  protected apply(): void {
    this.store.load(this.query());
  }

  protected reset(): void {
    this.form.reset();
    this.apply();
  }

  private query(): AuditQuery {
    const value = this.form.getRawValue();

    return {
      scope: value.scope,
      action: value.action === '' ? null : (value.action as AuditAction),
      target: value.target,
      since: startOfLocalDay(value.since),
      until: endOfLocalDay(value.until),
      limit: Number(value.limit),
    };
  }
}

/** `2026-08-03` as the first instant of that day where the reader is, or null when blank. */
function startOfLocalDay(date: string): Date | null {
  return date === '' ? null : new Date(`${date}T00:00:00`);
}

/** `2026-08-05` as the last instant of that day where the reader is, or null when blank. */
function endOfLocalDay(date: string): Date | null {
  return date === '' ? null : new Date(`${date}T23:59:59.999`);
}
