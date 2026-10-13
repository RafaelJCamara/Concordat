import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import type { ConsumeBinding, PublishBinding } from '../../../domain/contracts/contract';

/**
 * A contract's bindings: what may be sent to an exchange, and what a queue should receive.
 *
 * Presentational. Every subject links to its own screen — that is the point of a binding, and a
 * reviewer reading "orders.* carries orders.created@>=2" is always one click from the subject.
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
          @for (binding of publishes(); track $index) {
            <li class="border-border bg-card rounded-lg border p-3">
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
          @for (binding of consumes(); track $index) {
            <li class="border-border bg-card rounded-lg border p-3">
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
            </li>
          }
        </ul>
      }
    </section>

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
}
