import { DOCUMENT, inject } from '@angular/core';
import type { HttpInterceptorFn } from '@angular/common/http';
import { CONCORDAT_CONFIG } from '../config/app-config';
import { isApiUrl } from './api-url';

/** The header the Cloud profile uses to disambiguate between a caller's tenants. */
export const TENANT_HEADER = 'X-Concordat-Tenant';

/**
 * Names the tenant a request is for, when the deployment has more than one.
 *
 * **The header is a selector, not a credential.** The server must resolve the tenant from
 * the authenticated principal and treat this only as "which of the tenants you are already
 * entitled to did you mean". A server that trusted it would let any caller read any
 * tenant's registry by editing one header, which is the whole of the isolation model gone.
 * `ITenantContext` says as much: never return a tenant the caller has not been
 * authenticated for.
 *
 * In the self-hosted profile `config.tenant` is null and this interceptor adds nothing —
 * the server binds `TenantId.Default` for every operation, so there is nothing to say.
 * Sending a guessed value would be strictly worse than sending none: it would be a claim
 * the client is not entitled to make, and it would silently start failing the day the
 * server begins checking it.
 */
export const tenantInterceptor: HttpInterceptorFn = (req, next) => {
  const config = inject(CONCORDAT_CONFIG);
  const documentBase = inject(DOCUMENT).baseURI;

  if (config.tenant === null || !isApiUrl(req.url, config, documentBase)) {
    return next(req);
  }

  return next(req.clone({ setHeaders: { [TENANT_HEADER]: config.tenant } }));
};
