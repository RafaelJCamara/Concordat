import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { beforeEach, describe, expect, it } from 'vitest';
import type { SchemaVersion, Subject } from '../../../domain/registry/subject';
import { SubjectTable } from './subject-table';

// Presentational, so the tests are about what a reader can see and nothing else. The three
// cells worth checking are the ones that encode a decision rather than a field: `latest`
// follows the approval gate, an absent version is an em dash, and the pending count is a
// count of held versions rather than of versions. And the two that make the list's order
// checkable (ADR-049) and the held-version pill a way in (ADR-052).

function version(overrides: Partial<SchemaVersion> & { ordinal: number }): SchemaVersion {
  return {
    schemaId: '0123456789abcdef0123456789abcdef',
    semanticVersion: null,
    status: 'ACTIVE',
    changelog: null,
    registeredAt: new Date('2026-08-13T09:00:00.000Z'),
    registeredBy: 'ci',
    deprecated: false,
    ...overrides,
  };
}

function subject(overrides: Partial<Subject> = {}): Subject {
  return {
    name: 'orders.created',
    format: 'json',
    owner: 'orders-team',
    reviewers: [],
    lifecycle: 'ACTIVE',
    contentModel: 'OPEN',
    compatibilityPolicy: { mode: 'BACKWARD', surface: 'WIRE_JSON' },
    latest: 1,
    versions: [version({ ordinal: 1, semanticVersion: '1.0.0' })],
    ...overrides,
  };
}

