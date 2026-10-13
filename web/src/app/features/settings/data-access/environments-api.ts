import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { map, type Observable } from 'rxjs';
import { CONCORDAT_CONFIG } from '../../../core/config/app-config';
import { apiRoot } from '../../../core/http/api-url';
import type { Environment, EnvironmentChanges } from '../../../domain/environments/environment';
import { toEnvironment, type EnvironmentDto, type UpdateEnvironmentDto } from './environment-dtos';

/**
 * The registry's environment endpoints, and the brokers inside them.
 *
 * <b>Every method answers with the whole environment as it now stands</b>, brokers included,
 * and returns that. The registry does it that way so a caller replaces its copy rather than
 * guessing what a change touched — a credential set, for one, changes `hasCredentials` and
 * nothing else a client could have predicted.
 *
 * <b>Environments are addressed here by the name in the path, not through
 * `environmentRoot`:</b> that helper builds `/v1/environments/{env}/…` for resources *inside*
 * an environment, and these are the environment itself.
 */
@Injectable({ providedIn: 'root' })
export class EnvironmentsApi {
  private readonly http = inject(HttpClient);
  private readonly config = inject(CONCORDAT_CONFIG);

  getEnvironment(name: string): Observable<Environment> {
    return this.http.get<EnvironmentDto>(this.url(name)).pipe(map(toEnvironment));
  }

  updateEnvironment(name: string, changes: EnvironmentChanges): Observable<Environment> {
    const body: UpdateEnvironmentDto = { ...changes };

    return this.http.patch<EnvironmentDto>(this.url(name), body).pipe(map(toEnvironment));
  }

  addBroker(
    name: string,
    broker: { displayName: string; uri: string; virtualHost: string },
  ): Observable<Environment> {
    return this.http
      .post<EnvironmentDto>(`${this.url(name)}/brokers`, {
        displayName: broker.displayName,
        uri: broker.uri,
        // Left off rather than sent blank: the registry's default is `/`, and an empty string is
        // a virtual host named nothing.
        ...(broker.virtualHost.trim() === '' ? {} : { virtualHost: broker.virtualHost.trim() }),
      })
      .pipe(map(toEnvironment));
  }

  /** Runs a real handshake against the broker and records the result. Never an error for a dead broker. */
  checkBroker(name: string, brokerId: string): Observable<Environment> {
    return this.http
      .post<EnvironmentDto>(`${this.url(name)}/brokers/${encodeURIComponent(brokerId)}/check`, {})
      .pipe(map(toEnvironment));
  }

  /** Write-only (ADR-012): there is no call that returns a credential. */
  setBrokerCredential(
    name: string,
    brokerId: string,
    credential: { username: string; password: string },
  ): Observable<Environment> {
    return this.http
      .put<EnvironmentDto>(
        `${this.url(name)}/brokers/${encodeURIComponent(brokerId)}/credentials`,
        credential,
      )
      .pipe(map(toEnvironment));
  }

  removeBrokerCredential(name: string, brokerId: string): Observable<Environment> {
    return this.http
      .delete<EnvironmentDto>(
        `${this.url(name)}/brokers/${encodeURIComponent(brokerId)}/credentials`,
      )
      .pipe(map(toEnvironment));
  }

  removeBroker(name: string, brokerId: string): Observable<Environment> {
    return this.http
      .delete<EnvironmentDto>(`${this.url(name)}/brokers/${encodeURIComponent(brokerId)}`)
      .pipe(map(toEnvironment));
  }

  private url(name: string): string {
    return `${apiRoot(this.config)}/environments/${encodeURIComponent(name)}`;
  }
}
