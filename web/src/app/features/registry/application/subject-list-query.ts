import { trimRegistryText } from '../../../domain/registry/registry-text';
import { SUBJECT_NAME_MAX_LENGTH } from '../../../domain/registry/subject-name';

/**
 * Which part of the subject list a person is looking at: the prefix, the page and the size of one
 * (ADR-052). This is what the address bar holds, so it is what a reload, a shared link and the Back
 * button restore, and it is a plain value so the URL, the store and the pager can all be tested
 * against the same few functions and not against one another.
 */
export interface SubjectListQuery {
  /** What the names must begin with, trimmed as the registry will trim it; empty is every subject. */
  readonly prefix: string;
  /** 1-based. */
  readonly page: number;
  /** How many subjects to a page: one of {@link PAGE_SIZES}. */
  readonly size: number;
}

/**
 * The sizes offered. Three, because a person choosing one is choosing between "fits a laptop screen
 * without scrolling much" and "I am scanning", and a fourth is a decision about nothing; and none
 * above 100, because each subject carries its whole version history on the wire (ADR-045), so the
 * response grows with the page and the bound is the cost of one request. The registry would accept 500.
 */
export const PAGE_SIZES: readonly number[] = [25, 50, 100];

/** The size a page has when the address says none: what the screen has asked for since ADR-045. */
export const DEFAULT_PAGE_SIZE = 50;

/**
 * The last page number the address is believed. A page that large is an attempt at an offset past
 * anything a database holds, not a place a person got to by clicking, and the offset (`(page - 1) *
 * size`) has to stay an integer the registry can parse.
 */
export const MAX_PAGE = 1_000_000;

/** The names of the three parameters, in the address: short, because they are read aloud and pasted. */
export const QUERY_PARAMS = { prefix: 'q', page: 'page', size: 'size' } as const;

/** Anything with a `get`, which a router's `ParamMap` is: this module does not import the router. */
export interface ParamSource {
  get(name: string): string | null;
}

/** A whole number written as digits, or null: `2.5`, `-1`, `1e3`, `0x10` and `abc` are not pages. */
function wholeNumber(text: string | null): number | null {
  if (text === null || !/^\d{1,9}$/.test(text.trim())) {
    return null;
  }

  return Number.parseInt(text, 10);
}

/**
 * Reads the address, believing as little of it as possible.
 *
 * A link can be typed, truncated, edited or made by something that is not this app, so every value
 * is judged and the answer is always a query the screen can show: no page, a page that is not a
 * positive whole number, or a size that is not one of the offered sizes is the first page and the
 * default size, never an error and never a blank table. The prefix is trimmed as the registry trims
 * it and cut at the longest name there is, since nothing longer can match.
 */
export function parseSubjectListQuery(params: ParamSource): SubjectListQuery {
  const page = wholeNumber(params.get(QUERY_PARAMS.page));
  const size = wholeNumber(params.get(QUERY_PARAMS.size));

  return {
    prefix: trimRegistryText(params.get(QUERY_PARAMS.prefix) ?? '').slice(
      0,
      SUBJECT_NAME_MAX_LENGTH,
    ),
    page: page === null || page < 1 ? 1 : Math.min(page, MAX_PAGE),
    size: size !== null && PAGE_SIZES.includes(size) ? size : DEFAULT_PAGE_SIZE,
  };
}

/**
 * The address for a query: only what differs from the defaults, so the plain list has the plain
 * address (`/subjects`) and a link someone copies says nothing it does not need to. A `null` is how
 * the router is told to drop a parameter it would otherwise keep.
 */
export function toQueryParams(query: SubjectListQuery): Record<string, string | null> {
  return {
    [QUERY_PARAMS.prefix]: query.prefix === '' ? null : query.prefix,
    [QUERY_PARAMS.page]: query.page <= 1 ? null : String(query.page),
    [QUERY_PARAMS.size]: query.size === DEFAULT_PAGE_SIZE ? null : String(query.size),
  };
}

/** Whether the address already says what {@link toQueryParams} would make it say. */
export function isCanonical(params: ParamSource): boolean {
  const wanted = toQueryParams(parseSubjectListQuery(params));

  return Object.entries(wanted).every(([name, value]) => params.get(name) === value);
}

/** How many pages `total` subjects make at `size` to a page. At least one: an empty list is a page. */
export function pageCount(total: number, size: number): number {
  return Math.max(Math.ceil(total / size), 1);
}

/**
 * The page that begins at, or contains, the same first subject when the size changes. Changing the
 * size from the third page of 25 to 100 lands on the first page of 100, which holds what the person
 * was looking at, and not on a page 3 of 100 that is a different part of the list.
 */
export function pageAfterResize(page: number, oldSize: number, newSize: number): number {
  return Math.floor(((page - 1) * oldSize) / newSize) + 1;
}
