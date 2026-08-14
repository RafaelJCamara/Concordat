using System.Security.Cryptography;
using Concordat.Application.Abstractions;
using Microsoft.AspNetCore.Identity;

namespace Concordat.Infrastructure.Identity;

/// <summary>
/// Hashes passwords with ASP.NET Core Identity's reviewed implementation (M8.1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Delegated on purpose.</b> ADR-008 accepts that shipping built-in identity means owning
/// password storage, and the way to own it responsibly is to not hand-roll the part that has
/// been broken in public a hundred times. <c>PasswordHasher&lt;T&gt;</c> is PBKDF2-HMAC-SHA512
/// with a per-password salt and an iteration count that Microsoft raises across releases, and
/// its output is self-describing — the format version travels with the hash, so an old hash
/// stays verifiable after the parameters change.
/// </para>
/// <para>
/// That self-description is what makes <see cref="Verify"/>'s rehash signal work: a password
/// stored under out-of-date parameters is upgraded the moment its owner proves they know it,
/// rather than waiting for a reset that may never come.
/// </para>
/// <para>
/// Argon2id would be a defensible alternative and is what a greenfield design might pick. It
/// is not in the framework, and taking a third-party cryptographic dependency to replace a
/// reviewed in-framework one is a trade this project has not needed to make.
/// </para>
/// </remarks>
public sealed class AspNetPasswordHasher : IPasswordHasher
{
    private readonly PasswordHasher<object> _hasher = new();
    private readonly Lazy<string> _dummy;

    /// <summary>Creates the hasher.</summary>
    public AspNetPasswordHasher() =>
        // Derived once, from a value nobody knows and nobody keeps. Lazy so the cost lands on
        // the first sign-in rather than on startup, and only ever once.
        _dummy = new Lazy<string>(() =>
            _hasher.HashPassword(Placeholder, Convert.ToHexString(RandomNumberGenerator.GetBytes(32))));

    private static object Placeholder { get; } = new();

    /// <inheritdoc />
    public string DummyHash => _dummy.Value;

    /// <inheritdoc />
    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        return _hasher.HashPassword(Placeholder, password);
    }

    /// <inheritdoc />
    public (bool Verified, bool NeedsRehash) Verify(string hash, string password)
    {
        ArgumentNullException.ThrowIfNull(hash);
        ArgumentNullException.ThrowIfNull(password);

        // A malformed stored hash throws rather than returning false, which would be a crash on
        // every sign-in for one corrupted row. It is treated as a failed verification: the
        // caller cannot fix it, and the alternative is a 500 that leaks that the account exists.
        try
        {
            var result = _hasher.VerifyHashedPassword(Placeholder, hash, password);

            return result switch
            {
                PasswordVerificationResult.Success => (true, false),
                PasswordVerificationResult.SuccessRehashNeeded => (true, true),
                _ => (false, false),
            };
        }
        catch (FormatException)
        {
            return (false, false);
        }
    }
}
