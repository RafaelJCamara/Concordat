import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { map, type Observable } from 'rxjs';
import { CONCORDAT_CONFIG } from '../../../core/config/app-config';
import { environmentRoot } from '../../../core/http/api-url';
import type { RegistrationOutcome } from '../../../domain/registry/registration';
import type { Subject } from '../../../domain/registry/subject';
import {
  toRegistrationOutcome,
  toSubject,
  type RegisterVersionDto,
  type RegistrationDto,
  type SubjectDto,
} from './subject-dtos';

/**
 * The registry's subject endpoints.
 *
 * **The only place in the registry feature that touches `HttpClient`** (DESIGN §9). The
 * prototype had two competing HTTP paths — a hardcoded absolute `fetch` and an `axios.post`
 * to a different, unproxied path that always 404'd and swallowed the create silently. One
 * typed entry point per resource is the fix, and it only stays fixed if the ESLint
 * boundaries rule keeps `HttpClient` out of everything else.
 *
 * Every method returns domain types, never DTOs. A caller that receives a `SubjectDto` will
 * eventually reach into a wire field, and then the mapping is no longer at the boundary.
 */
@Injectable({ providedIn: 'root' })
export class SubjectsApi {
  private readonly http = inject(HttpClient);
  private readonly config = inject(CONCORDAT_CONFIG);

  /** Every subject in an environment, retired ones included. */
  listSubjects(environment: string): Observable<readonly Subject[]> {
    return this.http
      .get<readonly SubjectDto[]>(`${this.root(environment)}/subjects`)
      .pipe(map((dtos) => dtos.map(toSubject)));
  }

  /** One subject and all of its versions. */
  getSubject(environment: string, name: string): Observable<Subject> {
    return this.http
      .get<SubjectDto>(`${this.root(environment)}/subjects/${encodeURIComponent(name)}`)
      .pipe(map(toSubject));
  }

  /**
   * Registers a version against an existing subject.
   *
   * <b>A breaking change is a success here, not an error.</b> The API answers 201 with
   * `status: AWAITING_APPROVAL` and leaves `latest` unmoved (ADR-017), so nothing on this
   * path should treat divergences as a failure — the reason the endpoint is shaped that way
   * is that CI must never wedge on a proposal a human has not looked at yet. What *is* an
   * error arrives through the interceptor as a `ConcordatError`, as everywhere else.
   *
   * `registeredBy` is deliberately not sent. The API attributes the write to the caller's own
   * identity (M8.2), and a client-supplied name would be a second, unverified answer to a
   * question the server has already answered — the field exists for the CLI, which may be
   * running as a shared CI key and has a `--registered-by` flag for exactly that.
   */
  registerVersion(
    environment: string,
    subject: string,
    request: { schema: string; semanticVersion: string | null; changelog: string | null },
  ): Observable<RegistrationOutcome> {
    const body: RegisterVersionDto = {
      schema: request.schema,
      semanticVersion: request.semanticVersion,
      changelog: request.changelog,
    };

    return this.http
      .post<RegistrationDto>(
        `${this.root(environment)}/subjects/${encodeURIComponent(subject)}/versions`,
        body,
      )
      .pipe(map(toRegistrationOutcome));
  }

  private root(environment: string): string {
    return environmentRoot(this.config, environment);
  }
}
