// What a password has to be, as the registry asks it (ADR-048).

/**
 * The shortest password the registry accepts: `User.MinPasswordLength`.
 *
 * Length only, with no character-class rules. The same twelve characters the instance was claimed
 * with, said in one place that the sentence beside every password field reads.
 */
export const MIN_PASSWORD_LENGTH = 12;

/** Why a password cannot be sent, in words for the field it is typed into. */
export interface PasswordFinding {
  /** The registry's own code where it would refuse it, this client's where it would not. */
  readonly code: 'password_invalid' | 'password_mismatch' | 'password_unchanged';
  readonly message: string;
}

/**
 * Whether a password is long enough.
 *
 * `.length` counts UTF-16 code units, which is what the registry's `string.Length` counts, so a
 * password with an emoji in it is as many characters here as there. Nothing is trimmed: a space is a
 * character, and a registry that stored a trimmed password would be storing one nobody typed.
 */
export function checkPasswordLength(password: string): PasswordFinding | null {
  return password.length < MIN_PASSWORD_LENGTH
    ? {
        code: 'password_invalid',
        message: `A password must be at least ${MIN_PASSWORD_LENGTH} characters. There are no other rules.`,
      }
    : null;
}

/** What a change of one's own password comes to. */
export interface PasswordChange {
  /**
   * The finding that blocks sending it, or null when there is a request to make.
   *
   * <b>Every one of these is something the registry would refuse or something there is no request
   * for.</b> The registry refuses a new password under twelve characters and one equal to the current
   * one (`password_invalid`), and a confirmation that does not match is a typo the registry never
   * sees: there is no request that means "the second box differed".
   */
  readonly finding: PasswordFinding | null;
}

/**
 * Judges a new password as typed, against the one it replaces and the second box.
 *
 * The current password is not judged here: only the registry can say whether it is right, and
 * guessing at that client-side would be a way to learn it from the response time.
 */
export function checkPasswordChange(
  current: string,
  next: string,
  confirmation: string,
): PasswordChange {
  const length = checkPasswordLength(next);

  if (length !== null) {
    return { finding: length };
  }

  if (next === current) {
    return {
      finding: {
        code: 'password_unchanged',
        message: 'That is the password you already have. Choose a different one.',
      },
    };
  }

  if (next !== confirmation) {
    return {
      finding: {
        code: 'password_mismatch',
        message: 'The two new passwords are not the same.',
      },
    };
  }

  return { finding: null };
}
