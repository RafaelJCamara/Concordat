import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { OneTimeSecret } from './one-time-secret';

// The property worth pinning is that a failed copy says so. The silent version leaves the button
// on "Copy" as though nothing had been asked, and the person closes the page believing they hold
// a secret the registry will never show again.

describe('OneTimeSecret', () => {
  let fixture: ComponentFixture<OneTimeSecret>;
  let host: HTMLElement;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [OneTimeSecret] });
    fixture = TestBed.createComponent(OneTimeSecret);
    fixture.componentRef.setInput('heading', 'Copy this key now');
    fixture.componentRef.setInput('secret', 'cdt_abc_SECRET');
    fixture.componentRef.setInput('label', 'API key secret');
    fixture.detectChanges();
    host = fixture.nativeElement as HTMLElement;
  });

  afterEach(() => vi.unstubAllGlobals());

  function button(text: string): HTMLButtonElement {
    return [...host.querySelectorAll('button')].find((b) => b.textContent?.trim() === text)!;
  }

  it('shows the heading and the secret, read-only, with an accessible name', () => {
    const field = host.querySelector('input') as HTMLInputElement;

    expect(host.textContent).toContain('Copy this key now');
    expect(field.value).toBe('cdt_abc_SECRET');
    expect(field.readOnly).toBe(true);
    expect(field.getAttribute('aria-label')).toBe('API key secret');
  });

  it('is announced, because it appears in response to an action and cannot be recovered', () => {
    expect(host.querySelector('[role="alert"]')).not.toBeNull();
  });

  it('copies to the clipboard and says it did', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal('navigator', { clipboard: { writeText } });

    button('Copy').click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(writeText).toHaveBeenCalledWith('cdt_abc_SECRET');
    expect(button('Copied')).toBeDefined();
  });

  it('says so when the clipboard is unavailable, rather than staying quietly on "Copy"', async () => {
    vi.stubGlobal('navigator', {
      clipboard: { writeText: vi.fn().mockRejectedValue(new Error('denied')) },
    });

    button('Copy').click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(host.textContent).toContain('Could not copy automatically');
    expect(button('Copy')).toBeDefined();
  });

  it('tells its owner when the secret has been taken, and only then', () => {
    let dismissed = 0;
    fixture.componentInstance.dismissed.subscribe(() => dismissed++);

    expect(dismissed).toBe(0);

    button('I have saved it').click();

    expect(dismissed).toBe(1);
  });
});
