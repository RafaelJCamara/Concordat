import { ChangeDetectionStrategy, Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { IfScope } from './if-scope';
import { SessionStore } from './session-store';

@Component({
  selector: 'cd-host',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IfScope],
  template: `
    <button *cdIfScope="['subject:write', 'subject:admin']" data-testid="write">Register</button>
  `,
})
class Host {}

/**
 * A hidden button is not authorization — the server refuses the same request with 403. What
 * this directive buys is that the UI does not offer an action the server will refuse, and
 * ADR-018 asks specifically for the affordance to be **absent** rather than disabled: a
 * disabled button invites a support ticket, an absent one does not.
 */
describe('IfScope', () => {
  let session: InstanceType<typeof SessionStore>;

  const render = () => {
    const fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
    return fixture;
  };

  const button = (fixture: ReturnType<typeof render>) =>
    fixture.nativeElement.querySelector('[data-testid="write"]');

  beforeEach(() => {
    TestBed.configureTestingModule({});
    session = TestBed.inject(SessionStore);
  });

  it('renders nothing for a reader', () => {
    session.signIn({ credential: 't', actor: 'analyst', scopes: ['subject:read'] });

    // Absent from the DOM, not hidden with CSS: a display:none button is still focusable in
    // some screen-reader configurations and still clickable from a console.
    expect(button(render())).toBeNull();
  });

  it('renders for someone holding any one of the scopes', () => {
    session.signIn({ credential: 't', actor: 'ops', scopes: ['subject:admin'] });

    expect(button(render())).not.toBeNull();
  });

  it('renders on an unclaimed instance', () => {
    // The API answers an unauthenticated caller as an owner until an account exists, so
    // hiding the affordance would make a first run look broken.
    session.observeInstance({ claimed: false, actor: null, scopes: [] });

    expect(button(render())).not.toBeNull();
  });

  it('renders nothing before the API has answered', () => {
    // Cold start, probe not yet returned. Hiding is the safe direction.
    expect(button(render())).toBeNull();
  });

  it('removes the content when the session expires', () => {
    // The one that matters. A 401 drops the credential mid-session, and a button that stayed
    // behind would offer an action to somebody the registry has just stopped recognising.
    session.signIn({ credential: 't', actor: 'ops', scopes: ['subject:admin'] });
    const fixture = render();
    expect(button(fixture)).not.toBeNull();

    session.expire();
    fixture.detectChanges();

    expect(button(fixture)).toBeNull();
  });

  it('brings the content back on a later sign-in', () => {
    session.observeInstance({ claimed: true, actor: null, scopes: [] });
    const fixture = render();
    expect(button(fixture)).toBeNull();

    session.signIn({ credential: 't', actor: 'ops', scopes: ['subject:write'] });
    fixture.detectChanges();

    expect(button(fixture)).not.toBeNull();
  });
});
