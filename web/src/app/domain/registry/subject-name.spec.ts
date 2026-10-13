import { describe, expect, it } from 'vitest';
import { isRegistryWhitespace, trimRegistryText } from './registry-text';
import {
  SUBJECT_NAME_MAX_LENGTH,
  checkSubjectName,
  slugSubjectName,
  type SubjectNameCheck,
} from './subject-name';

// Every verdict below was first sent to the real create endpoint. `e2e/subject-name-parity.spec.ts`
// holds the same table against it on every run; this file is the fast half and would catch a typo
// in a range or a changed limit without a registry.

const at = (...codes: number[]) => String.fromCharCode(...codes);

const verdict = (text: string): string => {
  const check = checkSubjectName(text);

  return check.ok ? `ok:${check.name}` : check.code;
};

describe('trimRegistryText', () => {
  // The registry trims with System.String.Trim(). Each row is a character it strips.
  it.each([
    [0x09, 'tab'],
    [0x0a, 'line feed'],
    [0x0b, 'vertical tab'],
    [0x0c, 'form feed'],
    [0x0d, 'carriage return'],
    [0x20, 'space'],
    [0x85, 'next line'],
    [0xa0, 'no-break space'],
    [0x1680, 'ogham space mark'],
    [0x2000, 'en quad'],
    [0x2005, 'four-per-em space'],
    [0x200a, 'hair space'],
    [0x2028, 'line separator'],
    [0x2029, 'paragraph separator'],
    [0x202f, 'narrow no-break space'],
    [0x205f, 'medium mathematical space'],
    [0x3000, 'ideographic space'],
  ])('strips code point %i (%s) from both ends', (code) => {
    expect(trimRegistryText(at(code) + 'name' + at(code))).toBe('name');
    expect(isRegistryWhitespace(code)).toBe(true);
  });

  // And these are the ones it does not, which are as much of the table as the first list.
  it.each([
    [0xfeff, 'byte-order mark, which String.prototype.trim() strips'],
    [0x180e, 'Mongolian vowel separator'],
    [0x200b, 'zero-width space'],
    [0x2060, 'word joiner'],
    [0x1c, 'file separator'],
    [0x1d, 'group separator'],
    [0x1e, 'record separator'],
    [0x1f, 'unit separator'],
    [0x00, 'NUL'],
  ])('does not strip code point %i (%s)', (code) => {
    expect(trimRegistryText(at(code) + 'name' + at(code))).toBe(at(code) + 'name' + at(code));
    expect(isRegistryWhitespace(code)).toBe(false);
  });

  it('is not String.prototype.trim(), in both directions', () => {
    const nextLine = at(0x85) + 'x';
    const bom = at(0xfeff) + 'x';

    // The stricter direction: JavaScript leaves this alone and the registry strips it.
    expect(nextLine.trim()).toBe(nextLine);
    expect(trimRegistryText(nextLine)).toBe('x');

    // The looser direction: JavaScript strips this and the registry keeps it.
    expect(bom.trim()).toBe('x');
    expect(trimRegistryText(bom)).toBe(bom);
  });

  it('leaves whitespace inside a value alone', () => {
    expect(trimRegistryText('  a b  ')).toBe('a b');
  });

  it('turns an all-whitespace value into the empty string', () => {
    expect(trimRegistryText(' \t\n' + at(0x3000))).toBe('');
  });
});

