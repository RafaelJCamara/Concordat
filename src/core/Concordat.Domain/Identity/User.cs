using System.Text.RegularExpressions;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;

namespace Concordat.Domain.Identity;

/// <summary>The surrogate identity of a <see cref="User"/>.</summary>
/// <param name="Value">The underlying identifier.</param>
public readonly record struct UserId(Guid Value)
{
    /// <summary>Creates a new identifier.</summary>
    /// <returns>A fresh <see cref="UserId"/>.</returns>
    public static UserId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}

/// <summary>
/// An email address, normalised for comparison.
/// </summary>
/// <remarks>
/// <b>Lowercased, unlike a subject name.</b> A subject name is a wire identifier where case is
/// meaning (M2.3); an email address is a human's login, and letting <c>Alice@example.com</c>
/// and <c>alice@example.com</c> be two accounts is how one person ends up locked out of the
/// permissions they were granted.
/// </remarks>
public sealed partial record EmailAddress
{
    /// <summary>The longest permitted address, in characters.</summary>
    public const int MaxLength = 254;

    private EmailAddress(string value) => Value = value;

    /// <summary>The normalised address.</summary>
    public string Value { get; }

    /// <summary>Creates an address.</summary>
    /// <param name="value">The address as typed.</param>
    /// <returns>The address, or a validation failure.</returns>
    /// <remarks>
    /// Deliberately not an RFC 5322 grammar. A regex that claims to validate email is a
    /// well-known way to reject valid addresses; this catches the typo and lets the first
    /// password-reset mail judge the rest.
    /// </remarks>
    public static Result<EmailAddress> Create(string? value)
    {
        var trimmed = value?.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(trimmed))
        {
            return Result<EmailAddress>.Failure(
                ConcordatCodes.EmailInvalid, "An email address is required.");
        }

        if (trimmed.Length > MaxLength)
        {
            return Result<EmailAddress>.Failure(
                ConcordatCodes.EmailInvalid,
                $"An email address may be at most {MaxLength} characters.");
        }

