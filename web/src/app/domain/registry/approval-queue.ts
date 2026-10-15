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
}

/** A page of the approval queue, oldest proposal first, and how long the whole queue is. */
export interface ApprovalQueue {
  /** Every version awaiting a decision in the environment, not only the ones in `items`. */
  readonly total: number;
  readonly items: readonly PendingApproval[];
}
