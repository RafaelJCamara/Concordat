using System.Security.Cryptography;
using System.Text;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;

namespace Concordat.Domain.Identity;

/// <summary>An API key and the one-time secret that goes with it.</summary>
/// <param name="Key">The stored record.</param>
/// <param name="Secret">
/// The full credential, in the form the caller sends. <b>This is the only time it exists.</b>
/// </param>
public sealed record IssuedApiKey(ApiKey Key, string Secret);

/// <summary>What an API key is for.</summary>
/// <remarks>
/// <b>A browser session is a short-lived API key, not a second credential format.</b> The
/// alternative — inventing session tokens — means a second thing to verify, a second thing to
/// revoke, and a second place for an authentication bug to live. Reusing this mechanism means
/// sign-in exercises the same verification path that CI does, every day.
/// </remarks>
public enum ApiKeyKind
{
    /// <summary>Issued deliberately, for CI or an SDK. Listed, and revoked by hand.</summary>
    Standing = 1,

    /// <summary>
    /// Minted by a sign-in and short-lived. Never listed among a tenant's keys, because a
    /// screenful of one-per-sign-in rows would bury the keys somebody actually has to manage.
    /// </summary>
    Session = 2,
}

/// <summary>
/// A credential for CI and the SDKs, hashed at rest (ADR-008).
/// </summary>
/// <remarks>
/// <para>
/// <b>The stored form is a SHA-256 hash, and a fast hash is the right choice here.</b> Slow
/// KDFs exist to make guessing a low-entropy secret expensive; this secret is 256 bits from a
/// CSPRNG, so there is nothing to guess and the cost would buy nothing while adding
/// milliseconds to every authenticated request. Passwords are the opposite case and get the
/// opposite treatment.
/// </para>
/// <para>
/// <b>The credential carries its own id.</b> <c>cdt_&lt;keyId&gt;_&lt;secret&gt;</c> lets the
/// server look the row up by an indexed column and then compare one hash in constant time.
/// Hashing the whole credential and searching by hash would work too, but it makes the key id
/// unavailable for logging, revocation-by-prefix, or telling a user which of their keys was
/// used — and it makes an unknown key indistinguishable from a database problem.
/// </para>
/// <para>
/// <b>Revocation and expiry fail closed and are indistinguishable to the caller.</b>
/// <see cref="Verify"/> returns the same answer for an unknown key, a wrong secret, a revoked
/// key and an expired one, because telling an attacker which of their guesses was once real is
/// information they cannot otherwise get.
/// </para>
/// </remarks>
public sealed class ApiKey
{
    /// <summary>The prefix every credential carries, so one is recognisable on sight.</summary>
    /// <remarks>
    /// Not decoration: secret scanners key on prefixes, and a credential that looks like
    /// random text is one that gets pasted into an issue and never noticed.
    /// </remarks>
    public const string Prefix = "cdt";

    /// <summary>The length of the public key id, in characters.</summary>
    public const int KeyIdLength = 16;

    /// <summary>The longest permitted label, in characters.</summary>
    public const int MaxLabelLength = 128;

    /// <summary>The length of the secret half, in characters.</summary>
    public const int SecretLength = 43;