        return Pattern().IsMatch(trimmed)
            ? Result<EmailAddress>.Success(new EmailAddress(trimmed))
            : Result<EmailAddress>.Failure(
                ConcordatCodes.EmailInvalid, $"'{value}' is not an email address.");
    }

    /// <summary>Rehydrates from storage without validating.</summary>
    /// <param name="value">The stored address.</param>
    /// <returns>The address.</returns>
    public static EmailAddress FromTrusted(string value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value;

    [GeneratedRegex(@"^[^@\s]+@[^@\s.]+(\.[^@\s.]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}

/// <summary>
/// A local account (ADR-008).
/// </summary>
/// <remarks>
/// <para>
/// Concordat ships its own identity so that <c>docker compose up</c> produces a working,
/// authenticated instance with no external dependency. OIDC is supported but optional — the
/// built-in path is what makes first-run evaluation possible.
/// </para>
/// <para>
/// <b>The aggregate never sees a plaintext password.</b> It is handed an already-hashed value
/// by a service that owns the algorithm, so there is no path through the domain on which a
/// password could be stored, logged or compared in the clear.
/// </para>
/// </remarks>
public sealed class User
{
    /// <summary>The shortest password this build accepts.</summary>
    /// <remarks>
    /// Length only, with no character-class rules. Composition requirements measurably push
    /// people towards <c>Password1!</c>; NIST dropped them for that reason, and a longer
    /// minimum buys more than a symbol nobody remembers.
    /// </remarks>
    public const int MinPasswordLength = 12;

    /// <summary>The longest permitted display name, in characters.</summary>
    public const int MaxDisplayNameLength = 128;

    private User(
        UserId id,
        EmailAddress email,
        string displayName,
        string passwordHash,
        DateTimeOffset createdAt)
    {
        Id = id;
        Email = email;
        DisplayName = displayName;
        PasswordHash = passwordHash;
        CreatedAt = createdAt;
    }

    // Materialisation only.
    private User()
    {
        Email = null!;
        DisplayName = null!;
        PasswordHash = null!;
    }

    /// <summary>The surrogate identity.</summary>
    public UserId Id { get; }

    /// <summary>The login, normalised to lowercase.</summary>
    public EmailAddress Email { get; private set; }

    /// <summary>What the audit trail and the UI show.</summary>
    public string DisplayName { get; private set; }

    /// <summary>The hash. <b>Never a password, and never leaves the server.</b></summary>
    public string PasswordHash { get; private set; }

    /// <summary>Whether the account may sign in.</summary>
    /// <remarks>
    /// Disabled rather than deleted: audit entries and API keys reference a user, and removing
    /// the row would either orphan them or cascade away the record of what that person did.
    /// </remarks>
    public bool Disabled { get; private set; }

    /// <summary>When the account was created.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>When it last signed in, or null.</summary>
    public DateTimeOffset? LastSignedInAt { get; private set; }

    /// <summary>How this user is attributed in the audit trail.</summary>
    /// <returns>An actor identifier.</returns>
    public ActorId Actor() => ActorId.Create(Email.Value).Value;

    /// <summary>Creates an account.</summary>
    /// <param name="email">The login.</param>
    /// <param name="displayName">What to show; defaults to the address's local part.</param>
    /// <param name="passwordHash">An already-hashed password.</param>
    /// <param name="createdAt">When.</param>
    /// <returns>The user, or a validation failure.</returns>
    public static Result<User> Create(
        string? email, string? displayName, string passwordHash, DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);

        var address = EmailAddress.Create(email);
        if (address.IsFailure)
        {
            return Result<User>.Failure(address.Error!);
        }

        var name = displayName?.Trim();

        if (string.IsNullOrEmpty(name))
        {
            name = address.Value.Value[..address.Value.Value.IndexOf('@', StringComparison.Ordinal)];
        }

        if (name.Length > MaxDisplayNameLength)
        {
            name = name[..MaxDisplayNameLength];
        }

        return Result<User>.Success(new User(
            UserId.New(), address.Value, name, passwordHash, createdAt.ToUniversalTime()));
    }

    /// <summary>Checks a proposed password against the length rule.</summary>
    /// <param name="password">The proposed password.</param>
    /// <returns>Success, or a failure explaining the rule.</returns>
    /// <remarks>
    /// Static and separate from <see cref="Create"/> because the aggregate must never hold a
    /// plaintext password, and this is the one place that is allowed to look at one.
    /// </remarks>
    public static Result CheckPassword(string? password) =>
        password is null || password.Length < MinPasswordLength
            ? Result.Failure(
                ConcordatCodes.PasswordInvalid,
                $"A password must be at least {MinPasswordLength} characters. There are no " +
                "character-class rules: composition requirements push people towards " +
                "predictable substitutions, and length buys more.")
            : Result.Success();

    /// <summary>Replaces the stored hash.</summary>
    /// <param name="passwordHash">An already-hashed password.</param>
    public void SetPasswordHash(string passwordHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        PasswordHash = passwordHash;
    }

    /// <summary>Changes the display name.</summary>
    /// <param name="displayName">The new name.</param>
    public void Rename(string? displayName)
    {
        var name = displayName?.Trim();

        if (!string.IsNullOrEmpty(name))
        {
            DisplayName = name.Length > MaxDisplayNameLength
                ? name[..MaxDisplayNameLength]
                : name;
        }
    }

    /// <summary>Records a successful sign-in.</summary>
    /// <param name="at">When.</param>
    public void RecordSignIn(DateTimeOffset at) => LastSignedInAt = at.ToUniversalTime();

    /// <summary>Stops the account signing in, without losing what it did.</summary>
    /// <param name="disabled">Whether to disable.</param>
    public void SetDisabled(bool disabled) => Disabled = disabled;
}

/// <summary>
/// A user's role in a tenant.
/// </summary>
/// <remarks>
/// Separate from <see cref="User"/> because Cloud (M9) has one account belonging to several
/// organisations, and self-hosted has exactly one membership per user. Modelling it now costs
/// a table; retrofitting it means rewriting every authorisation path.
/// </remarks>
public sealed class Membership
{
    private Membership(Guid id, TenantId tenantId, UserId userId, Role role, DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        UserId = userId;
        Role = role;
        CreatedAt = createdAt;
    }

    // Materialisation only.
    private Membership()
    {
    }

    /// <summary>The membership's identity.</summary>
    public Guid Id { get; }

    /// <summary>Which tenant.</summary>
    public TenantId TenantId { get; }

    /// <summary>Which user.</summary>
    public UserId UserId { get; }

    /// <summary>What they may do.</summary>
    public Role Role { get; private set; }

    /// <summary>When it was granted.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>Grants a role.</summary>
    /// <param name="tenantId">Which tenant.</param>
    /// <param name="userId">Which user.</param>
    /// <param name="role">What they may do.</param>
    /// <param name="createdAt">When.</param>
    /// <returns>The membership.</returns>
    public static Membership Grant(
        TenantId tenantId, UserId userId, Role role, DateTimeOffset createdAt) =>
        new(Guid.CreateVersion7(), tenantId, userId, role, createdAt.ToUniversalTime());

    /// <summary>Changes the role.</summary>
    /// <param name="role">The new role.</param>
    public void ChangeRole(Role role) => Role = role;
}
