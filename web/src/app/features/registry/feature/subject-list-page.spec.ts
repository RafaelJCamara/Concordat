import { signal } from '@angular/core';
import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { beforeEach, describe, expect, it } from 'vitest';
import { SessionStore } from '../../../core/auth/session-store';
import { provideConcordatConfig } from '../../../core/config/app-config';
import type { Scope } from '../../../domain/identity/scope';
import type { Subject } from '../../../domain/registry/subject';
import { SubjectListStore } from '../application/subject-list-store';
import { SubjectListPage } from './subject-list-page';

// The screen's own logic is the search box, and that is what this file is about. Loading,
// failing and retrying belong to `SubjectListStore` and are tested there; asserting them again
// through the page would be the same test with a slower setup.
//
// The filter matters more than it looks. It is the only thing standing between a reader and a
// list of several hundred dotted names, and it is client-side over an unpaginated list — a
// decision with an expiry date that is written down in the component and pinned here.

function subject(overrides: Partial<Subject> & { name: string }): Subject {
  return {
    format: 'json',
    owner: 'orders-team',
    lifecycle: 'ACTIVE',
    contentModel: 'OPEN',
    compatibilityPolicy: { mode: 'BACKWARD', surface: 'WIRE_JSON' },
    latest: null,
    versions: [],
    ...overrides,
  };
}

const SUBJECTS: readonly Subject[] = [
  subject({ name: 'acme.orders.OrderCreated', owner: 'orders-team' }),
  subject({ name: 'acme.orders.OrderShipped', owner: 'orders-team' }),
  subject({ name: 'acme.billing.InvoiceRaised', owner: 'billing-team' }),
];

/**
 * A loaded `SubjectListStore`, stubbed.
 *
 * The store is replaced rather than the API underneath it, because the boundaries rule says a
 * routed component may not reach into `data-access` — and a test that imports `SubjectsApi` to
 * build a fake has just done, from the same folder, the thing the rule exists to prevent. The
 * store is this page's only collaborator, so it is also the only thing worth substituting.
 */
function loadedStore(subjects: readonly Subject[]) {
  return {
    subjects: signal(subjects),
    loading: signal(false),
    error: signal(null),
    isEmpty: signal(subjects.length === 0),
    load: () => {},
  } as unknown as InstanceType<typeof SubjectListStore>;
}

describe('SubjectListPage', () => {
  let fixture: ComponentFixture<SubjectListPage>;

  /** Types into the search box the way a person does, rather than poking the signal. */
  function search(term: string): HTMLElement {
    const box = (fixture.nativeElement as HTMLElement).querySelector('input[type=search]');
    const input = box as HTMLInputElement;

    input.value = term;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    return fixture.nativeElement as HTMLElement;
  }

  function rows(): string[] {
    return [...(fixture.nativeElement as HTMLElement).querySelectorAll('tbody tr')].map(
      (row) => row.textContent ?? '',
    );
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      // The router is for `SubjectTable`'s links to the detail screen, not for this page.
      providers: [provideRouter([]), provideConcordatConfig({ defaultEnvironment: 'dev' })],
    });

    // The page provides its own store, so a root-level provider would never be reached. This
    // replaces the component-level one, which is the only place it is registered.
    TestBed.overrideComponent(SubjectListPage, {
      set: { providers: [{ provide: SubjectListStore, useValue: loadedStore(SUBJECTS) }] },
    });

    fixture = TestBed.createComponent(SubjectListPage);
    fixture.detectChanges();
  });

  it('lists everything before anything is typed', () => {
    expect(rows()).toHaveLength(3);
  });

  it('matches on any part of the name, not just the start', () => {
    // Subject names are dotted paths. Somebody looking for `acme.orders.OrderCreated` types
    // "ordercreated" far more often than they type the namespace it lives under, and a prefix
    // match would answer that with nothing.
    search('ordercreated');

    expect(rows()).toHaveLength(1);
    expect(rows()[0]).toContain('acme.orders.OrderCreated');
  });

  it('ignores case', () => {
    search('INVOICE');

    expect(rows()).toHaveLength(1);
    expect(rows()[0]).toContain('acme.billing.InvoiceRaised');
  });

  it('matches on the owner too', () => {
    // "What does the billing team own" is a question people actually ask, usually while
    // deciding who to page.
    search('billing-team');

    expect(rows()).toHaveLength(1);
    expect(rows()[0]).toContain('acme.billing.InvoiceRaised');
  });

  it('treats whitespace as an empty query rather than as no matches', () => {
    // A trailing space survives a copy-paste and a fat-fingered spacebar. Filtering on it
    // would empty the list and look like the registry lost everything.
    expect(search('   ')).toBeDefined();
    expect(rows()).toHaveLength(3);
  });

  it('says which term found nothing', () => {
    const host = search('nonesuch');

    expect(rows()).toHaveLength(0);
    // Naming the term is the difference between "there is nothing here" and "there is nothing
    // matching what you typed" — one of which sends someone to check the registry is up.
    expect(host.textContent).toContain('nonesuch');
  });

  it('keeps the search box up while a query matches nothing', () => {
    // The empty result has to be recoverable without a reload. Rendering the "no matches" panel
    // *instead of* the box is a dead end that has shipped in more than one product.
    const host = search('nonesuch');

    expect(host.querySelector('input[type=search]')).not.toBeNull();
  });
});

// "New subject" is the only way into the create screen from here, and it used to be a button with
// no handler: a writer could press it and nothing happened. It is a link now, absent — not
// disabled — for a caller who cannot write (ADR-018), and the empty list carries the same step.
describe('SubjectListPage: creating a subject', () => {
  function renderAs(scopes: readonly Scope[], subjects: readonly Subject[]): HTMLElement {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideConcordatConfig({ defaultEnvironment: 'dev' })],
    });
    TestBed.overrideComponent(SubjectListPage, {
      set: { providers: [{ provide: SubjectListStore, useValue: loadedStore(subjects) }] },
    });
    TestBed.inject(SessionStore).observeInstance({ claimed: true, actor: 'someone', scopes });

    const fixture = TestBed.createComponent(SubjectListPage);
    fixture.detectChanges();

    return fixture.nativeElement as HTMLElement;
  }

  const links = (host: HTMLElement) =>
    [...host.querySelectorAll('a[href="/subjects/new-subject"]')].map((a) => a.textContent?.trim());

  it('links "New subject" to the create screen for someone who can write', () => {
    expect(links(renderAs(['subject:write'], SUBJECTS))).toEqual(['New subject']);
  });

  it('offers it to an admin as well, who holds the other write scope', () => {
    expect(links(renderAs(['subject:admin'], SUBJECTS))).toEqual(['New subject']);
  });

  it('leaves it out, rather than disabling it, for a reader', () => {
    const host = renderAs(['subject:read'], SUBJECTS);

    expect(links(host)).toEqual([]);
    expect(host.textContent).not.toContain('New subject');
  });

  it('is a link and not a button that does nothing', () => {
    const host = renderAs(['subject:write'], SUBJECTS);

    expect(
      [...host.querySelectorAll('button')].some((b) => b.textContent?.includes('New subject')),
    ).toBe(false);
  });

  it('puts the next step beside an empty list, for a writer', () => {
    const host = renderAs(['subject:write'], []);

    expect(host.textContent).toContain('No subjects yet');
    expect(links(host)).toEqual(['New subject', 'Create the first subject']);
  });

  it('says the list is empty and offers nothing a reader cannot do', () => {
    const host = renderAs(['subject:read'], []);

    expect(host.textContent).toContain('No subjects yet');
    expect(links(host)).toEqual([]);
  });
});
