import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { map, type Observable } from 'rxjs';
import { CONCORDAT_CONFIG } from '../../../core/config/app-config';
import { apiRoot, environmentRoot } from '../../../core/http/api-url';
import { wireTimestamp, wireToken } from '../../../core/http/wire-token';
import {
  NOTIFICATION_CHANNELS,
  type CreatedSubscription,
  type NotificationChannel,
  type OutboxDepth,
  type Subscription,
} from '../../../domain/notifications/subscription';

/** `SubscriptionResponse`. */
export interface SubscriptionDto {
  readonly id: string;
  readonly channel: string;
  readonly endpoint: string;
  readonly events: readonly string[];
  readonly enabled: boolean;
  readonly createdAt: string;
}

/** `CreatedSubscriptionResponse`. */
export interface CreatedSubscriptionDto {
  readonly subscription: SubscriptionDto;
  readonly signingSecret: string | null;
}

/** A subscription as a person asks for one. */
export interface NewSubscription {
  readonly channel: NotificationChannel;
  readonly endpoint: string;
  /** Empty for every event. */
  readonly events: readonly string[];
}

export function toSubscription(dto: SubscriptionDto): Subscription {
  return {
    id: dto.id,
    channel: wireToken('channel', dto.channel, NOTIFICATION_CHANNELS) as NotificationChannel,
    endpoint: dto.endpoint,
    // As received; see `NOTIFICATION_EVENTS` for why this one is not strict.
    events: dto.events,
    enabled: dto.enabled,
    createdAt: wireTimestamp(dto.createdAt),
  };
}

/**
 * The registry's notification endpoints.
 *
 * Subscriptions are per environment; the outbox depth is not — it is one queue for the whole
 * deployment, which is why its path has no environment in it.
 */
@Injectable({ providedIn: 'root' })
export class NotificationsApi {
  private readonly http = inject(HttpClient);
  private readonly config = inject(CONCORDAT_CONFIG);

  listSubscriptions(environment: string): Observable<readonly Subscription[]> {
    return this.http
      .get<readonly SubscriptionDto[]>(`${this.root(environment)}/notifications`)
      .pipe(map((dtos) => dtos.map(toSubscription)));
  }

  /**
   * Subscribes a channel.
   *
   * <b>An empty event list is sent as empty, not left off</b>, and means every event — the
   * registry's own reading, chosen so a subscription can never be enabled and silently deliver
   * nothing.
   */
  createSubscription(
    environment: string,
    subscription: NewSubscription,
  ): Observable<CreatedSubscription> {
    return this.http
      .post<CreatedSubscriptionDto>(`${this.root(environment)}/notifications`, {
        channel: subscription.channel,
        endpoint: subscription.endpoint,
        events: subscription.events,
      })
      .pipe(
        map((dto) => ({
          subscription: toSubscription(dto.subscription),
          signingSecret: dto.signingSecret,
        })),
      );
  }

  setEnabled(environment: string, id: string, enabled: boolean): Observable<Subscription> {
    return this.http
      .put<SubscriptionDto>(
        `${this.root(environment)}/notifications/${encodeURIComponent(id)}/enabled`,
        { enabled },
      )
      .pipe(map(toSubscription));
  }

  /**
   * Changes which events a subscription wants, keeping its endpoint and its signing secret.
   *
   * <b>An empty list is sent as empty, and the member is always sent</b>: it means every event, and
   * the registry refuses a body without it so that a missing member cannot widen who is told.
   */
  setEvents(environment: string, id: string, events: readonly string[]): Observable<Subscription> {
    return this.http
      .put<SubscriptionDto>(
        `${this.root(environment)}/notifications/${encodeURIComponent(id)}/events`,
        { events },
      )
      .pipe(map(toSubscription));
  }

  deleteSubscription(environment: string, id: string): Observable<void> {
    return this.http
      .delete<void>(`${this.root(environment)}/notifications/${encodeURIComponent(id)}`)
      .pipe(map(() => undefined));
  }

  outboxDepth(): Observable<OutboxDepth> {
    return this.http
      .get<OutboxDepth>(`${apiRoot(this.config)}/notifications/outbox`)
      .pipe(map((depth) => ({ pending: depth.pending, parked: depth.parked })));
  }

  private root(environment: string): string {
    return environmentRoot(this.config, environment);
  }
}
