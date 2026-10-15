import { signal } from '@angular/core';
import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { describe, expect, it, vi } from 'vitest';
import { SessionStore } from '../../../core/auth/session-store';
import { provideConcordatConfig } from '../../../core/config/app-config';
import { ConcordatError } from '../../../core/http/problem-details';
import {
  COMPATIBILITY_MODE_MEANING,
  COMPATIBILITY_MODES,
  COMPATIBILITY_SURFACE_MEANING,
  COMPATIBILITY_SURFACES,
} from '../../../domain/registry/compatibility';
import type { NewSubject } from '../../../domain/registry/new-subject';
import type { Subject } from '../../../domain/registry/subject';
import { NewSubjectStore } from '../application/new-subject-store';
import { SubjectListStore } from '../application/subject-list-store';
import { NewSubjectPage } from './new-subject-page';

// What matters here is what the form will and will not send, and that it never claims a subject
// exists before the registry has said so. A block must be a refusal the registry certainly makes
// (ADR-028), the name follows the title only until it is edited by hand, and creating takes two
// clicks because the name cannot be changed afterwards: the second must only be able to send
// what the first named.

interface Scenario {
  existing?: readonly string[];
  actor?: string | null;
  submitting?: boolean;
  error?: { error: ConcordatError; attempted: string } | null;
}

function subjectNamed(name: string): Subject {
  return {
    name,
    format: 'json',
    owner: 'someone',
    reviewers: [],
    lifecycle: 'ACTIVE',
    contentModel: 'OPEN',
    compatibilityPolicy: { mode: null, surface: null },
    latest: null,
    versions: [],
  };
}

function render(scenario: Scenario = {}) {
  TestBed.configureTestingModule({
    providers: [provideRouter([]), provideConcordatConfig({ defaultEnvironment: 'dev' })],
  });

  const created = signal<Subject | null>(null);
  const error = signal(scenario.error ?? null);
  const store = {
    submitting: signal(scenario.submitting ?? false),
    created,
    error,
    create: vi.fn(),
    reset: vi.fn(() => error.set(null)),
  };
  const list = {
    subjects: signal((scenario.existing ?? []).map(subjectNamed)),
    load: vi.fn(),
  };

  // Before anything that calls TestBed.inject, or the override is ignored.
  TestBed.overrideComponent(NewSubjectPage, {
    set: {
      providers: [
        { provide: NewSubjectStore, useValue: store },
        { provide: SubjectListStore, useValue: list },
      ],
    },
  });

  TestBed.inject(SessionStore).observeInstance({
    claimed: true,
    actor: scenario.actor === undefined ? 'alice@example.com' : scenario.actor,
    scopes: ['subject:write'],
  });

  const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
  const fixture = TestBed.createComponent(NewSubjectPage);
  fixture.detectChanges();

  return { fixture, host: fixture.nativeElement as HTMLElement, store, list, navigate };
}

type Rendered = ReturnType<typeof render>;

