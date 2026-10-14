import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { describe, expect, it } from 'vitest';
import type { ConsumeBinding, PublishBinding } from '../../../domain/contracts/contract';
import { BindingList, type BindingRef } from './binding-list';
import { EnforcementBadge } from './enforcement-badge';

// The presentational pieces of the contract screens: how a mode is drawn, and how a binding is
// read. One file for both because each is small, and neither has behaviour beyond rendering.

const publish: PublishBinding = {
  id: 'p1',
  exchange: 'orders',
  routingKeyPattern: 'orders.*',
  brokerId: null,
  virtualHost: '/',
  precedence: null,
  subjects: [{ subject: 'orders.created', selector: '>=2' }],
};

const consume: ConsumeBinding = {
  id: 'c1',
  queue: 'billing.orders',
  brokerId: null,
  virtualHost: '/billing',
  subjects: [{ subject: 'orders.created', selector: 'latest' }],
};

function renderBindings(
  publishes: readonly PublishBinding[],
  consumes: readonly ConsumeBinding[],
): HTMLElement {
  TestBed.configureTestingModule({ providers: [provideRouter([])] });
  const fixture = TestBed.createComponent(BindingList);
  fixture.componentRef.setInput('publishes', publishes);
  fixture.componentRef.setInput('consumes', consumes);
  fixture.detectChanges();

  return fixture.nativeElement as HTMLElement;
}

describe('EnforcementBadge', () => {
  function render(mode: 'OFF' | 'MONITOR' | 'ENFORCE'): HTMLElement {
    const fixture = TestBed.createComponent(EnforcementBadge);
    fixture.componentRef.setInput('mode', mode);
    fixture.detectChanges();

    return fixture.nativeElement as HTMLElement;
  }

  it.each(['OFF', 'MONITOR', 'ENFORCE'] as const)('always shows the word %s', (mode) => {
    // The tone is for scanning and the token is for reading: OFF and ENFORCE are not ordered by
    // colour, so the badge never leans on colour alone.
    expect(render(mode).textContent).toContain(mode);
  });

  it('says in a tooltip what ENFORCE does', () => {
    expect(render('ENFORCE').querySelector('[title]')?.getAttribute('title')).toContain('Refuses');
  });

  it('says in a tooltip that MONITOR lets the message through', () => {
    expect(render('MONITOR').querySelector('[title]')?.getAttribute('title')).toContain(
      'lets the message through',
    );
  });

  it('tones the three differently', () => {
    const html = (['OFF', 'MONITOR', 'ENFORCE'] as const).map((m) => render(m).innerHTML);

    expect(new Set(html.map((h) => h.match(/bg-[a-z-]+\/10|bg-muted/)?.[0])).size).toBe(3);
  });
});

describe('BindingList, changing and removing', () => {
  function render(editable: boolean, activeId: string | null = null) {
    TestBed.configureTestingModule({ providers: [provideRouter([])] });
    const fixture = TestBed.createComponent(BindingList);
    fixture.componentRef.setInput('publishes', [publish]);
    fixture.componentRef.setInput('consumes', [consume]);
    fixture.componentRef.setInput('editable', editable);
    fixture.componentRef.setInput('activeId', activeId);

    const changes: BindingRef[] = [];
    const removals: BindingRef[] = [];
    fixture.componentInstance.changeRequested.subscribe((ref) => changes.push(ref));
    fixture.componentInstance.removeRequested.subscribe((ref) => removals.push(ref));
    fixture.detectChanges();

    return { host: fixture.nativeElement as HTMLElement, changes, removals };
  }

  const named = (host: HTMLElement, label: string) =>
    host.querySelector(`button[aria-label="${label}"]`) as HTMLButtonElement | null;

  it('offers neither without the right to change a contract', () => {
    const { host } = render(false);

    expect(host.querySelectorAll('button')).toHaveLength(0);
  });

  it('offers each binding its own Change and Remove, named for the binding', () => {
    const { host } = render(true);

    expect(named(host, 'Change orders / orders.*')).not.toBeNull();
    expect(named(host, 'Remove orders / orders.*')).not.toBeNull();
    expect(named(host, 'Change billing.orders')).not.toBeNull();
    expect(named(host, 'Remove billing.orders')).not.toBeNull();
  });

  it('only says which binding and which kind; it changes nothing itself', () => {
    const { host, changes, removals } = render(true);

    named(host, 'Change orders / orders.*')!.click();
    named(host, 'Remove billing.orders')!.click();

    expect(changes).toEqual([{ kind: 'publish', id: 'p1' }]);
    expect(removals).toEqual([{ kind: 'consume', id: 'c1' }]);
  });

  it('draws the binding being worked on', () => {
    const { host } = render(true, 'c1');
    const rows = [...host.querySelectorAll('li.border-primary')];

    expect(rows).toHaveLength(1);
    expect(rows[0]?.textContent).toContain('billing.orders');
  });
});

describe('BindingList', () => {
  it('counts each kind', () => {
    const host = renderBindings([publish], [consume]);

    expect(host.textContent).toContain('Publishes · 1');
    expect(host.textContent).toContain('Consumes · 1');
  });

  it('shows the exchange, the pattern and the virtual host', () => {
    const host = renderBindings([publish], []);

    expect(host.textContent).toContain('orders');
    expect(host.textContent).toContain('orders.*');
    expect(host.textContent).toContain('vhost /');
  });

  it('links each subject to its own screen, with the selector beside it', () => {
    const host = renderBindings([publish], []);
    const link = host.querySelector('a[href="/subjects/orders.created"]');

    expect(link?.textContent?.replace(/\s+/g, '')).toBe('orders.created@>=2');
  });

  it('shows precedence only when one was declared', () => {
    expect(renderBindings([{ ...publish, precedence: 3 }], []).textContent).toContain(
      'precedence 3',
    );
  });

  it('does not show a precedence column of dashes when none was', () => {
    expect(renderBindings([publish], []).textContent).not.toContain('precedence');
  });

  it('shows a zero precedence, which is a precedence', () => {
    expect(renderBindings([{ ...publish, precedence: 0 }], []).textContent).toContain(
      'precedence 0',
    );
  });

  it('says a contract with no publish binding governs no route yet', () => {
    expect(renderBindings([], [consume]).textContent).toContain('governs no route yet');
  });

  it('shows the queue and its virtual host for a consume binding', () => {
    const host = renderBindings([], [consume]);

    expect(host.textContent).toContain('billing.orders');
    expect(host.textContent).toContain('vhost /billing');
  });

  it('says plainly when there are no consume bindings', () => {
    expect(renderBindings([publish], []).textContent).toContain('No consume bindings');
  });
});
