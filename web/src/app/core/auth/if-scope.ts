import {
  Directive,
  effect,
  inject,
  input,
  TemplateRef,
  ViewContainerRef,
  type OnDestroy,
} from '@angular/core';
import { grants, type Scope } from '../../domain/identity/scope';
import { SessionStore } from './session-store';

/**
 * Renders its content only when the session holds one of the given scopes (ADR-018).
 *
 * ```html
 * <button *cdIfScope="['subject:write', 'subject:admin']">Register a version</button>
 * ```
 *
 * **A hidden button is not authorization.** The same scopes are checked server-side on every
 * mutating endpoint, because the API is equally reachable from the CLI, the SDKs and curl.
 * This exists so the UI does not offer an action the server will refuse — which is a
 * usability property, not a security one, and the comment is here because the distinction is
 * exactly what ADR-018 rejected a UI-only restriction over.
 *
 * It lives in `core/auth` rather than `shared/ui` because it reads the session store, and
 * `shared` may not depend on `core`. That boundary is what stops a presentational component
 * quietly acquiring a dependency on application state.
 */
@Directive({ selector: '[cdIfScope]' })
export class IfScope implements OnDestroy {
  private readonly session = inject(SessionStore);
  private readonly template = inject(TemplateRef<unknown>);
  private readonly container = inject(ViewContainerRef);

  private rendered = false;

  /** The scopes that permit the content. Holding any one of them is enough. */
  readonly cdIfScope = input.required<readonly Scope[]>();

  constructor() {
    effect(() => {
      // An unclaimed instance is answered as an owner by the API, so hiding affordances that
      // would in fact work makes a first run look broken.
      const allowed =
        this.session.claimed() === false || grants(this.session.scopes(), this.cdIfScope());

      if (allowed === this.rendered) {
        return;
      }

      this.rendered = allowed;

      if (allowed) {
        this.container.createEmbeddedView(this.template);
      } else {
        // Cleared rather than hidden with CSS. A `display: none` button is still in the DOM,
        // still focusable by a screen reader in some configurations, and still clickable from
        // a console — which turns a usability affordance into a thing that looks like a
        // control someone was meant to find.
        this.container.clear();
      }
    });
  }

  ngOnDestroy(): void {
    this.container.clear();
  }
}