    /// <summary>
    /// The alphabet both halves are drawn from.
    /// </summary>
    /// <remarks>
    /// <b>Alphanumeric, and that is not cosmetic.</b> The obvious choice is base64url, whose
    /// alphabet contains <c>_</c> — the separator this credential uses. Roughly half of all
    /// secrets would then have contained a <c>_</c> and failed to parse, which is a bug that
    /// ships looking like an intermittent authentication failure. 62 characters over 43 gives
    /// ~256 bits, and nothing here needs shell or URL escaping.
    /// </remarks>
    private const string Alphabet =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    private ApiKey(
        Guid id,
        TenantId tenantId,
        UserId? userId,
        string keyId,
        string secretHash,
        string label,
        IReadOnlyList<string> scopes,
        DateTimeOffset createdAt,
        DateTimeOffset? expiresAt,
        ApiKeyKind kind,
        ActorId actor)
    {
        Actor = actor;
        Kind = kind;
        Id = id;
        TenantId = tenantId;
        UserId = userId;
        KeyId = keyId;
        SecretHash = secretHash;
        Label = label;
        Scopes = scopes;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    // Materialisation only.
    private ApiKey()
    {
        Kind = ApiKeyKind.Standing;
        Actor = null!;
        KeyId = null!;
        SecretHash = null!;
        Label = null!;
        Scopes = [];
    }

    /// <summary>The surrogate identity.</summary>
    public Guid Id { get; }

    /// <summary>Which tenant the key acts in.</summary>
    public TenantId TenantId { get; }

    /// <summary>Who issued it, or null for a key not tied to a person.</summary>
    public UserId? UserId { get; }

    /// <summary>The public half, carried in the credential and safe to log.</summary>
    public string KeyId { get; private set; }

    /// <summary>The SHA-256 of the secret, hex-encoded. Never the secret.</summary>
    public string SecretHash { get; private set; }

    /// <summary>What this key is for, as its creator described it.</summary>
    public string Label { get; private set; }

    /// <summary>Whether it was issued deliberately or minted by a sign-in.</summary>
    public ApiKeyKind Kind { get; private set; }

    /// <summary>
    /// How requests made with this key are attributed in the audit trail.
    /// </summary>
    /// <remarks>
    /// <b>Stored rather than derived, and that is what makes the trail readable.</b> A session
    /// key is how a person acts through the web app, and attributing their changes to
    /// <c>key:session for alice@example.com</c> instead of <c>alice@example.com</c> would make
    /// every entry a puzzle. Deriving it would mean a user lookup on every authenticated
    /// request — this sits on the SDK's hot path — or string-munging a label, which breaks the
    /// first time somebody names a key <c>session for the demo</c>.
    /// </remarks>
    public ActorId Actor { get; private set; }

    /// <summary>
    /// What it may do — a subset of the issuer's own scopes, never a superset.
    /// </summary>
    public IReadOnlyList<string> Scopes { get; private set; }

    /// <summary>When it was created.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>When it stops working, or null for a key that never expires.</summary>
    public DateTimeOffset? ExpiresAt { get; }

    /// <summary>When it was last used to authenticate, or null.</summary>
    /// <remarks>
    /// The only way to answer "is this key still needed?", which is the question that has to be
    /// answered before anyone will revoke anything.
    /// </remarks>
    public DateTimeOffset? LastUsedAt { get; private set; }

    /// <summary>When it was revoked, or null while it is live.</summary>
    public DateTimeOffset? RevokedAt { get; private set; }

    /// <summary>Whether this key can currently authenticate.</summary>
    /// <param name="now">The current time.</param>
    /// <returns>True when it is neither revoked nor expired.</returns>
    public bool IsUsable(DateTimeOffset now) =>
        RevokedAt is null && (ExpiresAt is null || ExpiresAt > now);

    /// <summary>The scopes this key resolves to.</summary>
    /// <returns>The set, with implications expanded.</returns>
    public ScopeSet ScopeSet() => Identity.ScopeSet.Of(Scopes);

    /// <summary>Issues a key and its one-time secret.</summary>
    /// <param name="tenantId">Which tenant it acts in.</param>
    /// <param name="userId">Who issued it.</param>
    /// <param name="label">What it is for.</param>
    /// <param name="scopes">What it may do.</param>
    /// <param name="createdAt">When.</param>
    /// <param name="expiresAt">When it should stop working, or null.</param>
    /// <param name="kind">Whether it is a standing key or a session.</param>
    /// <param name="actor">
    /// How to attribute requests made with this key. Defaults to <c>key:&lt;label&gt;</c>; a
    /// sign-in passes the user's own identity so the audit trail names a person.
    /// </param>
    /// <returns>The key and its secret, or a validation failure.</returns>
    public static Result<IssuedApiKey> Issue(
        TenantId tenantId,
        UserId? userId,
        string? label,
        IReadOnlyList<string>? scopes,
        DateTimeOffset createdAt,
        DateTimeOffset? expiresAt = null,
        ApiKeyKind kind = ApiKeyKind.Standing,
        ActorId? actor = null)
    {
        var trimmed = label?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return Result<IssuedApiKey>.Failure(
                ConcordatCodes.ApiKeyLabelInvalid,
                "A label is required. An unlabelled key is one nobody dares revoke, because " +
                "nobody can work out what would break.");
        }

        if (trimmed.Length > MaxLabelLength)
        {
            return Result<IssuedApiKey>.Failure(
                ConcordatCodes.ApiKeyLabelInvalid,
                $"A label may be at most {MaxLabelLength} characters.");
        }

        var parsed = Scope.Parse(scopes, out var granted);
        if (parsed.IsFailure)
        {
            return Result<IssuedApiKey>.Failure(parsed.Error!);
        }

        if (expiresAt is { } expiry && expiry <= createdAt)
        {
            return Result<IssuedApiKey>.Failure(
                ConcordatCodes.ApiKeyLabelInvalid,
                "An expiry in the past would issue a key that never worked.");
        }

        // The id is public — it travels in the credential and is safe to log — so its length is
        // about collision resistance, not secrecy.
        var keyId = RandomNumberGenerator.GetString(Alphabet, KeyIdLength);
        var secret = RandomNumberGenerator.GetString(Alphabet, SecretLength);

        var key = new ApiKey(
            Guid.CreateVersion7(),
            tenantId,
            userId,
            keyId,
            Hash(secret),
            trimmed,
            granted,
            createdAt.ToUniversalTime(),
            expiresAt?.ToUniversalTime(),
            kind,
            actor ?? ActorId.Create($"key:{trimmed}").Value);

        return Result<IssuedApiKey>.Success(
            new IssuedApiKey(key, $"{Prefix}_{keyId}_{secret}"));
    }

