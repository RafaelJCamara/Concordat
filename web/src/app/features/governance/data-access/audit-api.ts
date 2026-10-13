import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { map, type Observable } from 'rxjs';
import { CONCORDAT_CONFIG } from '../../../core/config/app-config';
import { apiRoot } from '../../../core/http/api-url';
import type { AuditEntry, AuditQuery } from '../../../domain/governance/audit';
import { toAuditEntry, type AuditEntryDto } from './audit-dtos';

/**
 * The registry's audit trail.
 *
 * `GET /v1/audit` is **not** under an environment segment, unlike almost everything else: the
 * trail spans every resource type and every environment in the tenant, so the environment is a
 * filter here and not a path. Passing none asks for all of them.
 */
@Injectable({ providedIn: 'root' })
export class AuditApi {
  private readonly http = inject(HttpClient);
  private readonly config = inject(CONCORDAT_CONFIG);

  /**
   * The trail, newest first.
   *
   * @param environment The active environment, used only when the query scopes to it.
   */
  getAudit(environment: string, query: AuditQuery): Observable<readonly AuditEntry[]> {
    let params = new HttpParams().set('limit', query.limit);

    if (query.scope === 'environment') {
      params = params.set('env', environment);
    }

    if (query.action !== null) {
      params = params.set('action', query.action);
    }

    // Trimmed, and left off when blank: the server treats a blank filter as no filter, but a
    // `target=` on the wire makes a log line that looks like a filter somebody meant.
    const target = query.target.trim();

    if (target !== '') {
      params = params.set('target', target);
    }

    if (query.since !== null) {
      params = params.set('since', query.since.toISOString());
    }

    if (query.until !== null) {
      params = params.set('until', query.until.toISOString());
    }

    return this.http
      .get<readonly AuditEntryDto[]>(`${apiRoot(this.config)}/audit`, { params })
      .pipe(map((dtos) => dtos.map(toAuditEntry)));
  }
}