describe('checkSubjectName', () => {
  it.each([
    'a',
    'A',
    '_',
    '9',
    '9.9',
    'a.b',
    'a_b',
    'acme.orders.OrderCreated',
    'acme.e2e.parity.json',
    '_.a._',
    // Names a route or a verb would suggest are names like any other.
    'latest',
    'versions',
    'new',
    'acme.latest',
  ])('accepts %s', (name) => {
    expect(verdict(name)).toBe(`ok:${name}`);
  });

  it('accepts a name of exactly 512 characters and refuses one of 513', () => {
    expect(verdict('a'.repeat(SUBJECT_NAME_MAX_LENGTH))).toMatch(/^ok:/);
    expect(verdict('a'.repeat(SUBJECT_NAME_MAX_LENGTH + 1))).toBe('subject_name_invalid');
  });

  it('measures the length after trimming, as the registry does', () => {
    const padded = ' ' + 'a'.repeat(SUBJECT_NAME_MAX_LENGTH) + ' ';

    expect(padded.length).toBeGreaterThan(SUBJECT_NAME_MAX_LENGTH);
    expect(verdict(padded)).toMatch(/^ok:/);
  });

  it('stores the trimmed name, which is what the registry answers with', () => {
    expect(verdict('  acme.orders\t\n')).toBe('ok:acme.orders');
    expect(verdict(at(0x85) + 'acme' + at(0xa0))).toBe('ok:acme');
  });

  it.each([
    ['empty', ''],
    ['blank', '   '],
    ['only a dot', '.'],
    ['a leading dot', '.a'],
    ['a trailing dot', 'a.'],
    ['a double dot', 'a..b'],
    ['a hyphen', 'order-created'],
    ['a space inside', 'order created'],
    ['a slash', 'acme/orders'],
    ['a colon', 'acme:orders'],
    ['a plus', 'acme+orders'],
    ['an accented letter', 'caf' + at(0xe9)],
    ['a CJK character', at(0x4e2d)],
    ['an Arabic-Indic digit', at(0x663)],
    ['a full-width letter', at(0xff41)],
    ['a NUL', 'a' + at(0) + 'b'],
    ['a newline inside', 'a\nb'],
    ['a byte-order mark in front', at(0xfeff) + 'a'],
    ['a zero-width space in front', at(0x200b) + 'a'],
    ['a unit separator in front', at(0x1f) + 'a'],
    ['a percent sign', 'a%41'],
    ['a dollar', 'a$x'],
  ])('refuses %s', (_label, name) => {
    expect(verdict(name)).toBe('subject_name_invalid');
  });

  it('refuses a lone surrogate without repeating it', () => {
    const check = checkSubjectName('a' + at(0xd800));

    expect(check.ok).toBe(false);
    // The message is shown in the page and the registry once failed on exactly this character.
    expect(isWellFormed(messageOf(check))).toBe(true);
  });

  describe('says which character is wrong', () => {
    it.each([
      ['order-created', "'-' (U+002D)"],
      ['order created', 'A space'],
      ['caf' + at(0xe9), "'" + at(0xe9) + "' (U+00E9)"],
      ['a' + at(0), 'The character U+0000'],
      ['a' + at(0xd83d, 0xde00), 'U+1F600'],
    ])('%j: %s', (name, expected) => {
      expect(messageOf(checkSubjectName(name))).toContain(expected);
    });

    it('says how the dots are wrong when every character is allowed', () => {
      expect(messageOf(checkSubjectName('.a'))).toContain('start with a dot');
      expect(messageOf(checkSubjectName('a.'))).toContain('end with a dot');
      expect(messageOf(checkSubjectName('a..b'))).toContain('two dots in a row');
    });

    it('always ends with what a name looks like', () => {
      expect(messageOf(checkSubjectName('a b'))).toContain('acme.orders.OrderCreated');
    });
  });

  it('cannot be the segment the create route uses, so the route can never shadow a subject', () => {
    // `/subjects/new-subject` is the create screen. A hyphen is not in the grammar, so no
    // subject can be named it and the route cannot hide one. (`new` is a valid name, which is
    // exactly why the route is not `/subjects/new`.)
    expect(checkSubjectName('new-subject').ok).toBe(false);
    expect(checkSubjectName('new').ok).toBe(true);
  });
});

