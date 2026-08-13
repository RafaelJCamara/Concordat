import { InjectionToken, type Provider } from '@angular/core';

/** How the registry this app talks to is deployed (DESIGN §10). */
export type DeploymentProfile = 'self-hosted' | 'cloud';

/** Everything the app needs to know about the registry it is pointed at. */
export interface ConcordatConfig {
  /**
   * The origin and path prefix the REST API hangs off, without a trailing slash.
   *
   * Empty means same-origin, which is the self-hosted default: one image serves the API
   * and the embedded SPA (DESIGN §10), so a relative URL is not merely convenient — it
   * means there is no cross-origin request to which a credential could be attached by
   * accident, and no CORS configuration to get wrong.
   */
  readonly apiBaseUrl: string;

  readonly profile: DeploymentProfile;

  /**
   * The tenant to disambiguate to, or null when the deployment has only one.
   *
   * Self-hosted binds a fixed tenant server-side (`TenantId.Default`), so there is nothing
   * for the client to say and it says nothing. See `tenantInterceptor` for why sending a
   * guessed value would be worse than sending none.
   */
  readonly tenant: string | null;

  /** The environment selected on a cold start, before the user picks one. */
  readonly defaultEnvironment: string;
}

/**
 * The configuration in force.
 *
 * An injection token rather than a compile-time `environment.ts` constant because the same
 * built bundle is served by every deployment — self-hosted images, Cloud, a developer's
 * `ng serve`. Baking the API location into the bundle would mean a rebuild per deployment,
 * which is how a self-hosted user ends up pointed at somebody else's registry.
 */
export const CONCORDAT_CONFIG = new InjectionToken<ConcordatConfig>('CONCORDAT_CONFIG');

const DEFAULTS: ConcordatConfig = {
  apiBaseUrl: '',
  profile: 'self-hosted',
  tenant: null,
  defaultEnvironment: 'dev',
};

/** Provides the configuration, defaulting to the self-hosted same-origin shape. */
export function provideConcordatConfig(overrides: Partial<ConcordatConfig> = {}): Provider {
  return { provide: CONCORDAT_CONFIG, useValue: { ...DEFAULTS, ...overrides } };
}
