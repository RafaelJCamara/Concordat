import { describe, expect, it } from 'vitest';
import { DEFAULT_SELECTOR, ENFORCEMENT_MODES, parseSubjectRefs } from './contract';

// `parseSubjectRefs` checks shape and nothing else. Whether a subject exists, and whether a
// selector is one the registry accepts, is the registry's to say — a second copy of that
// grammar in the browser is one more place for the two to disagree.

describe('parseSubjectRefs', () => {
  it('reads a bare subject as latest, which is what the registry would assume', () => {
    expect(parseSubjectRefs('orders.created')).toEqual([
      { subject: 'orders.created', selector: DEFAULT_SELECTOR },
    ]);
  });

  it('reads a pinned ordinal', () => {
    expect(parseSubjectRefs('orders.created@2')).toEqual([
      { subject: 'orders.created', selector: '2' },
    ]);
  });

  it('reads a range, keeping its text exactly', () => {
    expect(parseSubjectRefs('orders.created@>=2')).toEqual([
      { subject: 'orders.created', selector: '>=2' },
    ]);
  });

  it('does not interpret the selector, so a grammar the registry grows needs no release here', () => {
    expect(parseSubjectRefs('a@something-new')?.[0]?.selector).toBe('something-new');
  });

  it('splits on commas, spaces and new lines, in any mixture', () => {
    expect(parseSubjectRefs('a, b  c\nd,\n e@1')).toEqual([
      { subject: 'a', selector: 'latest' },
      { subject: 'b', selector: 'latest' },
      { subject: 'c', selector: 'latest' },
      { subject: 'd', selector: 'latest' },
      { subject: 'e', selector: '1' },
    ]);
  });

  it('keeps the order it was written in', () => {
    expect(parseSubjectRefs('z a m')?.map((r) => r.subject)).toEqual(['z', 'a', 'm']);
  });

  it.each(['', '   ', ',', ' , \n '])('refuses %j, which names nothing', (text) => {
    expect(parseSubjectRefs(text)).toBeNull();
  });

  it.each(['orders.created@', '@2', '@', 'a b@'])(
    'refuses %j rather than guessing what was meant',
    (text) => {
      // `orders.created@` would silently become `latest` — a version choice its author never
      // made, on a binding that decides what a fleet may send.
      expect(parseSubjectRefs(text)).toBeNull();
    },
  );

  it('is not fooled by an at-sign inside a later part', () => {
    // Only the first `@` separates; the rest belongs to the selector, for the registry to judge.
    expect(parseSubjectRefs('a@1@2')).toEqual([{ subject: 'a', selector: '1@2' }]);
  });
});

describe('ENFORCEMENT_MODES', () => {
  it('lists the three modes the registry emits, least strict first', () => {
    expect(ENFORCEMENT_MODES).toEqual(['OFF', 'MONITOR', 'ENFORCE']);
  });
});
