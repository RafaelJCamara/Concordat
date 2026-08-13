import { describe, expect, it } from 'vitest';
import {
  CONTENT_MODELS,
  SCHEMA_FORMATS,
  SUBJECT_LIFECYCLES,
  VERSION_STATUSES,
} from './wire-tokens';

// These literals are the wire, not a UI vocabulary (ADR-019). Nothing in this app checks
// them against the server at build time — `npm run codes:check` covers `concordatCode` and
// nothing else — so the whole guarantee that a read parses rests on these strings being
// exactly what the API emits.
//
// The failure this file prevents is a tidy-up: uppercasing `json`, hyphenating
// `AWAITING_APPROVAL`, or dropping `RETIRED` because no screen renders it yet. Each would
// compile, and each would make `toSubject` throw on a perfectly ordinary response.

describe('SCHEMA_FORMATS', () => {
  it('is lower case, unlike every other token in this file', () => {
    // Deliberately inconsistent with the rest, because the API is. `format` is the one
    // token the registry spells in lower case, and "fixing" it here breaks every read.
    expect(SCHEMA_FORMATS).toEqual(['json', 'avro', 'protobuf']);
  });
});

describe('VERSION_STATUSES', () => {
  it('names exactly the three states of the approval gate', () => {
    // ADR-017. A fourth status added server-side must reach this list before the UI can
    // render it — `toSubject` refuses an unknown one rather than guessing, which is why a
    // stale list fails loudly instead of mislabelling the gate.
    expect(VERSION_STATUSES).toEqual(['ACTIVE', 'AWAITING_APPROVAL', 'REJECTED']);
  });
});

describe('SUBJECT_LIFECYCLES', () => {
  it('keeps RETIRED, which is the soft delete and is terminal', () => {
    expect(SUBJECT_LIFECYCLES).toEqual(['ACTIVE', 'DEPRECATED', 'RETIRED']);
  });
});

describe('CONTENT_MODELS', () => {
  it('names both models', () => {
    expect(CONTENT_MODELS).toEqual(['OPEN', 'CLOSED']);
  });
});
