// Notifications: who is told when something happens in an environment.

/** How a notification is delivered. */
export const NOTIFICATION_CHANNELS = ['EMAIL', 'WEBHOOK'] as const;

/** A delivery channel, spelled as the API spells it. */
export type NotificationChannel = (typeof NOTIFICATION_CHANNELS)[number];

/**
 * Every event a subscription can ask for.
 *
 * <b>This list drives the form and nothing else.</b> A subscription's own events are kept as the
 * strings the registry sent, so a newer registry's new event renders as itself instead of
 * failing the page — the same call, for the same reason, as the audit log's actions: this is
 * configuration being shown back, and a screen that cannot display a subscription it has not
 * been taught about hides exactly the thing somebody opened it to check.
 *
 * Kept in step with `NotificationTokens` by
 * `WireTokenTests.NotificationEventsMatchWhatTheFrontendPublishes`.
 */
export const NOTIFICATION_EVENTS = [
  'VERSION_REGISTERED',
  'BREAKING_CHANGE_SUBMITTED',
  'BREAKING_CHANGE_APPROVED',
  'BREAKING_CHANGE_REJECTED',
  'VERSION_PROMOTED',
  'SUBJECT_DEPRECATED',
  'ENFORCEMENT_VIOLATION',
] as const;

/** An event a subscription may ask for. */
export type NotificationEvent = (typeof NOTIFICATION_EVENTS)[number];

/** What each event means, for somebody deciding what to be told about. */
export const NOTIFICATION_EVENT_MEANING: Readonly<Record<NotificationEvent, string>> = {
  VERSION_REGISTERED: 'A version became active.',
  BREAKING_CHANGE_SUBMITTED: 'A breaking change is waiting for a decision.',
  BREAKING_CHANGE_APPROVED: 'A held change was approved and is now latest.',
  BREAKING_CHANGE_REJECTED: 'A held change was rejected.',
  VERSION_PROMOTED: 'A version was promoted in from another environment.',
  SUBJECT_DEPRECATED: 'A subject was marked deprecated.',
  ENFORCEMENT_VIOLATION: 'A service reported a message that broke its contract.',
};

/** A channel subscribed to an environment's events. */
export interface Subscription {
  readonly id: string;
  readonly channel: NotificationChannel;
  /** An email address, or an `https` URL. */
  readonly endpoint: string;
  /** Which events, as received. Empty means every event — see {@link isAllEvents}. */
  readonly events: readonly string[];
  /** Whether it is delivering. A muted subscription keeps every setting. */
  readonly enabled: boolean;
  readonly createdAt: Date;
}

/**
 * Whether a subscription listens to everything.
 *
 * <b>An empty list means every event, not none.</b> The alternative is a subscription that is
 * configured, enabled and silently delivers nothing, which looks correct from every screen — so
 * the registry reads empty as "all", and a screen that rendered an empty list as "no events"
 * would describe the opposite of what is configured.
 */
export function isAllEvents(subscription: Subscription): boolean {
  return subscription.events.length === 0;
}

/** A newly created subscription, and — for a webhook — the secret to configure on the receiver. */
export interface CreatedSubscription {
  readonly subscription: Subscription;
  /**
   * The HMAC key every delivery is signed with, returned once and never again. Null for email,
   * which has nothing to sign.
   */
  readonly signingSecret: string | null;
}

/** How much the outbox is holding. */
export interface OutboxDepth {
  /** Staged and not yet delivered. */
  readonly pending: number;
  /** Given up on after repeated failures, and kept as evidence. */
  readonly parked: number;
}
