import { describe, expect, it } from 'vitest';
import { diffLines, LineOp } from './line-diff';

// The algorithm is checked by what it must always satisfy rather than by one answer: replaying
// the script over the first document gives the second, and it is as short as it can be. A
// hand-picked expected script would pass for any algorithm that happened to agree on the pick.

/** Replays a script: Same and Added take their line from `after`, Removed takes nothing. */
function replay(before: readonly string[], after: readonly string[], script: Uint8Array): string[] {
  const result: string[] = [];
  let b = 0;
  let a = 0;

  for (const op of script) {
    if (op === LineOp.Same) {
      expect(before[b]).toBe(after[a]);
      result.push(after[a] as string);
      b++;
      a++;
    } else if (op === LineOp.Removed) {
      b++;
    } else {
      result.push(after[a] as string);
      a++;
    }
  }

  expect(b).toBe(before.length);
  expect(a).toBe(after.length);

  return result;
}

const lines = (text: string): string[] => (text === '' ? [] : text.split(' '));

describe('diffLines', () => {
  it.each([
    ['identical', 'a b c', 'a b c', 0],
    ['a line added at the end', 'a b', 'a b c', 1],
    ['a line added at the start', 'b c', 'a b c', 1],
    ['a line removed from the middle', 'a b c', 'a c', 1],
    ['one line replaced', 'a b c', 'a x c', 2],
    ['nothing before', '', 'a b', 2],
    ['nothing after', 'a b', '', 2],
    ['nothing at all', '', '', 0],
    ['nothing in common', 'a b c', 'x y z', 6],
    ['a repeated line moved', 'a b a b', 'b a b a', 2],
    ['the classic Myers example', 'a b c a b b a', 'c b a b a c', 5],
  ])('%s: replays to the second document and is as short as it can be', (_n, x, y, changes) => {
    const before = lines(x);
    const after = lines(y);
    const script = diffLines(before, after, 100);

    expect(script).not.toBeNull();
    expect(replay(before, after, script as Uint8Array)).toEqual(after);
    expect((script as Uint8Array).filter((op) => op !== LineOp.Same).length).toBe(changes);
  });

  it('reports a removal as removed and an addition as added, not the other way round', () => {
    expect([...(diffLines(['a', 'b'], ['a'], 10) as Uint8Array)]).toEqual([
      LineOp.Same,
      LineOp.Removed,
    ]);
    expect([...(diffLines(['a'], ['a', 'b'], 10) as Uint8Array)]).toEqual([
      LineOp.Same,
      LineOp.Added,
    ]);
  });

  it('agrees with a brute-force longest common subsequence on random documents', () => {
    let seed = 12345;
    const next = (n: number): number => {
      seed = (seed * 1103515245 + 12345) & 0x7fffffff;
      return seed % n;
    };

    for (let round = 0; round < 200; round++) {
      const before = Array.from({ length: next(12) }, () => String(next(4)));
      const after = Array.from({ length: next(12) }, () => String(next(4)));
      const script = diffLines(before, after, 1000) as Uint8Array;

      expect(replay(before, after, script)).toEqual(after);
      expect(script.filter((op) => op === LineOp.Same).length).toBe(lcs(before, after));
    }
  });

  it('gives up with null once more lines changed than it was allowed', () => {
    const before = lines('a b c d e');
    const after = lines('v w x y z');

    expect(diffLines(before, after, 9)).toBeNull();
    expect(diffLines(before, after, 10)).not.toBeNull();
  });

  it('counts the limit in changed lines, so a long document with one edit never trips it', () => {
    const before = Array.from({ length: 50_000 }, (_, i) => `line ${i}`);
    const after = [...before];
    after[25_000] = 'edited';

    const script = diffLines(before, after, 2) as Uint8Array;

    expect(script).not.toBeNull();
    expect(script.filter((op) => op !== LineOp.Same).length).toBe(2);
  });

  it('applies the limit to a document that is empty on one side too', () => {
    expect(diffLines([], lines('a b c'), 2)).toBeNull();
    expect(diffLines(lines('a b c'), [], 3)).not.toBeNull();
  });
});

/** The length of the longest common subsequence, by the textbook table. */
function lcs(a: readonly string[], b: readonly string[]): number {
  const table = Array.from({ length: a.length + 1 }, () => new Array<number>(b.length + 1).fill(0));

  for (let i = 1; i <= a.length; i++) {
    for (let j = 1; j <= b.length; j++) {
      (table[i] as number[])[j] =
        a[i - 1] === b[j - 1]
          ? ((table[i - 1] as number[])[j - 1] as number) + 1
          : Math.max(
              (table[i - 1] as number[])[j] as number,
              (table[i] as number[])[j - 1] as number,
            );
    }
  }

  return (table[a.length] as number[])[b.length] as number;
}
