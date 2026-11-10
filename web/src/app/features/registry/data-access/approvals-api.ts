import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { map, type Observable } from 'rxjs';
import { CONCORDAT_CONFIG } from '../../../core/config/app-config';
import { environmentRoot } from '../../../core/http/api-url';
import type { ApprovalQueue } from '../../../domain/registry/approval-queue';
import { toApprovalQueue, type ApprovalQueueDto } from './approvals-dtos';

/**
 * The registry's approval queue for one environment (ADR-042).
 *
 * It replaced a queue the browser built by fetching every subject with every version and keeping
 * the ones held, which is right only while the subject list is one page long. The registry
 * answers one page, oldest proposal first, with the length of the whole queue beside it, so the
 * screen can say how much it is not showing.
 */
@Injectable({ providedIn: 'root' })
export class ApprovalsApi {
  private readonly http = inject(HttpClient);
  private readonly config = inject(CONCORDAT_CONFIG);

  /**
   * One page of the queue. Every parameter is optional; the registry clamps an out-of-range page
   * rather than refusing it, so nothing is validated here.
   *
   * With a `subject`, only that subject's held versions, and the registry's own `total` counts only
   * those (ADR-049): the filter is an exact name, not a prefix, and it is the registry's, so the page
   * that says "3 waiting" for one subject is not counting the rest of the queue. The name is sent as
   * given and encoded by the HTTP client, so a dot or any other character in it reaches the registry
   * as itself.
   */
  getQueue(
    environment: string,
    page: {
      readonly limit?: number;
      readonly offset?: number;
      readonly subject?: string | null;
    } = {},
  ): Observable<ApprovalQueue> {
    const params: Record<string, string | number> = {};

    if (page.subject) {
      params['subject'] = page.subject;
    }

    if (page.limit !== undefined) {
      params['limit'] = page.limit;
    }

    if (page.offset !== undefined) {
      params['offset'] = page.offset;
    }

    return this.http
      .get<ApprovalQueueDto>(`${environmentRoot(this.config, environment)}/approvals`, { params })
      .pipe(map(toApprovalQueue));
  }
}
