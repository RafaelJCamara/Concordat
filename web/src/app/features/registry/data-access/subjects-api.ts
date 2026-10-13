import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { map, type Observable } from 'rxjs';
import { CONCORDAT_CONFIG } from '../../../core/config/app-config';
import { environmentRoot } from '../../../core/http/api-url';
import type { VersionDiff } from '../../../domain/registry/diff';
import type { NewSubject } from '../../../domain/registry/new-subject';
import type { RegistrationOutcome } from '../../../domain/registry/registration';
import type { Subject } from '../../../domain/registry/subject';
import {
  toCreateSubjectDto,
  toRegistrationOutcome,
  toSubject,
  toVersionDiff,
  type DiffDto,
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

  /**
   * Creates a subject, and answers with the subject as the registry stored it.
   *
   * <b>What comes back is what counts.</b> The registry trims the name and the owner before it
   * judges them, so the stored name can differ from the one sent; a caller that navigates to what
   * it sent instead of what it was given would land on a 404 whenever the two differ. The answer
   * is the only thing that is mapped and handed on.
   *
   * `owner` is sent because the wire type requires it, and the registry stores what it is given
   * (a create as one account with `owner: probe` is owned by `probe`): it is a property of the
   * subject, a team as often as a person, and not an attribution of the write.
   */
  createSubject(environment: string, subject: NewSubject): Observable<Subject> {
    return this.http
      .post<SubjectDto>(`${this.root(environment)}/subjects`, toCreateSubjectDto(subject))
      .pipe(map(toSubject));
  }

  /** One subject and all of its versions. */
  getSubject(environment: string, name: string): Observable<Subject> {
    return this.http
      .get<SubjectDto>(`${this.root(environment)}/subjects/${encodeURIComponent(name)}`)
      .pipe(map(toSubject));
  }

  /**
   * Compares two versions of one subject.
   *
   * **Every difference, not only the ones the policy forbids** — the endpoint reports both
   * directions and leaves the verdict to the reader, because a diff filtered by policy would
   * hide changes a consumer generating code from the schema cares about. `from` is the
   * baseline and `to` is the schema checked against it, so a divergence reads as "what `to`
   * does that `from` did not".
   */
  diffVersions(
    environment: string,
    subject: string,
    from: number,
    to: number,
  ): Observable<VersionDiff> {
    return this.http
      .get<DiffDto>(
        `${this.root(environment)}/subjects/${encodeURIComponent(subject)}/versions/${from}/diff/${to}`,
      )
      .pipe(map(toVersionDiff));
  }

  /**
   * Approves or rejects a version held at the gate. Answers with the subject as it stands
   * afterwards, so a caller can replace its copy rather than guess what changed.
   *
   * `decidedBy` is sent because the wire type requires it, and it is not what the registry
   * records: the audit trail and the version's own history carry the authenticated caller
   * (M8.2). Sending the session's own actor keeps the two agreeing instead of putting a
   * second, unverified answer to a question the server has already settled.
   */
  decideVersion(
    environment: string,
    subject: string,
    ordinal: number,
    decision: 'approve' | 'reject',
    decidedBy: string,
  ): Observable<Subject> {
    return this.http
      .post<SubjectDto>(
        `${this.root(environment)}/subjects/${encodeURIComponent(subject)}/versions/${ordinal}/${decision}`,
        { decidedBy },
      )
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
