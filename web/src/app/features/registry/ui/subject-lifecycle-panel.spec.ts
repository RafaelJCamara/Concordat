import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { SubjectLifecyclePanel } from './subject-lifecycle-panel';

// The panel's own logic is which step it offers, what the second click names, and that deleting
// asks for the name. Whether the registry allows either is the registry's, and is proved there.

describe('SubjectLifecyclePanel', () => {
  let fixture: ComponentFixture<SubjectLifecyclePanel>;
  let host: HTMLElement;
  let retired: number;
  let purged: number;

  function open(
    lifecycle: string,
    versions = 2,
    error: string | null = null,
    saving = false,
  ): void {
    TestBed.configureTestingModule({ imports: [SubjectLifecyclePanel] });
    fixture = TestBed.createComponent(SubjectLifecyclePanel);
    fixture.componentRef.setInput('subject', 'orders.created');
    fixture.componentRef.setInput('lifecycle', lifecycle);
    fixture.componentRef.setInput('versions', versions);
    fixture.componentRef.setInput('error', error);
    fixture.componentRef.setInput('saving', saving);
    retired = 0;
    purged = 0;
    fixture.componentInstance.retire.subscribe(() => retired++);
    fixture.componentInstance.purge.subscribe(() => purged++);
    fixture.detectChanges();
    host = fixture.nativeElement as HTMLElement;
  }

  const button = (name: RegExp) =>
    [...host.querySelectorAll('button')].find((b) => name.test(b.textContent ?? ''));
  // Whitespace folded: the template wraps a sentence over several lines.
  const text = () => (host.textContent ?? '').replace(/\s+/g, ' ');
  const nameBox = () => host.querySelector<HTMLInputElement>('#subject-purge-name');

  function click(name: RegExp): void {
    (button(name) as HTMLButtonElement).click();
    fixture.detectChanges();
  }

  function type(value: string): void {
    nameBox()!.value = value;
    nameBox()!.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  describe('a subject that is not retired', () => {
    it.each(['ACTIVE', 'DEPRECATED'])(
      'offers retiring and nothing else when it is %s',
      (lifecycle) => {
        open(lifecycle);

        expect(button(/Retire subject/)).toBeDefined();
        expect(button(/Delete for good/)).toBeUndefined();
        expect(text()).not.toContain('Delete this subject');
      },
    );

    it('sends nothing on the first click, and names what retiring does on it', () => {
      open('ACTIVE');

      click(/Retire subject/);

      expect(retired).toBe(0);
      expect(text()).toContain('Retire orders.created?');
      expect(text()).toContain('It cannot be undone');
      expect(text()).toContain('SUBJECT_RETIRED');
      expect(text()).toContain('Nobody is notified');
    });

    it('retires on the second click, and only then', () => {
      open('ACTIVE');
      click(/Retire subject/);

      click(/Yes, retire it/);

      expect(retired).toBe(1);
      expect(purged).toBe(0);
    });

    it('goes back without sending anything', () => {
      open('ACTIVE');
      click(/Retire subject/);

      click(/Keep it active/);

      expect(retired).toBe(0);
      expect(button(/Retire subject/)).toBeDefined();
    });

    it('says the registry did not retire it, with its words', () => {
      open('ACTIVE', 2, 'Subject is already retired.');

      expect(text()).toContain('The registry did not retire it');
      expect(text()).toContain('Subject is already retired.');
    });
  });

  describe('when the registry has answered', () => {
    it('closes a retirement confirmation once the subject is retired, and offers the next step instead', () => {
      // The page reads the subject again after a retirement and the same panel is handed the new state.
      // Left open, the confirmation would have turned into the deletion one on the same screen.
      open('ACTIVE');
      click(/Retire subject/);

      fixture.componentRef.setInput('lifecycle', 'RETIRED');
      fixture.detectChanges();

      expect(text()).not.toContain('Delete orders.created for good?');
      expect(text()).not.toContain('Retire orders.created?');
      expect(button(/Delete for good/)).toBeDefined();
    });

    it('forgets an open confirmation and a typed name when a different subject arrives', () => {
      open('RETIRED');
      click(/Delete for good/);
      type('orders.created');

      fixture.componentRef.setInput('subject', 'payments.taken');
      fixture.detectChanges();

      expect(nameBox()).toBeNull();
      expect(button(/Delete for good/)).toBeDefined();
    });
  });

  describe('a retired subject', () => {
    it('offers deleting and nothing else', () => {
      open('RETIRED');

      expect(text()).toContain('Delete this subject');
      expect(button(/Delete for good/)).toBeDefined();
      expect(button(/Retire subject/)).toBeUndefined();
    });

    it('names how many versions go, and what stops a deletion, on the first click', () => {
      open('RETIRED', 3);

      click(/Delete for good/);

      expect(purged).toBe(0);
      expect(text()).toContain('Delete orders.created for good?');
      expect(text()).toContain('its 3 versions');
      expect(text()).toContain('a contract binds it');
      expect(text()).toContain('SUBJECT_PURGED');
    });

    it('says one version in the singular', () => {
      open('RETIRED', 1);

      click(/Delete for good/);

      expect(text()).toContain('its 1 version.');
    });

    it('holds the button back until the name is typed out, exactly', () => {
      open('RETIRED');
      click(/Delete for good/);
      const confirm = () => button(/Yes, delete it for good/) as HTMLButtonElement;

      expect(confirm().disabled).toBe(true);

      type('orders.creat');
      expect(confirm().disabled).toBe(true);

      // Names are case-sensitive: a different subject, and not a typo to forgive.
      type('ORDERS.CREATED');
      expect(confirm().disabled).toBe(true);

      type(' orders.created');
      expect(confirm().disabled).toBe(true);

      type('orders.created');
      expect(confirm().disabled).toBe(false);
    });

    it('deletes on the second click once the name matches', () => {
      open('RETIRED');
      click(/Delete for good/);
      type('orders.created');

      click(/Yes, delete it for good/);

      expect(purged).toBe(1);
      expect(retired).toBe(0);
    });

    it('does not delete from a click on a button that is held back', () => {
      open('RETIRED');
      click(/Delete for good/);

      click(/Yes, delete it for good/);

      expect(purged).toBe(0);
    });

    it('forgets what was typed when it is closed, so the next opening starts empty', () => {
      open('RETIRED');
      click(/Delete for good/);
      type('orders.created');

      click(/Keep it/);
      click(/Delete for good/);

      expect(nameBox()!.value).toBe('');
      expect((button(/Yes, delete it for good/) as HTMLButtonElement).disabled).toBe(true);
    });

    it('shows the registry’s refusal in its own words, saying it did not delete', () => {
      open(
        'RETIRED',
        2,
        "Subject is still in use by contract 'orders'. Remove it from them first.",
      );

      expect(text()).toContain('The registry did not delete it');
      expect(text()).toContain("still in use by contract 'orders'");
    });

    it('disables the button while the registry has not answered', () => {
      open('RETIRED', 2, null, true);
      click(/Delete for good/);
      type('orders.created');

      expect((button(/Deleting…/) as HTMLButtonElement).disabled).toBe(true);
    });
  });
});
