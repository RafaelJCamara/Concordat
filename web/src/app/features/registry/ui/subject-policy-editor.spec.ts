import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import type { CompatibilityPolicy } from '../../../domain/registry/compatibility';
import { SubjectPolicyEditor } from './subject-policy-editor';

// What is the editor's own: when it lets somebody ask, how the two requests that look alike —
// inheriting, and choosing what the default happens to be — are told apart, and what the second
// click names. Which pairs are legal is `checkPolicyEdit`'s, held against the registry.

describe('SubjectPolicyEditor', () => {
  let fixture: ComponentFixture<SubjectPolicyEditor>;
  let host: HTMLElement;
  let confirmed: CompatibilityPolicy[];

  function open(current: CompatibilityPolicy): void {
    fixture.componentRef.setInput('current', current);
    fixture.detectChanges();
    host = fixture.nativeElement as HTMLElement;
  }

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [SubjectPolicyEditor] });
    fixture = TestBed.createComponent(SubjectPolicyEditor);
    fixture.componentRef.setInput('subject', 'orders.created');
    confirmed = [];
    fixture.componentInstance.confirmed.subscribe((policy) => confirmed.push(policy));
  });

  const select = (id: string) => host.querySelector<HTMLSelectElement>(`#${id}`)!;
  const button = (name: RegExp) =>
    [...host.querySelectorAll('button')].find((b) => name.test(b.textContent ?? ''))!;

  function choose(mode: string, surface: string): void {
    select('subject-policy-mode').value = mode;
    select('subject-policy-mode').dispatchEvent(new Event('change'));
    select('subject-policy-surface').value = surface;
    select('subject-policy-surface').dispatchEvent(new Event('change'));
    fixture.detectChanges();
  }

  const review = () => button(/Review change/) as HTMLButtonElement;

  describe('opening', () => {
    it('starts on the policy already shown, so changing it is an edit', () => {
      open({ mode: 'BACKWARD', surface: 'WIRE_JSON' });

      expect(select('subject-policy-mode').value).toBe('BACKWARD');
      expect(select('subject-policy-surface').value).toBe('WIRE_JSON');
      expect(review().disabled).toBe(true);
    });

    it('starts on inherit for a subject that inherits, and offers it in both selects', () => {
      open({ mode: null, surface: null });

      expect(select('subject-policy-mode').value).toBe('');
      expect(select('subject-policy-surface').value).toBe('');
      expect(select('subject-policy-mode').textContent).toContain(
        'Inherit the environment default',
      );
    });

    it('offers every mode and every surface the registry has', () => {
      open({ mode: null, surface: null });

      expect(
        [...select('subject-policy-mode').options].map((o) => o.value).filter((v) => v !== ''),
      ).toEqual([
        'NONE',
        'BACKWARD',
        'BACKWARD_TRANSITIVE',
        'FORWARD',
        'FORWARD_TRANSITIVE',
        'FULL',
        'FULL_TRANSITIVE',
      ]);
      expect(
        [...select('subject-policy-surface').options].map((o) => o.value).filter((v) => v !== ''),
      ).toEqual(['WIRE', 'WIRE_JSON', 'SOURCE']);
    });
  });

  describe('what blocks', () => {
    beforeEach(() => open({ mode: null, surface: null }));

    it('blocks half a policy, which the registry answers 400, and says which half is missing', () => {
      choose('FULL', '');

      expect(host.textContent).toContain('Choose a surface as well as a mode');
      expect(review().disabled).toBe(true);

      choose('', 'WIRE');

      expect(host.textContent).toContain('Choose a mode as well as a surface');
      expect(review().disabled).toBe(true);
    });

    it('lets a whole policy through', () => {
      choose('FULL', 'SOURCE');

      expect(review().disabled).toBe(false);
    });

    it('does not tell somebody off on open for the policy they have not yet touched', () => {
      expect(host.textContent).not.toContain('already the policy');
      expect(host.textContent).not.toContain('no policy of its own, as of');
    });

    it('says, as advice, when the choice is the policy already shown', () => {
      open({ mode: 'FULL', surface: 'WIRE' });
      choose('FULL_TRANSITIVE', 'WIRE');
      choose('FULL', 'WIRE');

      expect(host.textContent).toContain('already the policy');
      expect(review().disabled).toBe(true);
    });
  });

  describe('pinning', () => {
    beforeEach(() => {
      open({ mode: null, surface: null });
      choose('FORWARD', 'WIRE_JSON');
      review().click();
      fixture.detectChanges();
    });

    it('asks, naming the subject and the policy, and sends nothing', () => {
      expect(host.textContent).toContain('Pin orders.created to FORWARD · WIRE_JSON?');
      expect(confirmed).toEqual([]);
    });

    it('says what the registry does about the next registration and about the ones before it', () => {
      const text = host.textContent ?? '';

      expect(text).toContain('takes effect for the next version registered');
      expect(text).toContain('Versions already registered are not judged again');
      expect(text).toContain('a version held at the approval gate stays held');
    });

    it('says the subject no longer follows the environment', () => {
      expect(host.textContent).toContain('does not follow');
    });

    it('says what the registry does not do: audit it, or tell anybody', () => {
      expect(host.textContent).toContain('not written to the audit log');
      expect(host.textContent).toContain('Nobody is notified');
    });

    it('does not say the subject is judged by the environment’s default, which is about inheriting', () => {
      expect(host.textContent).not.toContain('judged by its environment’s default');
    });

    it('sends the whole policy on the second click, once', () => {
      button(/Yes, change the policy/).click();

      expect(confirmed).toEqual([{ mode: 'FORWARD', surface: 'WIRE_JSON' }]);
    });

    it('is withdrawn by choosing something else, so the second click cannot send what was not shown', () => {
      choose('FULL', 'WIRE_JSON');

      expect(host.textContent).not.toContain('Yes, change the policy');
      expect(confirmed).toEqual([]);
    });
  });

  describe('inheriting', () => {
    beforeEach(() => {
      open({ mode: 'FULL', surface: 'SOURCE' });
      choose('', '');
      review().click();
      fixture.detectChanges();
    });

    it('asks as a different question from pinning, and sends nulls on the second click', () => {
      expect(host.textContent).toContain(
        'Remove the policy of orders.created, so that it inherits?',
      );

      button(/Yes, inherit/).click();

      expect(confirmed).toEqual([{ mode: null, surface: null }]);
    });

    it('says inheriting is not the same as choosing the current default', () => {
      expect(host.textContent).toContain(
        'Inheriting is not the same as choosing the current default',
      );
    });

    it('says an inheriting subject is judged by its environment’s default, everywhere it is read', () => {
      // Measured against the real registry (ADR-033): in an environment whose default is NONE a
      // breaking version of a subject with no policy of its own is accepted. `e2e/subject-settings.spec.ts`
      // holds the sentence; this holds that the screen says it and says nothing the old registry did.
      const text = host.textContent ?? '';

      expect(text).toContain('this subject is judged by its environment’s default');
      expect(text).toContain('Registering a version, checking a schema without registering it');
      expect(text).toContain('impact analysis and promotion');
    });

    it('no longer describes the built-in default as what an inheriting subject is judged by', () => {
      // The registry used to judge it by BACKWARD · WIRE_JSON whatever the environment said, and
      // this screen told the person so. A leftover would now be false, and silently.
      const text = host.textContent ?? '';

      expect(text).not.toContain('As this registry stands');
      expect(text).not.toContain('BACKWARD · WIRE_JSON');
      expect(text).not.toContain('whatever the environment’s default is set to');
    });
  });

  it('shows the registry’s own words when it refuses, and leaves the form editable', () => {
    open({ mode: null, surface: null });
    fixture.componentRef.setInput('error', 'This endpoint requires subject:admin.');
    fixture.detectChanges();

    expect(host.textContent).toContain('The registry refused this change');
    expect(host.textContent).toContain('This endpoint requires subject:admin.');
    expect(select('subject-policy-mode').disabled).toBe(false);
  });

  it('cannot be asked again while the registry has not answered', () => {
    open({ mode: null, surface: null });
    choose('FULL', 'WIRE');
    fixture.componentRef.setInput('saving', true);
    fixture.detectChanges();

    expect(review().disabled).toBe(true);
  });

  it('follows a refresh of the policy while pristine, and never overwrites what is being chosen', () => {
    open({ mode: null, surface: null });
    fixture.componentRef.setInput('current', { mode: 'FULL', surface: 'WIRE' });
    fixture.detectChanges();
    expect(select('subject-policy-mode').value).toBe('FULL');

    choose('NONE', 'SOURCE');
    fixture.componentRef.setInput('current', { mode: 'FORWARD', surface: 'WIRE' });
    fixture.detectChanges();

    expect(select('subject-policy-mode').value).toBe('NONE');
  });
});
