import { describe, expect, it } from 'vitest';
import { checkPasswordChange, checkPasswordLength, MIN_PASSWORD_LENGTH } from './password';

// The rule is the registry's (`User.CheckPassword`): twelve characters, nothing else. What is checked
// here is only what the registry would certainly refuse and what there is no request for.

describe('checkPasswordLength', () => {
  it('asks for twelve characters, as the registry does', () => {
    expect(MIN_PASSWORD_LENGTH).toBe(12);
    expect(checkPasswordLength('a'.repeat(11))?.code).toBe('password_invalid');
    expect(checkPasswordLength('a'.repeat(12))).toBeNull();
  });

  it('asks for nothing but length', () => {
    // No class rules: composition requirements push people towards predictable substitutions.
    expect(checkPasswordLength('aaaaaaaaaaaa')).toBeNull();
    expect(checkPasswordLength('            ')).toBeNull();
  });

  it('counts characters the way the registry does, as UTF-16 code units', () => {
    // `string.Length` in .NET counts code units, and so does `.length`: an emoji is two in each.
    expect('😀'.length).toBe(2);
    expect(checkPasswordLength('😀'.repeat(5))).not.toBeNull();
    expect(checkPasswordLength('😀'.repeat(6))).toBeNull();
    expect(checkPasswordLength('a'.repeat(10) + '😀')).toBeNull();
    expect(checkPasswordLength('a'.repeat(9) + '😀')).not.toBeNull();
  });

  it('does not trim, because the registry does not', () => {
    expect(checkPasswordLength('a'.repeat(6) + ' '.repeat(6))).toBeNull();
  });

  it('says the rule, and that there is no other', () => {
    expect(checkPasswordLength('short')?.message).toBe(
      'A password must be at least 12 characters. There are no other rules.',
    );
  });
});

describe('checkPasswordChange', () => {
  const current = 'the current password';

  it('lets through a long, different, confirmed password', () => {
    expect(checkPasswordChange(current, 'a brand new password', 'a brand new password')).toEqual({
      finding: null,
    });
  });

  it('blocks one that is too short, before anything else', () => {
    expect(checkPasswordChange(current, 'short', 'different').finding?.code).toBe(
      'password_invalid',
    );
  });

  it('blocks the password the person already has, as the registry does', () => {
    expect(checkPasswordChange(current, current, current).finding?.code).toBe('password_unchanged');
  });

  it('blocks two boxes that differ, which is a typo and not a request', () => {
    expect(
      checkPasswordChange(current, 'a brand new password', 'a brand new passwrod').finding,
    ).toEqual({
      code: 'password_mismatch',
      message: 'The two new passwords are not the same.',
    });
  });

  it('does not judge the current password, which only the registry can', () => {
    expect(
      checkPasswordChange('', 'a brand new password', 'a brand new password').finding,
    ).toBeNull();
    expect(
      checkPasswordChange('x', 'a brand new password', 'a brand new password').finding,
    ).toBeNull();
  });
});
