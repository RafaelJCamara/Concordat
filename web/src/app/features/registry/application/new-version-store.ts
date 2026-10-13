import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { catchError, exhaustMap, of, pipe, switchMap, tap } from 'rxjs';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import { toConcordatError, type ConcordatError } from '../../../core/http/problem-details';
import type { RegistrationOutcome } from '../../../domain/registry/registration';
import {
  highestLiveLabel,
  suggestLabels,
  type SemanticVersion,
} from '../../../domain/registry/semantic-version';
import type { Subject } from '../../../domain/registry/subject';
import { SchemasApi } from '../data-access/schemas-api';
import { SubjectsApi } from '../data-access/subjects-api';

/** What the form submits. */
export interface RegistrationRequest {
  readonly subject: string;
  readonly schema: string;
  readonly semanticVersion: string | null;
  readonly changelog: string | null;
}

/**
 * An earlier version's document, fetched to start the next one from.
 *
 * `nonce` is what makes loading the same version twice observable. Without it a second
 * "load v2" after the editor was changed would leave this signal reading equal to its last
 * value, and nothing downstream would re-apply it.
 */
export interface ClonedDocument {
  readonly ordinal: number;
  readonly text: string;
  readonly nonce: number;
}

interface NewVersionState {
  /** The subject being registered against, for the form's own context. */
  readonly subject: Subject | null;
  readonly loading: boolean;
  readonly submitting: boolean;
  readonly outcome: RegistrationOutcome | null;
  readonly error: ConcordatError | null;
  /** Whether an earlier version's document is being fetched. */
  readonly cloning: boolean;
  readonly cloned: ClonedDocument | null;
  /** Why an earlier version's document could not be fetched, kept apart from a refusal. */
  readonly cloneError: ConcordatError | null;
}

const INITIAL: NewVersionState = {
  subject: null,
  loading: false,
  submitting: false,
  outcome: null,
  error: null,
  cloning: false,
  cloned: null,
  cloneError: null,
};

/**
 * The registration form's state.
 *
 * <b>`exhaustMap`, not `switchMap`.</b> Every other store here switches, because a second
 * read supersedes the first. A second *write* does not: cancelling an in-flight POST
 * abandons the browser's side of a request the registry is still going to process, so a
 * double-clicked submit could allocate two ordinals while the UI showed one. `exhaustMap`
 * ignores the second click until the first has answered, which is the behaviour a submit
 * button needs and the opposite of the one a search box needs.
 */