    /// <summary>Splits a presented credential into its parts.</summary>
    /// <param name="credential">The value from the <c>Authorization</c> header.</param>
    /// <param name="keyId">The public key id, when it parsed.</param>
    /// <param name="secret">The secret half, when it parsed.</param>
    /// <returns>True when the shape is right. <b>Says nothing about validity.</b></returns>
    public static bool TryParse(string? credential, out string keyId, out string secret)
    {
        keyId = string.Empty;
        secret = string.Empty;

        if (string.IsNullOrWhiteSpace(credential))
        {
            return false;
        }

        var parts = credential.Trim().Split('_');

        if (parts.Length is not 3 ||
            !string.Equals(parts[0], Prefix, StringComparison.Ordinal) ||
            parts[1].Length is not KeyIdLength ||
            parts[2].Length is 0)
        {
            return false;
        }

        keyId = parts[1];
        secret = parts[2];
        return true;
    }

    /// <summary>Whether a presented secret matches this key, and the key may be used.</summary>
    /// <param name="secret">The secret half of the credential.</param>
    /// <param name="now">The current time.</param>
    /// <returns>True when the caller is authenticated.</returns>
    /// <remarks>
    /// The comparison is constant-time. A byte-by-byte early exit leaks how much of a guess was
    /// correct, which over enough requests recovers the hash a byte at a time.
    /// </remarks>
    public bool Verify(string? secret, DateTimeOffset now)
    {
        if (!IsUsable(now) || string.IsNullOrEmpty(secret))
        {
            return false;
        }

        var presented = Encoding.ASCII.GetBytes(Hash(secret));
        var stored = Encoding.ASCII.GetBytes(SecretHash);

        return CryptographicOperations.FixedTimeEquals(presented, stored);
    }

    /// <summary>Records that the key authenticated a request.</summary>
    /// <param name="at">When.</param>
    public void RecordUse(DateTimeOffset at) => LastUsedAt = at.ToUniversalTime();

    /// <summary>Revokes the key. Terminal.</summary>
    /// <param name="at">When.</param>
    /// <remarks>
    /// Kept rather than deleted, so the audit trail can still name the credential that made a
    /// change and an operator can see that a leaked key was dealt with.
    /// </remarks>
    public void Revoke(DateTimeOffset at) => RevokedAt ??= at.ToUniversalTime();

    /// <summary>Hashes a secret for storage or comparison.</summary>
    /// <param name="secret">The secret.</param>
    /// <returns>Lowercase hex SHA-256.</returns>
    public static string Hash(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    }
}
