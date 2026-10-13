import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { SubjectOwnerEditor } from './subject-owner-editor';

// The editor's own logic is when it lets somebody ask, what the second click names, and that the
// second click can only send what the first showed. The rules about what an owner may be are
// `checkOwnerEdit`'s and are tested there and against the registry.

describe('SubjectOwnerEditor', () => {
  let fixture: ComponentFixture<SubjectOwnerEditor>;
  let host: HTMLElement;
  let confirmed: string[];
  let cancelled: number;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [SubjectOwnerEditor] });
    fixture = TestBed.createComponent(SubjectOwnerEditor);
    fixture.componentRef.setInput('subject', 'orders.created');
    fixture.componentRef.setInput('current', 'orders-team');
    confirmed = [];
    cancelled = 0;
    fixture.componentInstance.confirmed.subscribe((owner) => confirmed.push(owner));
    fixture.componentInstance.cancelled.subscribe(() => cancelled++);
    fixture.detectChanges();
    host = fixture.nativeElement as HTMLElement;
  });

  const field = () => host.querySelector<HTMLInputElement>('input')!;
  const button = (name: RegExp) =>
    [...host.querySelectorAll('button')].find((b) => name.test(b.textContent ?? ''))!;

  function type(value: string): void {
    field().value = value;
    field().dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  it('opens holding the owner already shown, and has nothing to review yet', () => {
    expect(field().value).toBe('orders-team');
    expect((button(/Review change/) as HTMLButtonElement).disabled).toBe(true);
  });

  it('does not tell somebody off on open for the owner they have not yet edited', () => {
    // It starts on the owner already shown, so "this is already the owner" is true and useless.
    expect(host.textContent).not.toContain('already the owner');
  });

  it('says so, as advice and not as a refusal, once the field is edited back to the owner', () => {
    type('someone-else');
    type('orders-team');

    expect(host.textContent).toContain('is already the owner');
    expect(field().getAttribute('aria-invalid')).toBeNull();
    expect((button(/Review change/) as HTMLButtonElement).disabled).toBe(true);
  });

  it('asks for a new owner when the field is emptied, and sends nothing', () => {
    type('');

    expect(host.textContent).toContain('Enter the new owner');
    expect((button(/Review change/) as HTMLButtonElement).disabled).toBe(true);
  });

  it('refuses an owner over 256 characters with the registry’s own limit, and marks the field', () => {
    type('o'.repeat(257));

    expect(host.textContent).toContain('at most 256 characters');
    expect(field().getAttribute('aria-invalid')).toBe('true');
    expect((button(/Review change/) as HTMLButtonElement).disabled).toBe(true);
  });

  describe('the first click', () => {
    beforeEach(() => {
      type('  new-team  ');
      button(/Review change/).click();
      fixture.detectChanges();
    });

    it('asks, naming what will change and from what, and sends nothing', () => {
      expect(host.textContent).toContain(
        'Change the owner of orders.created from orders-team to new-team?',
      );
      expect(confirmed).toEqual([]);
    });

    it('says only what was measured the registry does', () => {
      const text = host.textContent ?? '';

      // One entry, whom it names, what it records.
      expect(text).toContain('SUBJECT_UPDATED');
      expect(text).toContain('naming the account that made it');
      expect(text).toContain('recording the previous owner and the new one');
      expect(text).not.toContain('does not record the previous owner');
      expect(text).toContain('Nobody is notified');
      // And that it moves nothing else.
      expect(text).toContain('Only the owner changes');
      expect(text).toContain('stay as they are');
    });

    it('does not call the owner a permission, which the registry does not treat it as', () => {
      expect(host.textContent).toContain('It is a label, not access');
    });

    it('is withdrawn by editing the field, so the second click cannot send something unshown', () => {
      type('a-different-team');

      expect(host.textContent).not.toContain('Yes, change the owner');
      expect(confirmed).toEqual([]);
    });

    it('is declined by keeping editing, which asks nothing of the registry', () => {
      button(/Keep editing/).click();
      fixture.detectChanges();

      expect(host.textContent).not.toContain('Yes, change the owner');
      expect(confirmed).toEqual([]);
    });
  });

  it('sends the trimmed owner on the second click, once', () => {
    type('  new-team  ');
    button(/Review change/).click();
    fixture.detectChanges();
    button(/Yes, change the owner/).click();

    expect(confirmed).toEqual(['new-team']);
  });

  it('cancels without sending', () => {
    button(/Cancel/).click();

    expect(cancelled).toBe(1);
    expect(confirmed).toEqual([]);
  });

  it('shows the registry’s own words when it refuses, and leaves the form editable', () => {
    fixture.componentRef.setInput('error', "Subject 'orders.created' is retired.");
    fixture.detectChanges();

    expect(host.textContent).toContain('The registry refused this change');
    expect(host.textContent).toContain("Subject 'orders.created' is retired.");
    expect(field().disabled).toBe(false);
  });

  it('cannot be asked again while the registry has not answered', () => {
    type('new-team');
    fixture.componentRef.setInput('saving', true);
    fixture.detectChanges();

    expect((button(/Review change/) as HTMLButtonElement).disabled).toBe(true);
  });

  it('follows a refresh of the owner while pristine, and never overwrites what is being typed', () => {
    fixture.componentRef.setInput('current', 'changed-by-somebody-else');
    fixture.detectChanges();
    expect(field().value).toBe('changed-by-somebody-else');

    type('mid-sentence');
    fixture.componentRef.setInput('current', 'changed-again');
    fixture.detectChanges();

    expect(field().value).toBe('mid-sentence');
  });
});
