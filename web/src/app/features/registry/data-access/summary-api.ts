import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { map, type Observable } from 'rxjs';
import { CONCORDAT_CONFIG } from '../../../core/config/app-config';
import { environmentRoot } from '../../../core/http/api-url';
import type { EnvironmentSummary } from '../../../domain/registry/environment-summary';
import { toEnvironmentSummary, type EnvironmentSummaryDto } from './summary-dtos';

/**
 * The registry's overview of one environment (ADR-037).
 *
 * One request that answers what the dashboard shows. It replaced a derivation that fetched every
 * subject, with every version, to count them in the browser, and stopped being true the day that
 * list was paginated.
 */
@Injectable({ providedIn: 'root' })
export class SummaryApi {
  private readonly http = inject(HttpClient);
  private readonly config = inject(CONCORDAT_CONFIG);

  /** The overview of an environment. An environment nothing was written to answers zeros. */
  getSummary(environment: string): Observable<EnvironmentSummary> {
    return this.http
      .get<EnvironmentSummaryDto>(`${environmentRoot(this.config, environment)}/summary`)
      .pipe(map(toEnvironmentSummary));
  }
}
