import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type { PayloadCheck } from '../../../domain/registry/schema-check';

/**
 * What the client-side check said about a sample message, and whether it could say anything.
 *
 * <b>Five states that must not read alike.</b> The message conforms; it does not; it is not
 * JSON at all; it could not be judged; or the check itself failed. "Could not be judged" and
 * "failed" are the dangerous pair: both look like an empty result, and an empty result reads as
 * "nothing wrong". Each says what happened in words instead.
 *
 * <b>Every one of them ends in the same fact: the registry decides.</b> The check runs `ajv`, a
 * different engine from the registry's, and they can disagree about `format` and about an
 * unusual keyword. So a pass is "conforms under ajv", and the footer says what that is worth.
 *
 * Announced through `role="status"` so a screen reader hears the answer arrive; without it the
 * button press appears to do nothing.
 */
@Component({
  selector: 'cd-check-result',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block' },
  template: `
    <div role="status" class="space-y-2 text-sm">
      @if (checking()) {
        <p class="text-muted-foreground">Checking…</p>
      } @else if (failure(); as message) {
        <p class="text-warning">
          Could not check here: {{ message }}
          <span class="text-muted-foreground">The registry will still judge the schema.</span>
        </p>
      } @else if (checked()) {
        @if (skipped(); as reason) {
          <p class="text-warning">The schema was not checked. {{ reason }}</p>
        } @else if (adviceCount() === 0) {
          <p class="text-success">
            Nothing to flag: the schema is valid under JSON Schema draft 2020-12.
          </p>
        } @else {
          <p class="text-warning">
            {{ adviceCount() }}
            {{ adviceCount() === 1 ? 'thing' : 'things' }} to look at, listed above. The registry
            accepts a schema like this; it is only worth a second look.
          </p>
        }

        @if (payload(); as sample) {
          @switch (sample.kind) {
            @case ('valid') {
              <p class="text-success">The sample message conforms to the schema.</p>
            }
            @case ('invalid') {
              <div>
                <p class="text-destructive">The sample message does not conform:</p>
                <ul class="mt-1 space-y-1">
                  @for (error of sample.errors; track $index) {
                    <li class="text-xs">
                      <code class="font-mono">{{
                        error.path === '' ? '(the message)' : error.path
                      }}</code>
                      <span class="text-muted-foreground"> {{ error.message }}</span>
                    </li>
                  }
                </ul>
                @if (sample.omitted > 0) {
                  <p class="text-muted-foreground mt-1 text-xs">and {{ sample.omitted }} more.</p>
                }
              </div>
            }
            @case ('not_json') {
              <p class="text-destructive">
                The sample message is not well-formed JSON: {{ sample.message }}
              </p>
            }
            @case ('unavailable') {
              <p class="text-warning">
                The sample message could not be checked. {{ sample.reason }}
              </p>
            }
          }
        }

        <p class="text-muted-foreground text-xs">
          Checked here with ajv, which is not the engine the registry validates messages with. They
          can disagree about <code class="font-mono">format</code> and about unusual keywords, so
          treat this as advice before a round trip, not as the verdict.
        </p>
      } @else if (stale()) {
        <p class="text-muted-foreground text-xs">
          The document or the sample changed since the last check. Validate again to see it.
        </p>
      }
    </div>
  `,
})
export class CheckResult {
  readonly checking = input(false);

  /** Why the check could not run, if it could not. */
  readonly failure = input<string | null>(null);

  /** Whether there is a current answer to show. */
  readonly checked = input(false);

  /** Whether there was an answer, and the text has since moved on. */
  readonly stale = input(false);

  /** Why the schema itself was not looked at, if it was not. */
  readonly skipped = input<string | null>(null);

  /** How many findings about the schema the answer holds. */
  readonly adviceCount = input(0);

  readonly payload = input<PayloadCheck | null>(null);
}
