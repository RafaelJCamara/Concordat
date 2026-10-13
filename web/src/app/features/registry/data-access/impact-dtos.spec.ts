import { describe, expect, it } from 'vitest';
import { toImpactReport, type ConsumerImpactDto, type ImpactDto } from './impact-dtos';

// The mapping is where a wire change shows up first. What is not obvious from the types is
// that the certainty guard is strict, like every other wire token: a fourth certainty means a
// newer server, and rendering it as one of the three would mislabel a verdict.

const consumer: ConsumerImpactDto = {
  service: 'billing',
  selector: '1',
  readerOrdinal: 1,
  breaks: true,
  certainty: 'CHECKED',
  lastSeenAt: '2026-08-13T09:00:00+00:00',
  stale: false,
  reasons: ['total is now required'],
};

const report: ImpactDto = {
  subject: 'orders.created',
  candidateOrdinal: 2,
  candidateSchemaId: 'abc',
  breakingCount: 1,
  consumers: [consumer],
  contracts: ['payments'],
};

describe('toImpactReport', () => {
  it('parses the timestamp, so a list is not sorted or compared as text', () => {
    const mapped = toImpactReport(report);

    expect(mapped.consumers[0]?.lastSeenAt).toBeInstanceOf(Date);
    expect(mapped.consumers[0]?.lastSeenAt.toISOString()).toBe('2026-08-13T09:00:00.000Z');
  });

  it('keeps the reasons verbatim, in the engine’s own words', () => {
    expect(toImpactReport(report).consumers[0]?.reasons).toEqual(['total is now required']);
  });

  it('keeps a null reader ordinal for a consumer that follows latest', () => {
    const mapped = toImpactReport({
      ...report,
      consumers: [
        { ...consumer, selector: 'latest', readerOrdinal: null, certainty: 'FOLLOWS_LATEST' },
      ],
    });

    expect(mapped.consumers[0]?.readerOrdinal).toBeNull();
    expect(mapped.consumers[0]?.certainty).toBe('FOLLOWS_LATEST');
  });

  it.each(['CHECKED', 'FOLLOWS_LATEST', 'UNRESOLVABLE'])('accepts the certainty %s', (token) => {
    expect(() =>
      toImpactReport({ ...report, consumers: [{ ...consumer, certainty: token }] }),
    ).not.toThrow();
  });

  it('refuses a certainty it does not recognise', () => {
    expect(() =>
      toImpactReport({ ...report, consumers: [{ ...consumer, certainty: 'PROBABLY_FINE' }] }),
    ).toThrow(/PROBABLY_FINE/);
  });
});
