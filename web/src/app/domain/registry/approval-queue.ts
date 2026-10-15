/** One version waiting on an approval decision, and what approving it would replace. */
export interface PendingApproval {
  readonly subject: string;
  readonly ordinal: number;
  readonly semanticVersion: string | null;
  readonly changelog: string | null;
  readonly registeredAt: Date;
  readonly registeredBy: string;
  /**
   * The ordinal consumers resolve as latest today, or null when nothing has been approved.
   *
   * An ordinal the registry chose, never "the highest one": a held version has a higher ordinal
   * than the version it would replace, and deriving the baseline here would reintroduce the
   * defect the gate exists to prevent (see `Subject.latest`).
   */
  readonly baseline: number | null;
  /** Who the subject names to decide this, or empty for any administrator (ADR-043). */
  readonly reviewers: readonly string[];
  /**
   * Whether the signed-in caller satisfies the subject's reviewer rule, as the registry judged it.
   *
   * It does not say the caller holds the scope to decide: that is the screen's own gate, and a reader
   * is told `true` for a subject that names nobody. Both must hold for a decision to be offered.
   */
  readonly mayDecide: boolean;
}

/** A page of the approval queue, oldest proposal first, and how long the whole queue is. */
export interface ApprovalQueue {
  /** Every version awaiting a decision in the environment, not only the ones in `items`. */
  readonly total: number;
  readonly items: readonly PendingApproval[];
}
