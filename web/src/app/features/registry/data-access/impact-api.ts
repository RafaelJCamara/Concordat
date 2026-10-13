import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { map, type Observable } from 'rxjs';
import { CONCORDAT_CONFIG } from '../../../core/config/app-config';
import { environmentRoot } from '../../../core/http/api-url';
import type { ImpactReport } from '../../../domain/registry/impact';
import { toImpactReport, type ImpactDto } from './impact-dtos';

/**
 * The registry's impact endpoint — "who breaks if this subject changes".
 *
 * Only the registered-version form, `GET …/impact`. The other form, `POST …/impact`, takes a
 * schema that is not registered yet and is what CI calls before pushing; a screen has no
 * unregistered schema to ask about, so it is not here until one does.
 */
@Injectable({ providedIn: 'root' })
export class ImpactApi {
  private readonly http = inject(HttpClient);
  private readonly config = inject(CONCORDAT_CONFIG);

  /**
   * The impact of a registered version.
   *
   * <b>`null` omits the parameter rather than sending `latest`.</b> The endpoint's own
   * default is the subject's latest, and the report says which ordinal it resolved to, so
   * the screen does not have to know.
   */
  getImpact(
    environment: string,
    subject: string,
    version: number | null,
  ): Observable<ImpactReport> {
    const params = version === null ? undefined : new HttpParams().set('version', version);

    return this.http
      .get<ImpactDto>(
        `${environmentRoot(this.config, environment)}/subjects/${encodeURIComponent(subject)}/impact`,
        { params },
      )
      .pipe(map(toImpactReport));
  }
}
