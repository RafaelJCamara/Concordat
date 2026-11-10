/**
 * A line diff: which lines of one document were kept, removed and added to make another.
 *
 * <b>Written here and not pulled in</b> (ADR-050). This is Myers' O(ND) algorithm with the
 * common beginning and end of the two documents trimmed first, which is the whole of what a
 * pull-request view needs; `diff`/`jsdiff` is about 60 kB of which this is a tenth, and Monaco's
 * diff editor is a second copy of a 3 MB editor for a read-only list. Pure TypeScript, no
 * framework, because it has to run in a worker and in a plain Node test.
 *
 * <b>The result is a script, not a pair of ranges</b>: one {@link LineOp} per line of the merged
 * view, in order. That is the shape both consumers want — the view walks it once, and the
 * collapse decides from it which runs of {@link LineOp.Same} to hide.
 *
 * <b>It gives up rather than run long.</b> Myers is quadratic in the number of changed lines, so
 * two documents with nothing in common cost the most and teach the reader the least. Past
 * `maxChanges` the answer is `null`, and the caller says so; a truncated diff that looked whole
 * would be a lie about a security review.
 */

/** What happened to one line of the merged view. */
export const LineOp = {
  /** The line is in both documents. */
  Same: 0,
  /** The line is only in the first document. */
  Removed: 1,
  /** The line is only in the second document. */
  Added: 2,
} as const;

/** One of the {@link LineOp} values. */
export type LineOp = (typeof LineOp)[keyof typeof LineOp];

/**
 * Compares two lists of lines.
 *
 * Returns the merged view as one op per line (a `Same` or `Removed` consumes a line of `before`;
 * a `Same` or `Added` consumes a line of `after`), or `null` when more than `maxChanges` lines
 * would have to be added or removed.
 */
export function diffLines(
  before: readonly string[],
  after: readonly string[],
  maxChanges: number,
): Uint8Array | null {
  // Lines become small integers once, so the inner loop compares numbers and never strings.
  const ids = new Map<string, number>();
  const intern = (line: string): number => {
    let id = ids.get(line);

    if (id === undefined) {
      id = ids.size;
      ids.set(line, id);
    }

    return id;
  };
  const a = Int32Array.from(before, intern);
  const b = Int32Array.from(after, intern);

  // Most schema changes touch a few lines of a long document. Everything either side of them is
  // free, and trimming it is what keeps the quadratic part from seeing it.
  let head = 0;
  while (head < a.length && head < b.length && a[head] === b[head]) {
    head++;
  }

  let tail = 0;
  while (
    tail < a.length - head &&
    tail < b.length - head &&
    a[a.length - 1 - tail] === b[b.length - 1 - tail]
  ) {
    tail++;
  }

  const middle = shortestEdit(
    a.subarray(head, a.length - tail),
    b.subarray(head, b.length - tail),
    maxChanges,
  );

  if (middle === null) {
    return null;
  }

  const script = new Uint8Array(head + middle.length + tail);
  script.fill(LineOp.Same, 0, head);
  script.set(middle, head);
  script.fill(LineOp.Same, head + middle.length);

  return script;
}

/** Myers' algorithm over two non-trivially-different sequences; `null` past `maxChanges`. */
function shortestEdit(a: Int32Array, b: Int32Array, maxChanges: number): Uint8Array | null {
  const n = a.length;
  const m = b.length;

  if (n === 0 || m === 0) {
    // One side is empty, so every line of the other is a change and there is nothing to search.
    if (n + m > maxChanges) {
      return null;
    }

    return new Uint8Array(n + m).fill(n === 0 ? LineOp.Added : LineOp.Removed);
  }

  const limit = Math.min(n + m, maxChanges);
  const offset = limit + 1;
  // `furthest[k + offset]` is the furthest `x` reached on diagonal `k` (x - y = k).
  const furthest = new Int32Array(2 * limit + 3);
  const history: Int32Array[] = [];

  for (let d = 0; d <= limit; d++) {
    // The state before this round, which is what backtracking from round d needs.
    history.push(furthest.slice());

    for (let k = -d; k <= d; k += 2) {
      const down =
        k === -d || (k !== d && at(furthest, k - 1 + offset) < at(furthest, k + 1 + offset));
      let x = down ? at(furthest, k + 1 + offset) : at(furthest, k - 1 + offset) + 1;
      let y = x - k;

      while (x < n && y < m && a[x] === b[y]) {
        x++;
        y++;
      }

      furthest[k + offset] = x;

      if (x >= n && y >= m) {
        return backtrack(history, d, n, m, offset);
      }
    }
  }

  return null;
}

/** Reads an index that is in range by construction; `noUncheckedIndexedAccess` cannot see that. */
function at(values: Int32Array, index: number): number {
  return values[index] ?? 0;
}

/** Walks the saved rounds back from the end to the start, and returns the script forwards. */
function backtrack(
  history: readonly Int32Array[],
  distance: number,
  n: number,
  m: number,
  offset: number,
): Uint8Array {
  // Filled from the back, so no reversal is needed afterwards.
  const script = new Uint8Array(n + m);
  let cursor = script.length;
  let x = n;
  let y = m;

  for (let d = distance; d > 0; d--) {
    const before = history[d] as Int32Array;
    const k = x - y;
    const down = k === -d || (k !== d && at(before, k - 1 + offset) < at(before, k + 1 + offset));
    const previousK = down ? k + 1 : k - 1;
    const previousX = at(before, previousK + offset);
    const previousY = previousX - previousK;

    // The diagonal run that followed the edit.
    while (x > previousX && y > previousY) {
      script[--cursor] = LineOp.Same;
      x--;
      y--;
    }

    // The edit itself: down is an insertion from `b`, right is a deletion from `a`.
    script[--cursor] = down ? LineOp.Added : LineOp.Removed;

    if (down) {
      y--;
    } else {
      x--;
    }
  }

  // Round zero is only a diagonal run from the origin.
  while (x > 0 && y > 0) {
    script[--cursor] = LineOp.Same;
    x--;
    y--;
  }

  return script.subarray(cursor);
}
