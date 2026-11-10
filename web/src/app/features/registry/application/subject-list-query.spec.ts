import { describe, expect, it } from 'vitest';
import {
  DEFAULT_PAGE_SIZE,
  MAX_PAGE,
  PAGE_SIZES,
  isCanonical,
  pageAfterResize,
  pageCount,
  parseSubjectListQuery,
  toQueryParams,
  type SubjectListQuery,
} from './subject-list-query';

// The address is where the screen's state lives (ADR-052), and an address can be typed, truncated,
// edited or made by something else. Every value is judged, and the answer is always a query the screen
// can show.

function params(values: Record<string, string>) {
  return { get: (name: string) => values[name] ?? null };
}

describe('parseSubjectListQuery', () => {
  it('reads an address with nothing in it as the first page of everything at the default size', () => {
    expect(parseSubjectListQuery(params({}))).toEqual({
      prefix: '',
      page: 1,
      size: DEFAULT_PAGE_SIZE,
    });
  });

  it('reads the three parameters', () => {
    expect(parseSubjectListQuery(params({ q: 'acme.', page: '3', size: '25' }))).toEqual({
      prefix: 'acme.',
      page: 3,
      size: 25,
    });
  });

  it('trims the prefix as the registry will, and keeps its case', () => {
    expect(parseSubjectListQuery(params({ q: '  Acme.Orders ' })).prefix).toBe('Acme.Orders');
  });

  it('cuts a prefix at the longest name there is', () => {
    expect(parseSubjectListQuery(params({ q: 'a'.repeat(900) })).prefix).toHaveLength(512);
  });

  it.each(['0', '-3', 'abc', '2.5', '1e3', '0x10', '', ' ', '१२', '99999999999999'])(
    'reads page %j as the first page, not an error',
    (page) => {
      expect(parseSubjectListQuery(params({ page })).page).toBe(1);
    },
  );

  it('caps a page so far out that its offset could not be a number the registry parses', () => {
    expect(parseSubjectListQuery(params({ page: '999999999' })).page).toBe(MAX_PAGE);
  });

  it.each(['0', '7', '500', '-50', 'big', '', '50.5'])(
    'reads size %j as the default, since it is not one that is offered',
    (size) => {
      expect(parseSubjectListQuery(params({ size })).size).toBe(DEFAULT_PAGE_SIZE);
    },
  );

  it.each(PAGE_SIZES)('accepts the offered size %i', (size) => {
    expect(parseSubjectListQuery(params({ size: String(size) })).size).toBe(size);
  });
});

describe('toQueryParams', () => {
  it('writes nothing for the defaults, so the plain list has the plain address', () => {
    expect(toQueryParams({ prefix: '', page: 1, size: DEFAULT_PAGE_SIZE })).toEqual({
      q: null,
      page: null,
      size: null,
    });
  });

  it('writes what differs from them', () => {
    expect(toQueryParams({ prefix: 'acme.', page: 4, size: 100 })).toEqual({
      q: 'acme.',
      page: '4',
      size: '100',
    });
  });

  it('round-trips through the parser', () => {
    const queries: SubjectListQuery[] = [
      { prefix: '', page: 1, size: 50 },
      { prefix: 'a.b_c', page: 2, size: 25 },
      { prefix: 'x', page: 120, size: 100 },
    ];

    for (const query of queries) {
      const written = toQueryParams(query);

      expect(parseSubjectListQuery({ get: (name) => written[name] ?? null })).toEqual(query);
    }
  });
});

describe('isCanonical', () => {
  it('is true for the address the screen would write', () => {
    expect(isCanonical(params({}))).toBe(true);
    expect(isCanonical(params({ q: 'acme.', page: '3', size: '25' }))).toBe(true);
  });

  it.each([
    ['a page 1 spelled out', { page: '1' }],
    ['a page that is not a number', { page: 'abc' }],
    ['a page with a zero in front', { page: '03' }],
    ['a size nobody offered', { size: '7' }],
    ['the default size spelled out', { size: '50' }],
    ['a prefix with spaces round it', { q: ' acme ' }],
    ['an empty prefix', { q: '' }],
  ])('is false for %s', (_name, values) => {
    expect(isCanonical(params(values))).toBe(false);
  });

  it('is true for the address that rewriting one gives, so that rewriting settles', () => {
    for (const values of [{ page: '0', size: '9', q: ' x ' }, { page: '1' }, { q: '' }] as Record<
      string,
      string
    >[]) {
      const written = toQueryParams(parseSubjectListQuery(params(values)));
      const rewritten = params(
        Object.fromEntries(Object.entries(written).filter(([, v]) => v !== null)) as Record<
          string,
          string
        >,
      );

      expect(isCanonical(rewritten)).toBe(true);
    }
  });
});

describe('pageCount', () => {
  it('rounds up, since a partly full last page is a page', () => {
    expect(pageCount(101, 50)).toBe(3);
    expect(pageCount(100, 50)).toBe(2);
    expect(pageCount(5000, 50)).toBe(100);
  });

  it('is one for an empty list: there is a page to be on', () => {
    expect(pageCount(0, 50)).toBe(1);
  });
});

describe('pageAfterResize', () => {
  it('lands on the page that holds the first row that was being looked at', () => {
    // Page 3 of 25 starts at row 51; at 100 to a page, row 51 is on page 1. At 50 it is on page 2.
    expect(pageAfterResize(3, 25, 100)).toBe(1);
    expect(pageAfterResize(3, 25, 50)).toBe(2);
  });

  it('goes the other way too', () => {
    // Page 2 of 100 starts at row 101; at 25 to a page that is page 5.
    expect(pageAfterResize(2, 100, 25)).toBe(5);
  });

  it('keeps the first page the first page', () => {
    expect(pageAfterResize(1, 25, 100)).toBe(1);
    expect(pageAfterResize(1, 100, 25)).toBe(1);
  });
});