export const NewVersionStore = signalStore(
  withState<NewVersionState>(INITIAL),
  withComputed(({ subject, outcome }) => ({
    /**
     * The ordinal this registration would take, for the form to show before submitting.
     *
     * Derived from the highest ordinal present rather than from `latest`, and that is right
     * here where it would be wrong elsewhere: this is "what number comes next", not "what is
     * current" — a version awaiting approval has already taken its ordinal, so the next one
     * has to clear it.
     */
    nextOrdinal: computed(() => {
      const value = subject();

      if (value === null) {
        return null;
      }

      return value.versions.reduce((highest, v) => Math.max(highest, v.ordinal), 0) + 1;
    }),

    /** Whether the registry accepted the schema but held it at the approval gate. */
    held: computed(() => outcome()?.status === 'AWAITING_APPROVAL'),

    /**
     * The subject's format, which decides how the editor reads the text.
     *
     * Null until the subject has loaded — and the editor says nothing about a document it
     * cannot yet classify, because telling somebody a Protobuf file is not JSON is the one
     * mistake that guess would make.
     */
    format: computed(() => subject()?.format ?? null),

    /** Every version, newest first, for the "start from" list. */
    startingPoints: computed(() =>
      [...(subject()?.versions ?? [])].sort((a, b) => b.ordinal - a.ordinal),
    ),

    /**
     * The version the editor offers to start from by default: the one consumers resolve.
     *
     * `latest` here rather than the highest ordinal. A held version has the higher ordinal
     * and is a proposal; the document people iterate on is the contract in force. It stays
     * one click away in the list for the person who is revising their own held proposal.
     */
    defaultStartingOrdinal: computed(() => {
      const value = subject();

      if (value === null || value.versions.length === 0) {
        return null;
      }

      return value.latest ?? value.versions.reduce((highest, v) => Math.max(highest, v.ordinal), 0);
    }),

    /**
     * The label a new one has to increase on, as the registry reads it.
     *
     * Not the label on `latest` — see `highestLiveLabel` for the version it is not.
     */
    liveLabel: computed((): SemanticVersion | null => {
      const value = subject();

      return value === null ? null : highestLiveLabel(value.versions);
    }),

    /** What each bump from the live label would produce, or null when there is none. */
    labelSuggestions: computed(() => {
      const value = subject();
      const label = value === null ? null : highestLiveLabel(value.versions);

      return label === null ? null : suggestLabels(label);
    }),
  })),
  withMethods((store) => {
    const api = inject(SubjectsApi);
    const schemas = inject(SchemasApi);
    const environments = inject(ActiveEnvironmentStore);

    /**
     * Reads the subject again after a registration, so the next ordinal and the label
     * suggestions come from the registry's answer rather than from arithmetic on the last one.
     *
     * Quiet on failure, deliberately: the registration already succeeded and the screen
     * already says so, so a failed refresh must not put a refusal banner on the form the
     * author comes back to. The stale subject only makes a suggestion stale, and a stale
     * suggestion is something the registry corrects when it is submitted.
     */
    const refreshSubject = rxMethod<string>(
      pipe(
        switchMap((name) =>
          api.getSubject(environments.name(), name).pipe(
            tap((subject) => patchState(store, { subject })),
            catchError(() => of(null)),
          ),
        ),
      ),
    );

    return {
      /** Loads the subject the form registers against, for its heading and next ordinal. */
      loadSubject: rxMethod<string>(
        pipe(
          tap(() => patchState(store, { loading: true, error: null })),
          exhaustMap((name) =>
            api.getSubject(environments.name(), name).pipe(
              tap((subject) => patchState(store, { subject, loading: false })),
              catchError((error: unknown) => {
                patchState(store, { loading: false, error: toConcordatError(error) });
                return of(null);
              }),
            ),
          ),
        ),
      ),

      submit: rxMethod<RegistrationRequest>(
        pipe(
          tap(() => patchState(store, { submitting: true, error: null, outcome: null })),
          exhaustMap((request) =>
            api
              .registerVersion(environments.name(), request.subject, {
                schema: request.schema,
                semanticVersion: request.semanticVersion,
                changelog: request.changelog,
              })
              .pipe(
                // A breaking change lands here, not in `catchError`: the registry accepted
                // it and held it at the gate, which is a success with a consequence.
                tap((outcome) => {
                  patchState(store, { outcome, submitting: false });

                  if (outcome.created) {
                    refreshSubject(request.subject);
                  }
                }),
                catchError((error: unknown) => {
                  patchState(store, { submitting: false, error: toConcordatError(error) });
                  return of(null);
                }),
              ),
          ),
        ),
      ),

      /**
       * Fetches an earlier version's document, as registered, to start the next one from.
       *
       * <b>The registered text, not its canonical form</b> — `SchemaDocument.text` is what
       * the author sent, and canonical text is a compact, key-sorted rendering of it that
       * nobody wants to edit. `switchMap`: this is a read, so a second choice supersedes the
       * first rather than queuing behind it.
       */
      cloneFrom: rxMethod<number>(
        pipe(
          tap(() => patchState(store, { cloning: true, cloneError: null })),
          switchMap((ordinal) => {
            const version = store.subject()?.versions.find((v) => v.ordinal === ordinal);

            if (version === undefined) {
              patchState(store, { cloning: false });
              return of(null);
            }

            return schemas.getSchema(version.schemaId).pipe(
              tap((document) =>
                patchState(store, (state) => ({
                  cloning: false,
                  cloned: {
                    ordinal,
                    text: document.text,
                    nonce: (state.cloned?.nonce ?? 0) + 1,
                  },
                })),
              ),
              catchError((error: unknown) => {
                patchState(store, { cloning: false, cloneError: toConcordatError(error) });
                return of(null);
              }),
            );
          }),
        ),
      ),

      /** Clears a previous answer so the form can be edited and sent again. */
      reset(): void {
        patchState(store, { outcome: null, error: null });
      },
    };
  }),
);