describe('slugSubjectName', () => {
  it.each([
    ['Order Created', 'Order_Created'],
    ['order created', 'order_created'],
    ['order-created', 'order_created'],
    ['  Order   Created  ', 'Order_Created'],
    ['Order, Created!', 'Order_Created'],
    ['acme orders / Order Created', 'acme_orders_Order_Created'],
    ['acme.orders.Order Created', 'acme.orders.Order_Created'],
    ['acme . orders', 'acme.orders'],
    ['..acme..orders..', 'acme.orders'],
    ['Caf' + at(0xe9) + ' orders', 'Cafe_orders'],
    ['na' + at(0xef) + 've', 'naive'],
    [at(0xff21) + 'cme', 'Acme'],
    ['order (v2)', 'order_v2'],
    ['100% sure', '100_sure'],
    ['  ', ''],
    ['', ''],
    ['!!!', ''],
    [at(0x4e2d, 0x6587), ''],
  ])('turns %j into %j', (text, expected) => {
    expect(slugSubjectName(text)).toBe(expected);
  });

  it('leaves a name the registry accepts exactly as it is', () => {
    for (const name of ['a', 'acme.orders.OrderCreated', '_internal', '_.a._', '9.9', 'A_b.C_d']) {
      expect(slugSubjectName(name)).toBe(name);
    }
  });

  it('keeps an underscore the author typed at the edge of a word', () => {
    // The ones it makes itself are dropped there; these are not its to drop.
    expect(slugSubjectName('_internal events')).toBe('_internal_events');
    expect(slugSubjectName('events_ internal')).toBe('events__internal');
    expect(slugSubjectName('x._y z')).toBe('x._y_z');
  });

  it('applies the two rewrites SubjectNormalizer applies to a type name', () => {
    // Pinned for every SDK by the conformance corpus: `+` and `:` become `.`.
    expect(slugSubjectName('Acme.Orders+Created')).toBe('Acme.Orders.Created');
    expect(slugSubjectName('acme:orders:created')).toBe('acme.orders.created');
  });

  it('does not apply the rewrites that would be wrong for words', () => {
    // SubjectNormalizer cuts at the first comma, which is right for an assembly-qualified name
    // and would eat half of a title.
    expect(slugSubjectName('Orders, refunds and returns')).toBe('Orders_refunds_and_returns');
  });

  it('never changes case', () => {
    expect(slugSubjectName('ORDER Created')).toBe('ORDER_Created');
    expect(slugSubjectName('orders.Created')).not.toBe(slugSubjectName('orders.created'));
  });

  it('is idempotent', () => {
    for (const text of ['Order Created', '..a..b..', 'Caf' + at(0xe9), 'a/b:c+d', '  x  ', '!!!']) {
      const once = slugSubjectName(text);

      expect(slugSubjectName(once)).toBe(once);
    }
  });

  it('always produces a name the registry accepts, or nothing', () => {
    const samples = [
      'Order Created',
      '--',
      'a.b.',
      at(0, 1, 2, 3),
      '😀 launch',
      'a' + at(0xd800) + 'b',
      at(0xdc00) + 'x',
      'x'.repeat(40) + ' ' + 'y'.repeat(40),
      at(0x301),
      'e' + at(0x301),
    ];

    for (const text of samples) {
      const slug = slugSubjectName(text);

      expect(slug === '' || checkSubjectName(slug).ok, `${JSON.stringify(text)} -> ${slug}`).toBe(
        true,
      );
    }
  });

  it('does not shorten a name, because that would be a different subject', () => {
    const long = ('word ' + 'x'.repeat(100) + ' ').repeat(6);
    const slug = slugSubjectName(long);

    expect(slug.length).toBeGreaterThan(SUBJECT_NAME_MAX_LENGTH);
    expect(checkSubjectName(slug).ok).toBe(false);
  });
});

function messageOf(check: SubjectNameCheck): string {
  return check.ok ? '' : check.message;
}

/** `String.prototype.isWellFormed`, spelled out for the library target. */
function isWellFormed(text: string): boolean {
  for (let i = 0; i < text.length; i++) {
    const unit = text.charCodeAt(i);

    if (unit >= 0xd800 && unit <= 0xdbff) {
      const next = text.charCodeAt(i + 1);

      if (!(next >= 0xdc00 && next <= 0xdfff)) {
        return false;
      }

      i++;
    } else if (unit >= 0xdc00 && unit <= 0xdfff) {
      return false;
    }
  }

  return true;
}
