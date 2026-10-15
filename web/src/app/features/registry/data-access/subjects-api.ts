import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { map, type Observable } from 'rxjs';
import { CONCORDAT_CONFIG } from '../../../core/config/app-config';
import { environmentRoot } from '../../../core/http/api-url';
import type { CompatibilityPolicy } from '../../../domain/registry/compatibility';
import type { VersionDiff } from '../../../domain/registry/diff';
import type { NewSubject } from '../../../domain/registry/new-subject';
import type { RegistrationOutcome } from '../../../domain/registry/registration';
import type { PurgedSubject, Subject, SubjectPage } from '../../../domain/registry/subject';
import {
  toCreateSubjectDto,
  toPolicy,
  toRegistrationOutcome,
  toSubject,
  toVersionDiff,
  type ChangeOwnerDto,
  type DiffDto,
  type PolicyDto,
  type RegisterVersionDto,
  type RegistrationDto,
  type SetPolicyDto,
  type SetReviewersDto,
  type SubjectDto,
} from './subject-dtos';

/** What to list: a prefix, and one page of the answer (all optional). */
export interface SubjectListOptions {
  readonly prefix?: string;
  readonly limit?: number;
  readonly offset?: number;
}

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

  /**
   * The subjects in an environment, retired ones included, as a page and the length of the whole list.
   *
   * With a `prefix`, only those whose name begins with it, and **the registry does the filtering**
   * (ADR-038): ordinal and case-sensitive, because names are, and a literal, so `_` matches only an
   * underscore. A caller that filtered this list itself would be wrong the moment it is paged, and would
   * be answering a different question than the registry (the box used to match anywhere in a name,
   * ignoring case).
   *
   * With a `limit` and/or an `offset`, one page of the list in name order (ADR-045); without either, every
   * subject, as before. The registry sends how many match in `X-Total-Count`, which is what `total` is: a
   * registry that predates the header answers every subject and no count, and `total` is then the length
   * of the list it sent.
   *
   * The prefix is sent as given. The registry trims it and answers an empty list for text no name could
   * start with, so nothing is validated here; an empty or absent prefix sends no parameter at all, which
   * is the request this method has always made.
   */
  listSubjects(environment: string, options: SubjectListOptions = {}): Observable<SubjectPage> {
    const params: Record<string, string | number> = {};

    if (options.prefix) {
      params['prefix'] = options.prefix;
    }

    if (options.limit !== undefined) {
      params['limit'] = options.limit;
    }

    if (options.offset !== undefined) {
      params['offset'] = options.offset;
    }

    return this.http
      .get<readonly SubjectDto[]>(`${this.root(environment)}/subjects`, {
        params,
        observe: 'response',
      })
      .pipe(
        map((response) => {
          const items = (response.body ?? []).map(toSubject);
          const header = Number.parseInt(response.headers.get('X-Total-Count') ?? '', 10);

          return { items, total: Number.isFinite(header) ? header : items.length };
        }),
      );
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

  /**
   * Changes a subject's owner, and answers with the subject as the registry holds it afterwards.
   *
   * The body is the owner and nothing else (`ChangeOwnerDto`). The registry trims it, so what
   * comes back can differ from what was sent, and the caller shows what comes back. The writer is
   * not sent: the audit entry carries the authenticated caller (M8.2), and the owner is a property
   * of the subject — a team as often as a person — and not an attribution of this change.
   */
  changeOwner(environment: string, name: string, owner: string): Observable<Subject> {
    const body: ChangeOwnerDto = { owner };

    return this.http
      .patch<SubjectDto>(this.subjectUrl(environment, name), body)
      .pipe(map(toSubject));
  }

  /**
   * Sets a subject's compatibility policy, or clears it so the subject inherits.
   *
   * <b>Answers with the policy and not the subject.</b> `PUT` returns a `PolicyResponse`, so a
   * caller that wants the subject asks for it again; nothing here pretends the answer is more than
   * it is. Both members null clears it.
   */
  setPolicy(
    environment: string,
    name: string,
    policy: CompatibilityPolicy,
  ): Observable<CompatibilityPolicy> {
    const body: SetPolicyDto = { mode: policy.mode, surface: policy.surface };

    return this.http
      .put<PolicyDto>(`${this.subjectUrl(environment, name)}/compatibility-policy`, body)
      .pipe(map(toPolicy));
  }

  /**
   * Names who may approve or reject a subject's held versions, or clears the set (ADR-043).
   *
   * <b>Answers with the subject</b>, as the registry holds it afterwards: the reviewers as stored (a
   * repeat dropped, the first spelling kept), which can differ from what was sent. An empty list
   * clears the set, and the body always carries the member: the registry refuses one that omits it.
   */
  setReviewers(
    environment: string,
    name: string,
    reviewers: readonly string[],
  ): Observable<Subject> {
    const body: SetReviewersDto = { reviewers };

    return this.http
      .put<SubjectDto>(`${this.subjectUrl(environment, name)}/reviewers`, body)
      .pipe(map(toSubject));
  }

  /**
   * Retires a subject: terminal, and it keeps its name and every version it has.
   *
   * Answers with the subject as the registry holds it afterwards, which is how the caller learns it
   * is retired rather than assuming so. `subject:admin`.
   */
  retire(environment: string, name: string): Observable<Subject> {
    return this.http.delete<SubjectDto>(this.subjectUrl(environment, name)).pipe(map(toSubject));
  }

  /**
   * Deletes a retired subject, and every version it holds, for good (ADR-046).
   *
   * <b>`force=true` is sent on every call and is not an option</b>: the registry refuses without it, and
   * a screen that offered a way to leave it out would be offering a request that cannot succeed. The
   * answer is how many versions went, as the registry counted them.
   */
  purge(environment: string, name: string): Observable<PurgedSubject> {
    return this.http.post<PurgedSubject>(`${this.subjectUrl(environment, name)}/purge`, null, {
      params: { force: 'true' },
    });
  }

  /** One subject and all of its versions. */
  getSubject(environment: string, name: string): Observable<Subject> {
    return this.http.get<SubjectDto>(this.subjectUrl(environment, name)).pipe(map(toSubject));
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

  /** A subject's own address. The name is percent-encoded: it is user-chosen, and could name another route. */
  private subjectUrl(environment: string, name: string): string {
    return `${this.root(environment)}/subjects/${encodeURIComponent(name)}`;
  }

  private root(environment: string): string {
    return environmentRoot(this.config, environment);
  }
}
