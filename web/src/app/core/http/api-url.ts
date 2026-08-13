import type { ConcordatConfig } from '../config/app-config';

/** The version prefix every REST route sits under. Part of the contract, not a detail. */
const API_VERSION = 'v1';

/** The absolute or root-relative base of the versioned API. */
export function apiRoot(config: ConcordatConfig): string {
  return `${config.apiBaseUrl}/${API_VERSION}`;
}

/** The base for everything scoped to one environment. */
export function environmentRoot(config: ConcordatConfig, environment: string): string {
  return `${apiRoot(config)}/environments/${encodeURIComponent(environment)}`;
}

/**
 * Whether a request is going to the Concordat API.
 *
 * The auth and tenant interceptors both gate on this, and the reason is not tidiness: an
 * interceptor that stamps `Authorization` onto every outgoing request will eventually stamp
 * it onto a request to somewhere else — an avatar on a CDN, a doc link, a third-party map
 * tile — and hand that host a bearer token for the registry. Matching the configured API
 * root, origin included, means an absolute URL to another host simply does not qualify.
 *
 * @param url The request URL, absolute or relative.
 * @param config The configuration in force.
 * @param documentBase `document.baseURI`, used to resolve a relative URL the same way the
 *   browser will.
 */
export function isApiUrl(url: string, config: ConcordatConfig, documentBase: string): boolean {
  let target: URL;
  let root: URL;

  try {
    target = new URL(url, documentBase);
    root = new URL(apiRoot(config), documentBase);
  } catch {
    // An unparseable URL is not one we are willing to call ours.
    return false;
  }

  if (target.origin !== root.origin) {
    return false;
  }

  // Path-prefix, not `startsWith` on the raw string: `/v1x/...` must not match `/v1`.
  return target.pathname === root.pathname || target.pathname.startsWith(`${root.pathname}/`);
}