describe('SubjectTable', () => {
  let fixture: ComponentFixture<SubjectTable>;

  function render(subjects: readonly Subject[]): HTMLElement {
    fixture.componentRef.setInput('subjects', subjects);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  beforeEach(() => {
    // The router is here for `routerLink` on the subject name, which is the one piece of
    // routing this presentational component owns — see the note on the component for why a
    // table cell cannot take its anchor from the screen above it.
    TestBed.configureTestingModule({ imports: [SubjectTable], providers: [provideRouter([])] });
    fixture = TestBed.createComponent(SubjectTable);
  });

  it('renders one row per subject', () => {
    const host = render([subject({ name: 'orders.created' }), subject({ name: 'orders.shipped' })]);

    expect(host.querySelectorAll('tbody tr')).toHaveLength(2);
  });

  it('renders the rows in the order it was given: the registry orders them and the table does not', () => {
    const host = render([
      subject({ name: 'z.newest' }),
      subject({ name: 'a.oldest' }),
      subject({ name: 'm.middle' }),
    ]);

    expect(
      [...host.querySelectorAll('tbody tr td:first-child')].map((c) => c.textContent?.trim()),
    ).toEqual(['z.newest', 'a.oldest', 'm.middle']);
  });

  it('renders nothing but the header when there are no subjects', () => {
    // The empty state is the page's business, not the table's. A table that drew its own
    // "no subjects" row would compete with it.
    const host = render([]);

    expect(host.querySelectorAll('tbody tr')).toHaveLength(0);
    expect(host.querySelectorAll('thead tr')).toHaveLength(1);
  });

  it('shows the version the latest pointer resolves to, not the newest one', () => {
    // ADR-017 as the user sees it. Ordinal 2 exists and is held at the gate; showing it as
    // `latest` would tell every reader that an unapproved contract is live.
    const host = render([
      subject({
        latest: 1,
        versions: [
          version({ ordinal: 1, semanticVersion: '1.0.0' }),
          version({ ordinal: 2, semanticVersion: '2.0.0', status: 'AWAITING_APPROVAL' }),
        ],
      }),
    ]);

    expect(host.textContent).toContain('v1');
    expect(host.textContent).not.toContain('v2');
  });

  it('shows an em dash when no version is active yet', () => {
    // Not "0" and not "none". A subject with no active version is the ordinary state right
    // after creation, and a zero reads like a count that went wrong.
    const host = render([subject({ latest: null, versions: [] })]);

    expect(host.textContent).toContain('—');
  });

  it('counts the versions waiting for a decision', () => {
    const host = render([
      subject({
        latest: 1,
        versions: [
          version({ ordinal: 1 }),
          version({ ordinal: 2, status: 'AWAITING_APPROVAL' }),
          version({ ordinal: 3, status: 'REJECTED' }),
          version({ ordinal: 4, status: 'AWAITING_APPROVAL' }),
        ],
      }),
    ]);

    expect(host.textContent).toContain('2 awaiting approval');
  });

  it('says nothing about approvals when nothing is waiting', () => {
    expect(render([subject()]).textContent).not.toContain('awaiting approval');
  });

  it('badges a lifecycle only when it is not the ordinary one', () => {
    // Every subject being labelled `ACTIVE` is noise that makes `DEPRECATED` harder to spot,
    // which is the one the badge exists for.
    expect(render([subject({ lifecycle: 'ACTIVE' })]).textContent).not.toContain('ACTIVE');
    expect(render([subject({ lifecycle: 'DEPRECATED' })]).textContent).toContain('DEPRECATED');
  });

  it('puts the exact creation time in a title beside the relative one', () => {
    // "2 months ago" is the right thing to scan a list with and the wrong thing to
    // reconcile an incident against. The next question after "recently?" is always "when
    // exactly?", and it has to be answerable without leaving the row.
    const host = render([subject({ createdAt: new Date('2026-11-02T08:30:15.000Z') })]);
    const time = host.querySelector('td[data-testid="created"] time');

    expect(time?.getAttribute('title')).toBe('2026-11-02T08:30:15.000Z');
    expect(time?.getAttribute('datetime')).toBe('2026-11-02T08:30:15.000Z');
  });

  describe('the order, made visible (ADR-049)', () => {
    it('has a Created column, and no column that claims to be when the subject was registered', () => {
      const headers = [...render([subject()]).querySelectorAll('thead th')].map((th) =>
        th.textContent?.trim(),
      );

      expect(headers).toEqual(['Subject', 'Format', 'Owner', 'Latest', 'Created', 'State']);
    });

    it('says the list is sorted by it, to a screen reader as well', () => {
      const created = [...render([subject()]).querySelectorAll('thead th')].find(
        (th) => th.textContent?.trim() === 'Created',
      );

      expect(created?.getAttribute('aria-sort')).toBe('descending');
    });

    it('shows the day and the minute, so that the order can be checked and not only asserted', () => {
      const host = render([
        subject({ name: 'b', createdAt: new Date('2026-11-09T14:03:59.000Z') }),
        subject({ name: 'a', createdAt: new Date('2026-11-09T09:15:00.000Z') }),
      ]);
      const cells = [...host.querySelectorAll('td[data-testid="created"]')].map((c) =>
        c.textContent?.replace(/\s+/g, ' ').trim(),
      );

      expect(cells[0]).toContain('2026-11-09 14:03 UTC');
      expect(cells[1]).toContain('2026-11-09 09:15 UTC');
    });

    it('shows the subject’s creation date and not the date its latest version was registered', () => {
      // The defect this column replaces: a subject made a year ago whose version was registered yesterday
      // looked as though it had been made yesterday.
      const host = render([
        subject({
          createdAt: new Date('2025-01-10T00:00:00.000Z'),
          versions: [version({ ordinal: 1, registeredAt: new Date('2026-08-13T09:00:00.000Z') })],
        }),
      ]);
      const created = host.querySelector('td[data-testid="created"]');

      expect(created?.textContent).toContain('2025-01-10 00:00 UTC');
      expect(created?.textContent).not.toContain('2026-08-13');
    });

    it('keeps when the latest version was registered, beside the version it belongs to, and labelled as that', () => {
      const host = render([subject()]);
      const registered = host.querySelector('[data-testid="registered"]');

      expect(registered?.textContent?.trim()).toMatch(/^registered /);
      expect(registered?.getAttribute('title')).toBe('Registered 2026-08-13T09:00:00.000Z');
    });

    it('says it does not know, with a dash, when the registry sent no creation date', () => {
      const host = render([subject()]);
      const cell = host.querySelector('td[data-testid="created"]');

      expect(cell?.textContent?.trim()).toBe('—');
      expect(cell?.querySelector('time')).toBeNull();
    });

    it('captions the table with the order', () => {
      expect(render([subject()]).querySelector('caption')?.textContent).toContain('Newest first');
    });
  });

  it('captions the table so the latest column is not a guess', () => {
    expect(render([subject()]).querySelector('caption')?.textContent).toContain('latest');
  });

  // The pill that says something is waiting is a way to it (ADR-052).
  describe('the awaiting-approval pill', () => {
    const held = (...ordinals: number[]) => [
      version({ ordinal: 1 }),
      ...ordinals.map((ordinal) => version({ ordinal, status: 'AWAITING_APPROVAL' })),
    ];
    const link = (host: HTMLElement) =>
      host.querySelector<HTMLAnchorElement>('[data-testid="review-link"]');

    it('is a real link, in the tab order, and not a clickable span', () => {
      const a = link(render([subject({ versions: held(2) })]));

      expect(a?.tagName).toBe('A');
      expect(a?.getAttribute('href')).not.toBeNull();
      expect(a?.getAttribute('tabindex')).toBeNull();
    });

    it('goes to the one held version, on the approvals screen', () => {
      const a = link(render([subject({ name: 'orders.created', versions: held(2) })]));

      expect(a?.getAttribute('href')).toBe('/approvals?subject=orders.created&version=2');
    });

    it('goes to the approvals screen filtered to the subject when several are held, and names no version', () => {
      const a = link(render([subject({ name: 'orders.created', versions: held(2, 3) })]));

      expect(a?.getAttribute('href')).toBe('/approvals?subject=orders.created');
    });

    it('puts a name with characters that mean something in an address into it as itself', () => {
      const a = link(render([subject({ name: 'a.b_c-d e&f', versions: held(2) })]));
      const url = new URL(a!.getAttribute('href')!, 'http://localhost');

      expect(url.pathname).toBe('/approvals');
      expect(url.searchParams.get('subject')).toBe('a.b_c-d e&f');
    });

    it('is named for what it does, starting with the words it shows', () => {
      const name = (versions: SchemaVersion[]) =>
        link(render([subject({ name: 'orders.created', versions })]))?.getAttribute('aria-label');

      expect(name(held(2))).toBe('1 awaiting approval: review the held version of orders.created');
      expect(name(held(2, 3))).toBe(
        '2 awaiting approval: review the held versions of orders.created',
      );
    });

    it('shows the words the name begins with, so the label is in the name', () => {
      const a = link(render([subject({ versions: held(2, 3) })]));

      expect(a?.textContent?.replace(/\s+/g, ' ').trim()).toBe('2 awaiting approval');
      expect(a?.getAttribute('aria-label')?.startsWith('2 awaiting approval')).toBe(true);
    });

    it('shows that it can be clicked: a ring on hover and on focus, an arrow, underlined words', () => {
      const a = link(render([subject({ versions: held(2) })]));

      expect(a?.className).toContain('hover:ring-2');
      expect(a?.className).toContain('focus-visible:ring-2');
      expect(a?.querySelector('cd-icon')).not.toBeNull();
      expect(a?.innerHTML).toContain('group-hover:underline');
    });

    it('leaves the lifecycle badge inert: a state with no destination is not a link', () => {
      const host = render([subject({ lifecycle: 'DEPRECATED' })]);

      expect(host.querySelector('a[data-testid="review-link"]')).toBeNull();
      const cell = host.querySelectorAll('tbody td')[5];

      expect(cell?.textContent).toContain('DEPRECATED');
      expect(cell?.querySelector('a')).toBeNull();
      expect(cell?.querySelector('button')).toBeNull();
    });

    it('is beside the lifecycle badge, not instead of it, and only the pill is a link', () => {
      const host = render([subject({ lifecycle: 'DEPRECATED', versions: held(2) })]);
      const cell = host.querySelectorAll('tbody td')[5];

      expect(cell?.querySelectorAll('a')).toHaveLength(1);
      expect(cell?.textContent).toContain('DEPRECATED');
      expect(cell?.querySelector('a')?.textContent).not.toContain('DEPRECATED');
    });

    it('is not there when nothing is held', () => {
      expect(link(render([subject()]))).toBeNull();
    });
  });
});
