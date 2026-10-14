import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { RouterLink } from '@angular/router';
import type { ConsumeBinding, PublishBinding } from '../../../domain/contracts/contract';

/**
 * A contract's bindings: what may be sent to an exchange, and what a queue should receive.
 *
 * Presentational. Every subject links to its own screen — that is the point of a binding, and a
 * reviewer reading "orders.* carries orders.created@>=2" is always one click from the subject.
 *
 * <b>Changing or removing is offered, never done, here.</b> When it is `editable` each binding gets a
 * button for each, and pressing one only says which binding and which action: the screen owns the
 * panel that asks the second question, so this stays a list.
 *
 * <b>Precedence is shown only when one was declared.</b> A null precedence is what makes an
 * overlap a conflict rather than a resolution, so its absence is information, but a column of
 * dashes on every row would bury the few bindings where it was set.
 */
@Component({
  selector: 'cd-binding-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [NgTemplateOutlet, RouterLink],
  host: { class: 'block space-y-6' },
  template: `
    <section aria-labelledby="publishes-heading">
      <h2 id="publishes-heading" class="text-foreground mb-2 text-sm font-semibold">
        Publishes · {{ publishes().length }}
      </h2>

      @if (publishes().length === 0) {
        <p class="text-muted-foreground text-sm">
          No publish bindings, so this contract governs no route yet.
        </p>
      } @else {
        <ul class="space-y-2">
          @for (binding of publishes(); track binding.id) {
            <li
              class="border-border bg-card rounded-lg border p-3"
              [class.border-primary]="binding.id === activeId()"
            >
              <div class="flex flex-wrap items-baseline gap-x-2 gap-y-1">
                <code class="text-foreground font-mono text-sm font-medium">
                  {{ binding.exchange }}
                </code>
                <code class="text-primary font-mono text-sm">{{ binding.routingKeyPattern }}</code>
                <span class="text-muted-foreground ms-auto font-mono text-xs">
                  vhost {{ binding.virtualHost }}
                  @if (binding.precedence !== null) {
                    · precedence {{ binding.precedence }}
                  }
                </span>
              </div>
              <ng-container
                [ngTemplateOutlet]="subjects"
                [ngTemplateOutletContext]="{ $implicit: binding.subjects }"
              />
              @if (editable()) {
                <ng-container
                  [ngTemplateOutlet]="actions"
                  [ngTemplateOutletContext]="{
                    kind: 'publish',
                    id: binding.id,
                    label: binding.exchange + ' / ' + binding.routingKeyPattern,
                  }"
                />
              }
            </li>
          }
        </ul>
      }
    </section>

    <section aria-labelledby="consumes-heading">
      <h2 id="consumes-heading" class="text-foreground mb-2 text-sm font-semibold">
        Consumes · {{ consumes().length }}
      </h2>

      @if (consumes().length === 0) {
        <p class="text-muted-foreground text-sm">No consume bindings.</p>
      } @else {
        <ul class="space-y-2">
          @for (binding of consumes(); track binding.id) {
            <li
              class="border-border bg-card rounded-lg border p-3"
              [class.border-primary]="binding.id === activeId()"
            >
              <div class="flex flex-wrap items-baseline gap-x-2 gap-y-1">
                <code class="text-foreground font-mono text-sm font-medium">
                  {{ binding.queue }}
                </code>
                <span class="text-muted-foreground ms-auto font-mono text-xs">
                  vhost {{ binding.virtualHost }}
                </span>
              </div>
              <ng-container
                [ngTemplateOutlet]="subjects"
                [ngTemplateOutletContext]="{ $implicit: binding.subjects }"
              />
              @if (editable()) {
                <ng-container
                  [ngTemplateOutlet]="actions"
                  [ngTemplateOutletContext]="{
                    kind: 'consume',
                    id: binding.id,
                    label: binding.queue,
                  }"
                />
              }
            </li>
          }
        </ul>
      }
    </section>

    <ng-template #actions let-kind="kind" let-id="id" let-label="label">
      <div class="mt-2 flex gap-1">
        <button
          type="button"
          class="text-primary hover:underline text-xs font-medium"
          [attr.aria-label]="'Change ' + label"
          (click)="changeRequested.emit({ kind, id })"
        >
          Change
        </button>
        <span class="text-muted-foreground text-xs" aria-hidden="true">·</span>
        <button
          type="button"
          class="text-destructive hover:underline text-xs font-medium"
          [attr.aria-label]="'Remove ' + label"
          (click)="removeRequested.emit({ kind, id })"
        >
          Remove
        </button>
      </div>
    </ng-template>

    <ng-template #subjects let-refs>
      <ul class="mt-2 flex flex-wrap gap-2">
        @for (ref of refs; track ref.subject + '@' + ref.selector) {
          <li>
            <a
              class="border-border hover:border-primary hover:text-primary inline-flex items-center rounded-md border px-2 py-1 font-mono text-xs transition-colors"
              [routerLink]="['/subjects', ref.subject]"
            >
              {{ ref.subject }}<span class="text-muted-foreground">&#64;{{ ref.selector }}</span>
            </a>
          </li>
        }
      </ul>
    </ng-template>
  `,
})
export class BindingList {
  readonly publishes = input.required<readonly PublishBinding[]>();
  readonly consumes = input.required<readonly ConsumeBinding[]>();

  /** Whether to offer Change and Remove (the caller holds `contract:write`). */
  readonly editable = input(false);

  /** The binding the screen is working on, drawn with the primary border. */
  readonly activeId = input<string | null>(null);

  /** Change was pressed for this binding. */
  readonly changeRequested = output<BindingRef>();

  /** Remove was pressed for this binding. */
  readonly removeRequested = output<BindingRef>();
}

/** Which binding a button was pressed for. */
export interface BindingRef {
  readonly kind: 'publish' | 'consume';
  readonly id: string;
}
