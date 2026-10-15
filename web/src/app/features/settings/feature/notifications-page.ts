import { ChangeDetectionStrategy, Component, effect, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
import { IfScope } from '../../../core/auth/if-scope';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import { ENV_WRITE_SCOPES } from '../../../domain/identity/scope';
import {
  isAllEvents,
  NOTIFICATION_CHANNELS,
  NOTIFICATION_EVENT_MEANING,
  NOTIFICATION_EVENTS,
  type NotificationChannel,
  type NotificationEvent,
  type Subscription,
} from '../../../domain/notifications/subscription';
import { RelativeTimePipe } from '../../../shared/pipes/relative-time-pipe';
import { OneTimeSecret } from '../../../shared/ui/one-time-secret/one-time-secret';
import { StatusBadge } from '../../../shared/ui/status-badge/status-badge';
import { NotificationsStore } from '../application/notifications-store';
import { SettingsNav } from './settings-nav';

const INPUT =
  'border-input bg-background focus-visible:ring-ring flex h-9 w-full rounded-md border px-3 py-1 text-sm focus-visible:ring-1 focus-visible:outline-none';

/**
 * Who is told when something happens in this environment.
 *
 * <b>Readable by anyone who can read the environment, writable with `env:write`</b> (ADR-018):
 * the controls are absent without it, not disabled.
 *
 * <b>"No events ticked" means every event, and the form says so beside the boxes.</b> The
 * registry reads an empty list as "all" so that a subscription can never be enabled and silently
 * deliver nothing — and a form that left the boxes unexplained would invite exactly that
 * misreading in the other direction.
 *
 * <b>A parked count is surfaced above the list.</b> A webhook whose receiver is down is retried
 * and then parked, quietly, and the outbox depth is the only place it ever shows.
 *
 * <b>The webhook signing secret is shown once.</b> Every delivery carries an HMAC of its body
 * keyed on it, so the receiver can tell this registry sent it from anything else that found the
 * URL; the registry cannot show it again.
 */
@Component({
  selector: 'cd-notifications-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [NotificationsStore],
  imports: [
    ReactiveFormsModule,
    HlmAlertImports,
    HlmButton,
    HlmSkeleton,
    IfScope,
    OneTimeSecret,
    RelativeTimePipe,
    SettingsNav,
    StatusBadge,
  ],
  template: `
    <section class="mx-auto w-full max-w-4xl space-y-6 p-6 lg:p-8">
      <header>
        <h1 class="text-foreground text-2xl font-bold tracking-tight">Settings</h1>
        <p class="text-muted-foreground mt-1">
          <span class="font-mono">{{ environments.name() }}</span>
        </p>
      </header>

      <cd-settings-nav />

      @if (store.signingSecret(); as issued) {
        <cd-one-time-secret
          heading="Configure this signing secret on the receiver — it will not be shown again"
          label="Webhook signing secret"
          [secret]="issued.secret"
          (dismissed)="store.dismiss()"
        >
          Every delivery to
          <span class="text-foreground font-mono">{{ issued.endpoint }}</span>
          carries an <code>X-Concordat-Signature</code> header: an HMAC-SHA256 of the body keyed on
          this secret. The receiver should check it, so it can tell this registry sent a request
          from anything else that found the URL.
        </cd-one-time-secret>
      }

      @if (store.outbox(); as outbox) {
        @if (outbox.parked > 0) {
          <div hlmAlert variant="destructive" role="alert">
            <h2 hlmAlertTitle>
              {{ outbox.parked }}
              {{ outbox.parked === 1 ? 'notification was' : 'notifications were' }}
              given up on
            </h2>
            <p hlmAlertDescription>
              A subscriber has been failing for long enough to be parked, and nothing else will say
              so. Check that each endpoint below is reachable.
            </p>
          </div>
        }
      }

      @if (store.error(); as error) {
        <div hlmAlert variant="destructive">
          <h2 hlmAlertTitle>Could not load notifications</h2>
          <p hlmAlertDescription>{{ error.detail }}</p>
          <button hlmBtn hlmAlertAction variant="outline" size="sm" (click)="store.load()">
            Try again
          </button>
        </div>
      } @else if (store.loading()) {
        <div class="space-y-2" aria-busy="true" aria-live="polite">
          <div hlmSkeleton class="h-16 w-full rounded-xl"></div>
        </div>
      } @else {
        @if (store.isEmpty()) {
          <div class="border-border bg-card rounded-xl border p-10 text-center">
            <p class="font-medium">No subscriptions</p>
            <p class="text-muted-foreground mx-auto mt-1 max-w-xl text-sm">
              Nobody is told when something happens here. Subscribe an email address or a webhook to
              hear about breaking changes and enforcement violations.
            </p>
          </div>
        } @else {
          <ul class="space-y-2">
            @for (subscription of store.subscriptions(); track subscription.id) {
              <li class="border-border bg-card rounded-xl border p-4">
                <div class="flex flex-wrap items-center gap-2">
                  <cd-status-badge tone="info">{{ subscription.channel }}</cd-status-badge>
                  <span class="text-foreground font-mono text-sm break-all">
                    {{ subscription.endpoint }}
                  </span>
                  @if (!subscription.enabled) {
                    <cd-status-badge tone="neutral">MUTED</cd-status-badge>
                  }
                </div>

                <p class="text-muted-foreground mt-2 text-xs">
                  @if (all(subscription)) {
                    Every event
                  } @else {
                    {{ subscription.events.join(', ') }}
                  }
                  ·
                  <span [title]="subscription.createdAt.toISOString()">
                    created {{ subscription.createdAt | cdRelativeTime }}
                  </span>
                </p>

                <div *cdIfScope="writeScopes" class="mt-3 flex flex-wrap items-center gap-2">
                  <button
                    type="button"
                    hlmBtn
                    variant="outline"
                    size="sm"
                    [disabled]="store.busy().includes(subscription.id)"
                    (click)="
                      store.setEnabled({ id: subscription.id, enabled: !subscription.enabled })
                    "
                  >
                    {{ subscription.enabled ? 'Mute' : 'Unmute' }}
                  </button>

                  <button
                    type="button"
                    hlmBtn
                    variant="outline"
                    size="sm"
                    [attr.aria-expanded]="editing()?.id === subscription.id"
                    [disabled]="store.busy().includes(subscription.id)"
                    (click)="editEvents(subscription)"
                  >
                    Change events
                  </button>

                  @if (confirming() === subscription.id) {
                    <span class="text-foreground text-sm">
                      Delete this subscription? Its settings are lost; mute it to keep them.
                    </span>
                    <button
                      type="button"
                      hlmBtn
                      variant="destructive"
                      size="sm"
                      [disabled]="store.busy().includes(subscription.id)"
                      (click)="remove(subscription.id)"
                    >
                      Confirm
                    </button>
                    <button
                      type="button"
                      hlmBtn
                      variant="ghost"
                      size="sm"
                      (click)="confirming.set(null)"
                    >
                      Cancel
                    </button>
                  } @else {
                    <button
                      type="button"
                      hlmBtn
                      variant="ghost"
                      size="sm"
                      (click)="confirming.set(subscription.id)"
                    >
                      Delete
                    </button>
                  }
                </div>

                @if (editing(); as edit) {
                  @if (edit.id === subscription.id) {
                    <fieldset
                      class="border-border mt-3 space-y-2 rounded-lg border p-3"
                      [attr.aria-label]="'Events for ' + subscription.endpoint"
                    >
                      <legend class="px-1 text-sm font-medium">Events</legend>
                      <p class="text-muted-foreground text-xs">
                        Keeps this subscription, its address and its signing secret, and changes
                        only what it is told about.
                      </p>
                      <div class="grid gap-x-4 gap-y-1.5 sm:grid-cols-2">
                        @for (event of events; track event) {
                          <label class="flex items-start gap-1.5 text-xs">
                            <input
                              type="checkbox"
                              class="mt-0.5 size-3.5"
                              [checked]="edit.chosen.has(event)"
                              (change)="toggleEdit(event)"
                            />
                            <span>
                              <span class="font-mono">{{ event }}</span>
                              <span class="text-muted-foreground"> — {{ meaning(event) }}</span>
                            </span>
                          </label>
                        }
                      </div>
                      <p class="text-muted-foreground text-xs">
                        {{
                          edit.chosen.size === 0
                            ? 'None ticked means every event, including any added later.'
                            : edit.chosen.size + ' of ' + events.length + ' events.'
                        }}
                      </p>
                      <div class="flex items-center gap-2">
                        <button type="button" hlmBtn size="sm" (click)="saveEvents(subscription)">
                          Save events
                        </button>
                        <button
                          type="button"
                          hlmBtn
                          variant="ghost"
                          size="sm"
                          (click)="editing.set(null)"
                        >
                          Cancel
                        </button>
                      </div>
                    </fieldset>
                  }
                }

                @if (store.rowErrors()[subscription.id]; as error) {
                  <div hlmAlert variant="destructive" class="mt-3">
                    <h3 hlmAlertTitle>Could not change the subscription</h3>
                    <p hlmAlertDescription>{{ error.detail }}</p>
                  </div>
                }
              </li>
            }
          </ul>
        }

        <form
          *cdIfScope="writeScopes"
          [formGroup]="form"
          (ngSubmit)="subscribe()"
          class="border-border bg-card space-y-3 rounded-xl border p-5"
          aria-labelledby="subscribe-heading"
        >
          <h2 id="subscribe-heading" class="text-foreground text-sm font-semibold">Subscribe</h2>

          <div class="grid gap-3 sm:grid-cols-3">
            <div class="space-y-1.5">
              <label class="text-sm font-medium" for="sub-channel">Channel</label>
              <select id="sub-channel" formControlName="channel" [class]="input">
                @for (channel of channels; track channel) {
                  <option [value]="channel">{{ channel }}</option>
                }
              </select>
            </div>
            <div class="space-y-1.5 sm:col-span-2">
              <label class="text-sm font-medium" for="sub-endpoint">
                {{ form.controls.channel.value === 'EMAIL' ? 'Email address' : 'Webhook URL' }}
              </label>
              <input
                id="sub-endpoint"
                formControlName="endpoint"
                autocomplete="off"
                [attr.placeholder]="
                  form.controls.channel.value === 'EMAIL'
                    ? 'platform@example.com'
                    : 'https://hooks.example.com/concordat'
                "
                [class]="input + ' font-mono'"
              />
              @if (form.controls.channel.value === 'WEBHOOK') {
                <p class="text-muted-foreground text-xs">
                  <code>https</code> only: a webhook body names subjects, versions and reviewers.
                </p>
              }
            </div>
          </div>

          <fieldset class="space-y-1.5">
            <legend class="text-sm font-medium">Events</legend>
            <div class="grid gap-x-4 gap-y-1.5 sm:grid-cols-2">
              @for (event of events; track event) {
                <label class="flex items-start gap-1.5 text-xs">
                  <input
                    type="checkbox"
                    class="mt-0.5 size-3.5"
                    [checked]="chosen().has(event)"
                    (change)="toggle(event)"
                  />
                  <span>
                    <span class="font-mono">{{ event }}</span>
                    <span class="text-muted-foreground"> — {{ meaning(event) }}</span>
                  </span>
                </label>
              }
            </div>
            <p class="text-muted-foreground text-xs">
              {{
                chosen().size === 0
                  ? 'None ticked means every event, including any added later.'
                  : chosen().size + ' of ' + events.length + ' events.'
              }}
            </p>
          </fieldset>

          @if (store.createError(); as error) {
            <div hlmAlert variant="destructive">
              <h3 hlmAlertTitle>Could not subscribe</h3>
              <p hlmAlertDescription>{{ error.detail }}</p>
            </div>
          }

          <button type="submit" hlmBtn size="sm" [disabled]="form.invalid || store.creating()">
            Subscribe
          </button>
        </form>
      }
    </section>
  `,
})
export class NotificationsPage {
  protected readonly store = inject(NotificationsStore);
  protected readonly environments = inject(ActiveEnvironmentStore);

  protected readonly input = INPUT;
  protected readonly writeScopes = ENV_WRITE_SCOPES;
  protected readonly channels = NOTIFICATION_CHANNELS;
  protected readonly events = NOTIFICATION_EVENTS;
  protected readonly all = isAllEvents;

  protected readonly chosen = signal<ReadonlySet<NotificationEvent>>(new Set());
  protected readonly confirming = signal<string | null>(null);

  /** The subscription whose events are being edited, and the events ticked so far. At most one. */
  protected readonly editing = signal<{
    readonly id: string;
    readonly chosen: ReadonlySet<NotificationEvent>;
  } | null>(null);

  protected readonly form = inject(FormBuilder).nonNullable.group({
    channel: ['EMAIL' as NotificationChannel],
    endpoint: ['', [Validators.required, Validators.pattern(/\S/)]],
  });

  constructor() {
    effect(() => {
      this.environments.name();
      this.store.load();
    });

    // A created subscription empties the form, so a second click cannot ask for a duplicate —
    // and for a webhook a second signing secret nobody wanted.
    effect(() => {
      if (this.store.created() !== null) {
        this.form.controls.endpoint.reset();
        this.chosen.set(new Set());
      }
    });
  }

  protected meaning(event: NotificationEvent): string {
    return NOTIFICATION_EVENT_MEANING[event];
  }

  protected toggle(event: NotificationEvent): void {
    const next = new Set(this.chosen());

    if (!next.delete(event)) {
      next.add(event);
    }

    this.chosen.set(next);
  }

  protected subscribe(): void {
    if (this.form.invalid) {
      return;
    }

    const { channel, endpoint } = this.form.getRawValue();

    this.store.create({
      channel,
      endpoint: endpoint.trim(),
      // In the registry's own order, so the same choice always sends the same request.
      events: NOTIFICATION_EVENTS.filter((event) => this.chosen().has(event)),
    });
  }

  /**
   * Opens the events of one subscription, on what it holds now.
   *
   * <b>"All events" opens with nothing ticked</b>, which is what it is: the registry reads an empty
   * list as every event, and ticking all of them would be a different subscription that stops hearing
   * the next event added.
   */
  protected editEvents(subscription: Subscription): void {
    if (this.editing()?.id === subscription.id) {
      this.editing.set(null);
      return;
    }

    this.confirming.set(null);
    this.editing.set({
      id: subscription.id,
      chosen: new Set(NOTIFICATION_EVENTS.filter((event) => subscription.events.includes(event))),
    });
  }

  protected toggleEdit(event: NotificationEvent): void {
    const current = this.editing();

    if (current === null) {
      return;
    }

    const next = new Set(current.chosen);

    if (!next.delete(event)) {
      next.add(event);
    }

    this.editing.set({ id: current.id, chosen: next });
  }

  protected saveEvents(subscription: Subscription): void {
    const current = this.editing();

    if (current?.id !== subscription.id) {
      return;
    }

    this.editing.set(null);
    this.store.setEvents({
      id: subscription.id,
      // In the registry's own order, so the same choice always sends the same request.
      events: NOTIFICATION_EVENTS.filter((event) => current.chosen.has(event)),
    });
  }

  protected remove(id: string): void {
    this.confirming.set(null);
    this.store.remove(id);
  }
}