/** Types into a control and leaves it, as a person does. */
function type(fixture: ComponentFixture<NewSubjectPage>, id: string, value: string): void {
  const element = (fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>(`#${id}`)!;

  element.value = value;
  element.dispatchEvent(new Event('input'));
  element.dispatchEvent(new Event('blur'));
  fixture.detectChanges();
}

function choose(fixture: ComponentFixture<NewSubjectPage>, id: string, value: string): void {
  const element = (fixture.nativeElement as HTMLElement).querySelector<HTMLSelectElement>(
    `#${id}`,
  )!;

  element.value = value;
  element.dispatchEvent(new Event('change'));
  fixture.detectChanges();
}

function pick(fixture: ComponentFixture<NewSubjectPage>, label: string): void {
  const radio = [...(fixture.nativeElement as HTMLElement).querySelectorAll('label')]
    .find((l) => l.textContent?.includes(label))
    ?.querySelector('input');

  radio?.click();
  fixture.detectChanges();
}

function click(fixture: ComponentFixture<NewSubjectPage>, text: string): void {
  const button = [...(fixture.nativeElement as HTMLElement).querySelectorAll('button')].find((b) =>
    b.textContent?.includes(text),
  );

  if (button === undefined) {
    throw new Error(`no button containing '${text}'`);
  }

  button.click();
  fixture.detectChanges();
}

const value = (host: HTMLElement, id: string) =>
  host.querySelector<HTMLInputElement>(`#${id}`)!.value;
const hasButton = (host: HTMLElement, text: string) =>
  [...host.querySelectorAll('button')].some((b) => b.textContent?.includes(text));
const submit = (host: HTMLElement) =>
  host.querySelector<HTMLButtonElement>('button[type="submit"]')!;

/** Fills in the one thing a form needs that has no default: a valid name. */
function validForm(r: Rendered, name = 'acme.orders.OrderCreated'): void {
  type(r.fixture, 'subject-name', name);
}

describe('NewSubjectPage', () => {
  describe('the name follows the title until it is edited', () => {
    it('suggests a name as the title is typed', () => {
      const r = render();
      type(r.fixture, 'subject-title', 'Order created');

      expect(value(r.host, 'subject-name')).toBe('Order_created');
    });

    it('keeps following while the title changes', () => {
      const r = render();
      type(r.fixture, 'subject-title', 'Order');
      type(r.fixture, 'subject-title', 'Order created');
      type(r.fixture, 'subject-title', 'Order created v2');

      expect(value(r.host, 'subject-name')).toBe('Order_created_v2');
    });

    it('stops the moment the name is edited by hand', () => {
      const r = render();
      type(r.fixture, 'subject-title', 'Order created');
      type(r.fixture, 'subject-name', 'acme.orders.OrderCreated');
      type(r.fixture, 'subject-title', 'Something else entirely');

      expect(value(r.host, 'subject-name')).toBe('acme.orders.OrderCreated');
    });

    it('treats a name typed before any title as the author’s from the first keystroke', () => {
      const r = render();
      type(r.fixture, 'subject-name', 'acme.orders.OrderCreated');
      type(r.fixture, 'subject-title', 'Order created');

      expect(value(r.host, 'subject-name')).toBe('acme.orders.OrderCreated');
    });

    it('does not count its own updates as editing', () => {
      // Following is a programmatic change, which fires no `input` event. If it counted, the
      // name would freeze after the first character of the title.
      const r = render();
      type(r.fixture, 'subject-title', 'O');
      type(r.fixture, 'subject-title', 'Or');

      expect(value(r.host, 'subject-name')).toBe('Or');
      expect(r.host.textContent).toContain('Following the title');
    });

    it('offers to go back to the title once it has been left, and not before', () => {
      const r = render();
      type(r.fixture, 'subject-title', 'Order created');

      expect(hasButton(r.host, 'from the title instead')).toBe(false);

      type(r.fixture, 'subject-name', 'acme.orders.OrderCreated');

      expect(hasButton(r.host, 'from the title instead')).toBe(true);
      expect(r.host.textContent).toContain('Order_created');
    });

    it('goes back to following when asked, and applies the suggestion', () => {
      const r = render();
      type(r.fixture, 'subject-title', 'Order created');
      type(r.fixture, 'subject-name', 'acme.orders.OrderCreated');
      click(r.fixture, 'from the title instead');

      expect(value(r.host, 'subject-name')).toBe('Order_created');
      expect(hasButton(r.host, 'from the title instead')).toBe(false);

      type(r.fixture, 'subject-title', 'Order shipped');

      expect(value(r.host, 'subject-name')).toBe('Order_shipped');
    });

    it('does not offer anything when there is no title to follow', () => {
      const r = render();
      type(r.fixture, 'subject-name', 'acme.orders.OrderCreated');

      expect(hasButton(r.host, 'from the title instead')).toBe(false);
    });
  });

  describe('the name', () => {
    it('is not complained about before anyone has touched it', () => {
      const r = render();

      expect(r.host.textContent).not.toContain('A subject name is required');
      expect(submit(r.host).disabled).toBe(true);
    });

    it('names the character the registry would refuse, and blocks', () => {
      const r = render();
      type(r.fixture, 'subject-name', 'order-created');

      expect(r.host.textContent).toContain("'-' (U+002D) is not allowed");
      expect(submit(r.host).disabled).toBe(true);
      expect(r.host.querySelector('#subject-name')?.getAttribute('aria-invalid')).toBe('true');
    });

    it('accepts a name with padding, which the registry trims', () => {
      const r = render();
      type(r.fixture, 'subject-name', '  acme.orders  ');

      expect(submit(r.host).disabled).toBe(false);
      expect(r.host.textContent).not.toContain('not allowed');
    });
  });

  describe('the owner', () => {
    it('defaults to the signed-in caller', () => {
      const r = render({ actor: 'alice@example.com' });

      expect(value(r.host, 'subject-owner')).toBe('alice@example.com');
    });

    it('is empty on an instance with nobody signed in, and then required', () => {
      const r = render({ actor: null });
      validForm(r);

      expect(value(r.host, 'subject-owner')).toBe('');
      expect(submit(r.host).disabled).toBe(true);

      type(r.fixture, 'subject-owner', '   ');

      expect(r.host.textContent).toContain('An owner is required');
    });

    it('may be anything the registry accepts, such as a team', () => {
      const r = render();
      validForm(r);
      type(r.fixture, 'subject-owner', 'team-payments');

      expect(submit(r.host).disabled).toBe(false);
    });
  });

  describe('the compatibility policy', () => {
    it('inherits by default, and says what that means', () => {
      const r = render();
      validForm(r);

      expect(submit(r.host).disabled).toBe(false);
      expect(r.host.textContent).toContain('Both or neither');
    });

    it('blocks when only one half is chosen, which the registry answers 400', () => {
      const r = render();
      validForm(r);
      choose(r.fixture, 'subject-mode', 'BACKWARD');

      expect(submit(r.host).disabled).toBe(true);
      expect(r.host.textContent).toContain('Choose a surface as well as a mode');

      choose(r.fixture, 'subject-surface', 'WIRE_JSON');

      expect(submit(r.host).disabled).toBe(false);
    });
  });

  describe('saying what the policy choices mean', () => {
    const sentence = (host: HTMLElement, id: string) =>
      host.querySelector<HTMLElement>(
        '#' + host.querySelector('#' + id)!.getAttribute('aria-describedby')!,
      )!;

    it('describes each select by the meaning of its choice, under the select, as the choice changes', () => {
      const r = render();

      expect(sentence(r.host, 'subject-mode').hidden).toBe(true);

      choose(r.fixture, 'subject-mode', 'FORWARD_TRANSITIVE');
      choose(r.fixture, 'subject-surface', 'SOURCE');

      expect(sentence(r.host, 'subject-mode').textContent?.trim()).toBe(
        COMPATIBILITY_MODE_MEANING.FORWARD_TRANSITIVE,
      );
      expect(sentence(r.host, 'subject-surface').textContent?.trim()).toBe(
        COMPATIBILITY_SURFACE_MEANING.SOURCE,
      );
    });

    it('leaves the selects named by their labels, which the e2e specs find them by', () => {
      const r = render();

      for (const [id, label] of [
        ['subject-mode', 'Mode'],
        ['subject-surface', 'Surface'],
      ] as const) {
        const select = r.host.querySelector<HTMLSelectElement>('#' + id)!;

        expect(select.labels?.[0]?.textContent?.trim()).toBe(label);
        expect(select.hasAttribute('aria-label')).toBe(false);
      }
    });

    it('offers every word at once, on a button, and shows none of them until it is pressed', () => {
      const r = render();
      // The form's own selects hold the tokens as options; the guide's are terms.
      expect(r.host.querySelectorAll('cd-compatibility-guide dt')).toHaveLength(0);

      click(r.fixture, 'What do these mean?');

      expect(
        [...r.host.querySelectorAll('cd-compatibility-guide dt')].map((dt) =>
          dt.textContent?.trim(),
        ),
      ).toEqual([...COMPATIBILITY_MODES, ...COMPATIBILITY_SURFACES]);
    });
  });

  describe('a name that is already taken', () => {
    it('is advice: it says so and links to the subject, but does not block', () => {
      // The list is a snapshot, so vetoing on it would be wrong in exactly the strict direction.
      // The registry's own 409 is what refuses.
      const r = render({ existing: ['acme.orders.OrderCreated'] });
      validForm(r);

      expect(r.host.textContent).toContain('already in this environment');
      expect(r.host.querySelector('a[href="/subjects/acme.orders.OrderCreated"]')).not.toBeNull();
      expect(submit(r.host).disabled).toBe(false);
    });

    it('warns about a name that differs only in case, because the registry will not', () => {
      const r = render({ existing: ['acme.orders.OrderCreated'] });
      validForm(r, 'acme.orders.ordercreated');

      expect(r.host.textContent).toContain('differs from this name only in case');
      expect(r.host.textContent).toContain('separate subject');
      expect(submit(r.host).disabled).toBe(false);
    });
  });

  describe('creating takes two clicks', () => {
    it('asks first, naming what is permanent, and sends nothing', () => {
      const r = render();
      validForm(r);
      click(r.fixture, 'Create subject');

      expect(r.store.create).not.toHaveBeenCalled();
      expect(r.host.textContent).toContain('Create acme.orders.OrderCreated in dev?');
      expect(r.host.textContent).toContain('The name is permanent');
      expect(r.host.textContent).toContain('cannot be renamed or deleted');
      expect(r.host.textContent).toContain('JSON Schema, open');
      expect(r.host.textContent).toContain('can be changed later, from the subject’s own page');
      expect(r.host.textContent).toContain('Changing the policy takes an admin');
      expect(r.host.textContent).not.toContain('only through the API');
    });

    it('sends exactly what the first click named, on the second', () => {
      const r = render();
      validForm(r, '  acme.orders.OrderCreated  ');
      pick(r.fixture, 'Avro');
      pick(r.fixture, 'Closed');
      choose(r.fixture, 'subject-mode', 'FULL');
      choose(r.fixture, 'subject-surface', 'SOURCE');
      click(r.fixture, 'Create subject');
      click(r.fixture, 'Yes, create it');

      expect(r.store.create).toHaveBeenCalledTimes(1);
      expect(r.store.create).toHaveBeenCalledWith({
        name: 'acme.orders.OrderCreated',
        format: 'avro',
        owner: 'alice@example.com',
        contentModel: 'CLOSED',
        compatibility: { mode: 'FULL', surface: 'SOURCE' },
      } satisfies NewSubject);
    });

    it('sends nothing to inherit the policy', () => {
      const r = render();
      validForm(r);
      click(r.fixture, 'Create subject');
      click(r.fixture, 'Yes, create it');

      expect(r.store.create.mock.calls[0]?.[0]).toMatchObject({ compatibility: null });
    });

    it.each([
      ['the name', (r: Rendered) => type(r.fixture, 'subject-name', 'acme.orders.OrderShipped')],
      ['the owner', (r: Rendered) => type(r.fixture, 'subject-owner', 'someone-else')],
      ['the format', (r: Rendered) => pick(r.fixture, 'Protobuf')],
      ['the content model', (r: Rendered) => pick(r.fixture, 'Closed')],
      ['the policy', (r: Rendered) => choose(r.fixture, 'subject-mode', 'FULL')],
    ])('withdraws the confirmation when %s is edited after the first click', (_label, edit) => {
      // Otherwise the second click would send something the first never showed.
      const r = render();
      validForm(r);
      click(r.fixture, 'Create subject');
      edit(r);

      expect(hasButton(r.host, 'Yes, create it')).toBe(false);
      expect(r.store.create).not.toHaveBeenCalled();
    });

    it('keeps the confirmation when only the title changes, which is not sent', () => {
      const r = render();
      validForm(r);
      click(r.fixture, 'Create subject');
      type(r.fixture, 'subject-title', 'Some words');

      expect(hasButton(r.host, 'Yes, create it')).toBe(true);
    });

    it('lets the author back out without sending anything', () => {
      const r = render();
      validForm(r);
      click(r.fixture, 'Create subject');
      click(r.fixture, 'Keep editing');

      expect(hasButton(r.host, 'Yes, create it')).toBe(false);
      expect(hasButton(r.host, 'Create subject')).toBe(true);
      expect(r.store.create).not.toHaveBeenCalled();
    });

    it('does not ask at all about a form the registry would refuse', () => {
      const r = render();
      type(r.fixture, 'subject-name', 'no spaces');
      click(r.fixture, 'Create subject');

      expect(hasButton(r.host, 'Yes, create it')).toBe(false);
    });

    it('says it is creating, and cannot be pressed again, while the request is out', () => {
      const r = render({ submitting: true });
      validForm(r);

      expect(submit(r.host).disabled).toBe(true);
      expect(submit(r.host).textContent).toContain('Creating…');
    });
  });

  describe('what the registry says', () => {
    it('renders a refusal above the form, and leaves the form editable', () => {
      const r = render({
        error: {
          attempted: 'acme.orders.OrderCreated',
          error: new ConcordatError({
            status: 400,
            code: 'subject_name_invalid',
            detail: 'The registry says no.',
          }),
        },
      });

      expect(r.host.textContent).toContain('The registry refused this subject');
      expect(r.host.textContent).toContain('The registry says no.');
      expect(r.host.querySelector('form')).not.toBeNull();
    });

    it('offers the existing subject when the name is taken', () => {
      const r = render({
        error: {
          attempted: 'acme.orders.OrderCreated',
          error: new ConcordatError({
            status: 409,
            code: 'subject_already_exists',
            detail: "Subject 'acme.orders.OrderCreated' already exists in this environment.",
          }),
        },
      });

      expect(r.host.querySelector('a[href="/subjects/acme.orders.OrderCreated"]')).not.toBeNull();
    });

    it('goes to the subject the registry answered with, not the one that was typed', () => {
      // It trims before it validates, so the two can differ — and only the stored one can be opened.
      const r = render();
      r.store.created.set(subjectNamed('stored.by.the.registry'));
      r.fixture.detectChanges();

      expect(r.navigate).toHaveBeenCalledWith(['/subjects', 'stored.by.the.registry']);
    });

    it('does not go anywhere before the registry has answered', () => {
      const r = render();
      validForm(r);
      click(r.fixture, 'Create subject');
      click(r.fixture, 'Yes, create it');

      expect(r.navigate).not.toHaveBeenCalled();
    });
  });

  it('reads the subject list for one thing, and a failure to read it blocks nothing', () => {
    const r = render();

    expect(r.list.load).toHaveBeenCalled();
    validForm(r);
    expect(submit(r.host).disabled).toBe(false);
  });
});
