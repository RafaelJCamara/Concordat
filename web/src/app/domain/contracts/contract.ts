// Contracts: which subjects may travel on which routes, and how strictly that is held to.

/**
 * How much a contract may do.
 *
 * `OFF` governs nothing, `MONITOR` reports a violation and lets the message through, and
 * `ENFORCE` refuses it. A new contract starts at `MONITOR`, never `ENFORCE`: a contract is
 * authored by guessing about a live topology, and one that blocked the moment it was written
 * would be discovered in production.
 */
export const ENFORCEMENT_MODES = ['OFF', 'MONITOR', 'ENFORCE'] as const;

/** An enforcement mode, spelled as the API spells it. */
export type EnforcementMode = (typeof ENFORCEMENT_MODES)[number];

/**
 * A subject a binding carries, and which of its versions apply.
 *
 * The selector is `latest`, an ordinal, or `>=N`. It is kept as the text the registry sent
 * rather than parsed into a union: the registry owns the grammar, and a build that parsed it
 * would need a release the day the grammar grew.
 */
export interface SubjectRef {
  readonly subject: string;
  readonly selector: string;
}

/** What a publisher on an exchange may send. */
export interface PublishBinding {
  readonly exchange: string;
  /** An AMQP topic pattern. `*` is one word and `#` is any number of them. */
  readonly routingKeyPattern: string;
  /** The broker it applies to, or null for every broker in the environment. */
  readonly brokerId: string | null;
  readonly virtualHost: string;
  /** Which binding wins when two overlap, or null when none was declared. */
  readonly precedence: number | null;
  readonly subjects: readonly SubjectRef[];
}

/** What a queue is expected to receive. */
export interface ConsumeBinding {
  readonly queue: string;
  readonly brokerId: string | null;
  readonly virtualHost: string;
  readonly subjects: readonly SubjectRef[];
}

/** A contract and its bindings. */
export interface Contract {
  readonly name: string;
  readonly enforcement: EnforcementMode;
  readonly createdAt: Date;
  readonly publishes: readonly PublishBinding[];
  readonly consumes: readonly ConsumeBinding[];
}

/** The selector a subject reference gets when its author names none. */
export const DEFAULT_SELECTOR = 'latest';

/**
 * Reads subject references as people type them: `orders.created`, `orders.created@2` or
 * `orders.created@>=2`, separated by commas, spaces or new lines.
 *
 * <b>Only the shape is checked here.</b> Whether the subject exists and whether the selector
 * is one the registry accepts is the registry's to say, with its own reason, and a second copy
 * of that grammar in the browser would be one more place for the two to disagree. What this
 * does refuse is the case where there is nothing to send, and the case where `@` is present
 * with nothing after it — which would otherwise silently become `latest`, a version choice the
 * author never made.
 *
 * @returns The references, or `null` when the text names none or one of them is malformed.
 */
export function parseSubjectRefs(text: string): readonly SubjectRef[] | null {
  const parts = text.split(/[\s,]+/).filter((part) => part !== '');

  if (parts.length === 0) {
    return null;
  }

  const refs: SubjectRef[] = [];

  for (const part of parts) {
    const at = part.indexOf('@');

    if (at === -1) {
      refs.push({ subject: part, selector: DEFAULT_SELECTOR });
      continue;
    }

    const subject = part.slice(0, at);
    const selector = part.slice(at + 1);

    if (subject === '' || selector === '') {
      return null;
    }

    refs.push({ subject, selector });
  }

  return refs;
}
