using System.Text.RegularExpressions;
using Concordat.Domain.Contracts;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;

namespace Concordat.Domain.Governance;

/// <summary>
/// A service's declared producer and consumer intent in one environment (M7.4, DESIGN §4
/// Context D).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is what makes impact analysis possible.</b> "Who breaks if I change this subject?"
/// has no answer from the registry's own data — a subject with fifty versions says nothing
/// about who reads them. It is answerable only because services say what they read, and only
/// as accurately as they say it.
/// </para>
/// <para>
/// <b>The unit is the service, not the instance.</b> Fifty pods reporting identical intent at
/// startup are one row, and the newest report wins. Recording instances would turn a rolling
/// deploy into fifty rows that all say the same thing, and impact analysis would report fifty
/// affected consumers where there is one.
/// </para>
/// <para>
/// <b><see cref="LastSeenAt"/> is not decoration.</b> A registration is a claim made once and
/// then true until contradicted. Without a timestamp, a service decommissioned a year ago
/// blocks a change forever, and the first time anyone works that out is while trying to ship.
/// Impact analysis reports it and marks it stale rather than dropping it, because "there is a
/// consumer nobody has heard from in six months" is information, not noise.
/// </para>
/// </remarks>
public sealed partial class ServiceRegistration
{
    /// <summary>The longest permitted service name, in characters.</summary>
    public const int MaxNameLength = 128;

    /// <summary>How long a registration stays fresh before impact analysis flags it.</summary>
    /// <remarks>
    /// Thirty days is long enough that a service deployed monthly is never wrongly called
    /// stale, and short enough that a decommissioned one stops being cited within a quarter.
    /// It is a reporting hint, never a reason to hide a consumer.
    /// </remarks>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(30);

    private ServiceRegistration(
        Guid id,
        EnvironmentId environmentId,
        string name,
        IReadOnlyList<SubjectRef> produces,
        IReadOnlyList<SubjectRef> consumes,
        DateTimeOffset firstSeenAt,
        DateTimeOffset lastSeenAt)
    {
        Id = id;
        EnvironmentId = environmentId;
        Name = name;
        Produces = [.. produces];
        Consumes = [.. consumes];
        FirstSeenAt = firstSeenAt;
        LastSeenAt = lastSeenAt;
    }

    // Materialisation only.
    private ServiceRegistration()
    {
        Name = null!;
        Produces = [];
        Consumes = [];
    }

    /// <summary>The registration's identity.</summary>
    public Guid Id { get; }

    /// <summary>Which environment it runs in.</summary>
    public EnvironmentId EnvironmentId { get; }

    /// <summary>The service's name, unique within the environment.</summary>
    public string Name { get; private set; }

    /// <summary>The subjects it publishes.</summary>
    /// <remarks>
    /// Settable and stored directly rather than wrapping a <see cref="List{T}"/> field. The
    /// column converter hands back an <see cref="IReadOnlyList{T}"/>, and a
    /// <see cref="List{T}"/> backing field would take the insert happily and then fail to
    /// materialise — a bug that only appears the second time a service reports.
    /// </remarks>
    public IReadOnlyList<SubjectRef> Produces { get; private set; }

    /// <summary>The subjects it reads, and at which versions.</summary>
    public IReadOnlyList<SubjectRef> Consumes { get; private set; }

    /// <summary>When it first declared itself.</summary>
    public DateTimeOffset FirstSeenAt { get; }

    /// <summary>When it last declared itself.</summary>
    public DateTimeOffset LastSeenAt { get; private set; }

    /// <summary>Whether nothing has been heard from it recently.</summary>
    /// <param name="now">The current time.</param>
    /// <returns>True when the last report is older than <see cref="StaleAfter"/>.</returns>
    public bool IsStale(DateTimeOffset now) => now - LastSeenAt > StaleAfter;

    /// <summary>Declares a service.</summary>
    /// <param name="environmentId">Which environment.</param>
    /// <param name="name">The service name.</param>
    /// <param name="produces">The subjects it publishes.</param>
    /// <param name="consumes">The subjects it reads.</param>
    /// <param name="at">When it reported.</param>
    /// <returns>The registration, or a validation failure.</returns>
    public static Result<ServiceRegistration> Create(
        EnvironmentId environmentId,
        string? name,
        IReadOnlyList<SubjectRef> produces,
        IReadOnlyList<SubjectRef> consumes,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(produces);
        ArgumentNullException.ThrowIfNull(consumes);

        var validated = ValidateName(name);
        if (validated.IsFailure)
        {
            return Result<ServiceRegistration>.Failure(validated.Error!);
        }

        var utc = at.ToUniversalTime();

        return Result<ServiceRegistration>.Success(new ServiceRegistration(
            Guid.CreateVersion7(),
            environmentId,
            validated.Value,
            produces,
            consumes,
            utc,
            utc));
    }

    /// <summary>Replaces the declared intent and records that the service was seen.</summary>
    /// <param name="produces">The subjects it publishes.</param>
    /// <param name="consumes">The subjects it reads.</param>
    /// <param name="at">When it reported.</param>
    /// <remarks>
    /// A replace, not a merge. A service that stopped consuming a subject has no way to say so
    /// if reports accumulate, and a stale entry that nobody can remove is exactly what makes
    /// impact analysis stop being trusted.
    /// </remarks>
    public void Report(
        IReadOnlyList<SubjectRef> produces, IReadOnlyList<SubjectRef> consumes, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(produces);
        ArgumentNullException.ThrowIfNull(consumes);

        Produces = [.. produces];
        Consumes = [.. consumes];
        LastSeenAt = at.ToUniversalTime();
    }

    /// <summary>Whether this service reads the named subject.</summary>
    /// <param name="subject">The subject.</param>
    /// <returns>The reference it declared, or null.</returns>
    public SubjectRef? ConsumerOf(SubjectName subject)
    {
        ArgumentNullException.ThrowIfNull(subject);

        return Consumes.FirstOrDefault(r => r.Subject == subject);
    }

    private static Result<string> ValidateName(string? name)
    {
        var trimmed = name?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return Result<string>.Failure(
                ConcordatCodes.ServiceNameInvalid, "A service name is required.");
        }

        if (trimmed.Length > MaxNameLength)
        {
            return Result<string>.Failure(
                ConcordatCodes.ServiceNameInvalid,
                $"A service name may be at most {MaxNameLength} characters.");
        }

        return NamePattern().IsMatch(trimmed)
            ? Result<string>.Success(trimmed)
            : Result<string>.Failure(
                ConcordatCodes.ServiceNameInvalid,
                $"'{trimmed}' is not a valid service name. Use letters, digits, '.', '-' and " +
                "'_'. A service name is reported by an SDK at startup and shown in impact " +
                "reports, so it has to survive being written down.");
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
