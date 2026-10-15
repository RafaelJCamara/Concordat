import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { SubjectReviewersEditor } from './subject-reviewers-editor';

// The editor's own logic is when it lets somebody ask, what the second click names, and that the
// second click can only send what the first showed. The rules about what a reviewer list may be are
// `checkReviewersEdit`'s and are tested there and against the registry.

describe('SubjectReviewersEditor', () => {
  let fixture: ComponentFixture<SubjectReviewersEditor>;
  let host: HTMLElement;
  let confirmed: (readonly string[])[];
  let cancelled: number;

  function open(current: readonly string[] = [], actor: string | null = null): void {
    TestBed.configureTestingModule({ imports: [SubjectReviewersEditor] });
    fixture = TestBed.createComponent(SubjectReviewersEditor);
    fixture.componentRef.setInput('subject', 'orders.created');
    fixture.componentRef.setInput('current', current);
    fixture.componentRef.setInput('actor', actor);
    confirmed = [];
    cancelled = 0;
    fixture.componentInstance.confirmed.subscribe((set) => confirmed.push(set));
    fixture.componentInstance.cancelled.subscribe(() => cancelled++);
    fixture.detectChanges();
    host = fixture.nativeElement as HTMLElement;
  }

  const field = () => host.querySelector<HTMLTextAreaElement>('textarea')!;
  const button = (name: RegExp) =>
    [...host.querySelectorAll('button')].find((b) => name.test(b.textContent ?? ''))!;

  function type(value: string): void {
    field().value = value;
    field().dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  describe('opening', () => {
    it('holds the set already named, one to a line, and has nothing to review yet', () => {
      open(['alice@example.com', 'bob@example.com']);

      expect(field().value).toBe('alice@example.com\nbob@example.com');
      expect((button(/Review change/) as HTMLButtonElement).disabled).toBe(true);
    });

    it('is empty for a subject that names nobody', () => {
      open([]);

      expect(field().value).toBe('');
    });

    it('does not tell somebody off on open for the set they have not yet edited', () => {
      open(['alice@example.com']);

      expect(host.textContent).not.toContain('already the reviewers');
    });

    it('says what an empty field does, because it is a real request', () => {
      open([]);

      expect(host.textContent).toContain('Leave it empty and any administrator may');
    });
  });

  describe('judging what was typed', () => {
    it('says so, as advice and not as a refusal, once the field is edited back to the set', () => {
      open(['alice@example.com']);

      type('bob@example.com');
      type('alice@example.com');

      expect(host.textContent).toContain('already the reviewers');
      expect(field().getAttribute('aria-invalid')).toBeNull();
      expect((button(/Review change/) as HTMLButtonElement).disabled).toBe(true);
    });

    it('refuses more than twenty with the registry’s own limit, and marks the field', () => {
      open([]);

      type(Array.from({ length: 21 }, (_, i) => `r${i}@example.com`).join('\n'));

      expect(host.textContent).toContain('at most 20 reviewers');
      expect(field().getAttribute('aria-invalid')).toBe('true');
      expect((button(/Review change/) as HTMLButtonElement).disabled).toBe(true);
    });

    it('refuses a reviewer over 256 characters', () => {
      open([]);

      type('r'.repeat(257));

      expect(host.textContent).toContain('at most 256 characters');
      expect((button(/Review change/) as HTMLButtonElement).disabled).toBe(true);
    });

    it('lets clearing a set be reviewed, because that is a change', () => {
      open(['alice@example.com']);

      type('');

      expect((button(/Review change/) as HTMLButtonElement).disabled).toBe(false);
    });

    it('says when a set would leave the signed-in person unable to decide', () => {
      open([], 'carol@example.com');

      type('alice@example.com');

      expect(host.textContent).toContain('You (carol@example.com) are not in this list');
      // Advice, not a refusal: handing a subject to someone else is a thing a team does.
      expect((button(/Review change/) as HTMLButtonElement).disabled).toBe(false);
    });

    it('says nothing of the sort when the person is named, in any case', () => {
      open([], 'carol@example.com');

      type('Carol@Example.com');

      expect(host.textContent).not.toContain('are not in this list');
    });
  });

  describe('the first click', () => {
    beforeEach(() => {
      open([]);
      type('  alice@example.com \n\nbob@example.com\nALICE@example.com');
      button(/Review change/).click();
      fixture.detectChanges();
    });

    it('asks, naming exactly who would decide, and sends nothing', () => {
      expect(host.textContent).toContain(
        'Let only alice@example.com, bob@example.com decide orders.created?',
      );
      expect(confirmed).toEqual([]);
    });

    it('says only what was measured the registry does', () => {
      const text = host.textContent ?? '';

      expect(text).toContain('Only the people named may approve or reject');
      expect(text).toContain('an administrator included, is refused');
      expect(text).toContain('already waiting as well as to later ones');
      expect(text).toContain('SUBJECT_REVIEWERS_CHANGED');
      expect(text).toContain('recording the previous set and the new one');
      expect(text).toContain('Nobody is notified');
    });

    it('does not call it a lock, which it is not', () => {
      expect(host.textContent).toContain('routes the decision and does not lock it');
    });

    it('is withdrawn by editing the field, so the second click cannot send something unshown', () => {
      type('mallory@example.com');

      expect(host.textContent).not.toContain('Yes, change the reviewers');
      expect(confirmed).toEqual([]);
    });

    it('is declined by keeping editing, which asks nothing of the registry', () => {
      button(/Keep editing/).click();
      fixture.detectChanges();

      expect(host.textContent).not.toContain('Yes, change the reviewers');
      expect(confirmed).toEqual([]);
    });
  });

  it('sends the set as the registry will read it: trimmed, a repeat dropped, in order', () => {
    open([]);
    type('  alice@example.com \n\nbob@example.com\nALICE@example.com');
    button(/Review change/).click();
    fixture.detectChanges();
    button(/Yes, change the reviewers/).click();

    expect(confirmed).toEqual([['alice@example.com', 'bob@example.com']]);
  });

  it('sends an empty list to clear, and says that any administrator may decide again', () => {
    open(['alice@example.com']);
    type('');
    button(/Review change/).click();
    fixture.detectChanges();

    expect(host.textContent).toContain('Clear the reviewers of orders.created?');
    expect(host.textContent).toContain('Any administrator may approve or reject');
    expect(host.textContent).not.toContain('Only the people named');

    button(/Yes, clear the reviewers/).click();

    expect(confirmed).toEqual([[]]);
  });

  it('cancels without sending', () => {
    open([]);

    button(/Cancel/).click();

    expect(cancelled).toBe(1);
    expect(confirmed).toEqual([]);
  });

  it('shows the registry’s own words when it refuses, and leaves the form editable', () => {
    open([]);
    fixture.componentRef.setInput('error', "Subject 'orders.created' is retired.");
    fixture.detectChanges();

    expect(host.textContent).toContain('The registry refused this change');
    expect(host.textContent).toContain("Subject 'orders.created' is retired.");
    expect(field().disabled).toBe(false);
  });

  it('cannot be asked again while the registry has not answered', () => {
    open([]);
    type('alice@example.com');
    fixture.componentRef.setInput('saving', true);
    fixture.detectChanges();

    expect((button(/Review change/) as HTMLButtonElement).disabled).toBe(true);
  });

  it('follows a refresh of the set while pristine, and never overwrites what is being typed', () => {
    open(['alice@example.com']);

    fixture.componentRef.setInput('current', ['alice@example.com', 'bob@example.com']);
    fixture.detectChanges();
    expect(field().value).toBe('alice@example.com\nbob@example.com');

    type('mid-sentence');
    fixture.componentRef.setInput('current', ['changed-again@example.com']);
    fixture.detectChanges();

    expect(field().value).toBe('mid-sentence');
  });
});
