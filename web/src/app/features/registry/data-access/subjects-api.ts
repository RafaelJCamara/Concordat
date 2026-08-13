import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { map, type Observable } from 'rxjs';
import { CONCORDAT_CONFIG } from '../../../core/config/app-config';
import { environmentRoot } from '../../../core/http/api-url';
import type { Subject } from '../../../domain/registry/subject';
import { toSubject, type SubjectDto } from './subject-dtos';

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

  private root(environment: string): string {
    return environmentRoot(this.config, environment);
  }
}
