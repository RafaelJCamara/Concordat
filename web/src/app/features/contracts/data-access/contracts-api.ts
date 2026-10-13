import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { map, type Observable } from 'rxjs';
import { CONCORDAT_CONFIG } from '../../../core/config/app-config';
import { environmentRoot } from '../../../core/http/api-url';
import type { Contract, EnforcementMode, SubjectRef } from '../../../domain/contracts/contract';
import {
  toContract,
  type AddConsumeBindingDto,
  type AddPublishBindingDto,
  type ContractDto,
} from './contract-dtos';

/** A publish binding as a person describes it. */
export interface NewPublishBinding {
  readonly exchange: string;
  readonly routingKeyPattern: string;
  readonly subjects: readonly SubjectRef[];
  /** Blank for the registry's default, `/`. */
  readonly virtualHost: string;
  readonly precedence: number | null;
}

/** A consume binding as a person describes it. */
export interface NewConsumeBinding {
  readonly queue: string;
  readonly subjects: readonly SubjectRef[];
  readonly virtualHost: string;
}

/**
 * The registry's contract endpoints.
 *
 * Every mutating method answers with the contract as it stands afterwards, and returns that —
 * so a caller replaces its copy rather than guessing what a binding changed.
 *
 * <b>No broker is named.</b> A binding without a `brokerId` applies to every broker in the
 * environment, which is the right default until there is a screen to choose one from; the
 * brokers page is a later part of M4.3 and this does not pretend otherwise.
 */
@Injectable({ providedIn: 'root' })
export class ContractsApi {
  private readonly http = inject(HttpClient);
  private readonly config = inject(CONCORDAT_CONFIG);

  listContracts(environment: string): Observable<readonly Contract[]> {
    return this.http
      .get<readonly ContractDto[]>(`${this.root(environment)}/contracts`)
      .pipe(map((dtos) => dtos.map(toContract)));
  }

  getContract(environment: string, name: string): Observable<Contract> {
    return this.http
      .get<ContractDto>(`${this.root(environment)}/contracts/${encodeURIComponent(name)}`)
      .pipe(map(toContract));
  }

  /** Creates a contract. Omitting the mode lets the registry choose `MONITOR`. */
  createContract(
    environment: string,
    name: string,
    enforcement: EnforcementMode | null,
  ): Observable<Contract> {
    return this.http
      .post<ContractDto>(`${this.root(environment)}/contracts`, {
        name,
        ...(enforcement === null ? {} : { enforcement }),
      })
      .pipe(map(toContract));
  }

  setEnforcement(
    environment: string,
    name: string,
    enforcement: EnforcementMode,
  ): Observable<Contract> {
    return this.http
      .put<ContractDto>(
        `${this.root(environment)}/contracts/${encodeURIComponent(name)}/enforcement`,
        { enforcement },
      )
      .pipe(map(toContract));
  }

  addPublishBinding(
    environment: string,
    name: string,
    binding: NewPublishBinding,
  ): Observable<Contract> {
    const body: AddPublishBindingDto = {
      exchange: binding.exchange,
      routingKeyPattern: binding.routingKeyPattern,
      subjects: binding.subjects,
      // Left off rather than sent blank: the registry's default is `/`, and an empty string is
      // a virtual host named nothing.
      ...(binding.virtualHost.trim() === '' ? {} : { virtualHost: binding.virtualHost.trim() }),
      ...(binding.precedence === null ? {} : { precedence: binding.precedence }),
    };

    return this.http
      .post<ContractDto>(
        `${this.root(environment)}/contracts/${encodeURIComponent(name)}/publishes`,
        body,
      )
      .pipe(map(toContract));
  }

  addConsumeBinding(
    environment: string,
    name: string,
    binding: NewConsumeBinding,
  ): Observable<Contract> {
    const body: AddConsumeBindingDto = {
      queue: binding.queue,
      subjects: binding.subjects,
      ...(binding.virtualHost.trim() === '' ? {} : { virtualHost: binding.virtualHost.trim() }),
    };

    return this.http
      .post<ContractDto>(
        `${this.root(environment)}/contracts/${encodeURIComponent(name)}/consumes`,
        body,
      )
      .pipe(map(toContract));
  }

  private root(environment: string): string {
    return environmentRoot(this.config, environment);
  }
}
