import { describe, expect, it } from 'vitest';
import {
  isAllEvents,
  NOTIFICATION_EVENT_MEANING,
  NOTIFICATION_EVENTS,
  type Subscription,
} from './subscription';

// The vocabulary the subscription form offers. The registry's side is pinned by
// `WireTokenTests.NotificationEventsMatchWhatTheFrontendPublishes`; this is the web's own.

function subscription(events: readonly string[]): Subscription {
  return {
    id: 's1',
    channel: 'WEBHOOK',
    endpoint: 'https://hooks.example.com/x',
    events,
    enabled: true,
    createdAt: new Date('2026-10-14T09:00:00Z'),
  };
}

describe('NOTIFICATION_EVENTS', () => {
  it('is the registry’s eight events, once each', () => {
    // A policy change is the eighth (ADR-034). A count, because a vocabulary that drifts from the
    // registry's is a box that cannot be ticked, which is quiet.
    expect([...NOTIFICATION_EVENTS].sort()).toEqual([
      'BREAKING_CHANGE_APPROVED',
      'BREAKING_CHANGE_REJECTED',
      'BREAKING_CHANGE_SUBMITTED',
      'ENFORCEMENT_VIOLATION',
      'SUBJECT_DEPRECATED',
      'SUBJECT_POLICY_CHANGED',
      'VERSION_PROMOTED',
      'VERSION_REGISTERED',
    ]);
  });

  it('says what each one means, so nobody ticks a box they cannot read', () => {
    for (const event of NOTIFICATION_EVENTS) {
      expect(NOTIFICATION_EVENT_MEANING[event].length).toBeGreaterThan(0);
    }
  });

  it('names the policy change by the same token the audit log does', () => {
    // One name for one thing: "who changed it" is the audit filter with the event's own name.
    expect(NOTIFICATION_EVENTS).toContain('SUBJECT_POLICY_CHANGED');
  });
});

describe('isAllEvents', () => {
  it('reads an empty list as every event, which now includes a policy change', () => {
    expect(isAllEvents(subscription([]))).toBe(true);
  });

  it('reads a list that names events as those events only', () => {
    expect(isAllEvents(subscription(['VERSION_REGISTERED']))).toBe(false);
  });
});
