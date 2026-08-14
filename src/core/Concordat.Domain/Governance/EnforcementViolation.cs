using Concordat.Domain.Registry;
using Concordat.Domain.Results;

namespace Concordat.Domain.Governance;

/// <summary>Which side of the wire a violation was seen on.</summary>
public enum ViolationSide
{
    /// <summary>A publisher sent something a contract forbids.</summary>
    Publish,

    /// <summary>A consumer received something a contract did not expect.</summary>
    Consume,
}

/// <summary>
/// A contract violation an SDK saw, reported back to the registry (decision 25).
/// </summary>
/// <remarks>
/// <para>
/// <b>The registry cannot observe this for itself, which is the whole reason this exists.</b>
/// An enforcement violation happens in the SDK, on the publisher's machine, when a message fails
/// against a contract the registry never sees the traffic for. <c>ENFORCEMENT_VIOLATION</c> was
/// in the notification catalogue from M7.5 because DESIGN §5 lists it, and nothing could ever
/// raise it — a published token no subscription would fire on.
/// </para>
/// <para>
/// <b>One row per distinct violation, not one per message.</b> A broken publisher emits
/// thousands a second; a row each would be a denial of service written by our own SDK. The
/// identity is <see cref="Fingerprint"/> — environment, side, route, subject, code — and repeat
/// sightings advance a counter and a timestamp.
/// </para>
/// <para>
/// <b>The notification fires on first sight only.</b> "This started happening" is an alert;
/// "this is still happening" is a dashboard. Staging one per report would page somebody every
/// reporting window for as long as the fault lasted, which is how alerting stops being read.
/// </para>
/// </remarks>
public sealed class EnforcementViolation
{
    /// <summary>The longest route recorded.</summary>
    public const int MaxRouteLength = 512;

    /// <summary>The longest detail recorded.</summary>
    public const int MaxDetailLength = 1024;

    /// <summary>The longest reporter name recorded.</summary>
    public const int MaxReportedByLength = 128;

    private EnforcementViolation(
        Guid id,
        EnvironmentId environmentId,
        ViolationSide side,
        string route,
        string? subject,
        string code,
        string detail,
        string reportedBy,
        DateTimeOffset firstSeenAt,
        DateTimeOffset lastSeenAt,
        long occurrences)
    {
        Id = id;
        EnvironmentId = environmentId;
        Side = side;
        Route = route;
        Subject = subject;
        Code = code;
        Detail = detail;
        ReportedBy = reportedBy;
        FirstSeenAt = firstSeenAt;
        LastSeenAt = lastSeenAt;
        Occurrences = occurrences;

        // Stored, not computed. It is the lookup key on the hot path of a report, and a computed
        // one would mean a table scan per fingerprint per reporting window per service.
        Fingerprint = Compose(environmentId, side, Route, Subject, Code);
    }

    /// <summary>The row's identity.</summary>
    public Guid Id { get; }

    /// <summary>Which environment the reporting service belongs to.</summary>
    public EnvironmentId EnvironmentId { get; }

    /// <summary>Publish or consume.</summary>
    public ViolationSide Side { get; }

    /// <summary>The exchange and routing key, or the queue.</summary>
    public string Route { get; private set; } = null!;

    /// <summary>The subject, when the SDK could resolve one.</summary>
    public string? Subject { get; }

    /// <summary>The stable <c>concordatCode</c> that classified it.</summary>
    public string Code { get; private set; } = null!;

    /// <summary>The most recent explanation. Overwritten, because the newest is the useful one.</summary>
    public string Detail { get; private set; } = null!;

    /// <summary>The service that reported it, or <c>unknown</c>.</summary>
    public string ReportedBy { get; private set; } = null!;

    /// <summary>When this violation was first seen.</summary>
    public DateTimeOffset FirstSeenAt { get; }

    /// <summary>When it was last seen.</summary>
    public DateTimeOffset LastSeenAt { get; private set; }

    /// <summary>How many messages have hit it.</summary>
    public long Occurrences { get; private set; }

    /// <summary>
    /// What makes two reports the same violation.
    /// </summary>
    /// <remarks>
    /// Deliberately excludes the detail and the reporting service. The same broken route reported
    /// by three replicas is one problem, and a validation message that names a different offending
    /// field each time is still that one problem.
    /// </remarks>
    public string Fingerprint { get; private set; } = null!;

    /// <summary>Builds the fingerprint a report would match.</summary>
    /// <param name="environmentId">The environment.</param>
    /// <param name="side">Publish or consume.</param>
    /// <param name="route">The route or queue.</param>
    /// <param name="subject">The subject, when known.</param>
    /// <param name="code">The classifying code.</param>
    /// <returns>The fingerprint.</returns>
    public static string Compose(
        EnvironmentId environmentId,
        ViolationSide side,
        string route,
        string? subject,
        string code) =>
        $"{environmentId.Value:N}|{(int)side}|{route}|{subject ?? "-"}|{code}";

    /// <summary>Records a violation seen for the first time.</summary>
    /// <param name="environmentId">The environment.</param>
    /// <param name="side">Publish or consume.</param>
    /// <param name="route">The exchange and routing key, or the queue.</param>
    /// <param name="subject">The subject, when the SDK resolved one.</param>
    /// <param name="code">The classifying <c>concordatCode</c>.</param>
    /// <param name="detail">What went wrong.</param>
    /// <param name="reportedBy">The reporting service.</param>
    /// <param name="occurrences">How many messages the reporter counted.</param>
    /// <param name="at">When the report arrived.</param>
    /// <returns>The violation, or the first validation failure.</returns>
    public static Result<EnforcementViolation> Open(
        EnvironmentId environmentId,
        ViolationSide side,
        string? route,
        string? subject,
        string? code,
        string? detail,
        string? reportedBy,
        long occurrences,
        DateTimeOffset at)
    {
        var trimmedRoute = route?.Trim();
        if (string.IsNullOrEmpty(trimmedRoute))
        {
            return Result<EnforcementViolation>.Failure(
                ConcordatCodes.ViolationReportInvalid,
                "A violation report needs the route it happened on.");
        }

        var trimmedCode = code?.Trim();
        if (string.IsNullOrEmpty(trimmedCode))
        {
            return Result<EnforcementViolation>.Failure(
                ConcordatCodes.ViolationReportInvalid,
                "A violation report needs the concordatCode that classified it.");
        }

        if (occurrences < 1)
        {
            return Result<EnforcementViolation>.Failure(
                ConcordatCodes.ViolationReportInvalid,
                $"A violation report must count at least one occurrence; got {occurrences}.");
        }

        var moment = at.ToUniversalTime();

        return Result<EnforcementViolation>.Success(new EnforcementViolation(
            Guid.CreateVersion7(),
            environmentId,
            side,
            Clamp(trimmedRoute, MaxRouteLength),
            string.IsNullOrWhiteSpace(subject) ? null : subject.Trim(),
            Clamp(trimmedCode, 64),
            Clamp(detail?.Trim() ?? string.Empty, MaxDetailLength),
            Clamp(
                string.IsNullOrWhiteSpace(reportedBy) ? "unknown" : reportedBy.Trim(),
                MaxReportedByLength),
            moment,
            moment,
            occurrences));
    }

    /// <summary>Records further sightings of a violation already known.</summary>
    /// <param name="occurrences">How many more.</param>
    /// <param name="detail">The newest explanation, or null to keep the current one.</param>
    /// <param name="reportedBy">The reporting service.</param>
    /// <param name="at">When the report arrived.</param>
    /// <remarks>
    /// <b>Never moves <see cref="FirstSeenAt"/> and never resets the count.</b> "Since when" and
    /// "how many" are the two questions somebody opens this row to answer, and both are ruined by
    /// treating a later report as a fresh start.
    /// </remarks>
    public void Observe(long occurrences, string? detail, string? reportedBy, DateTimeOffset at)
    {
        if (occurrences > 0)
        {
            Occurrences += occurrences;
        }

        if (!string.IsNullOrWhiteSpace(detail))
        {
            Detail = Clamp(detail.Trim(), MaxDetailLength);
        }

        if (!string.IsNullOrWhiteSpace(reportedBy))
        {
            ReportedBy = Clamp(reportedBy.Trim(), MaxReportedByLength);
        }

        var moment = at.ToUniversalTime();
        if (moment > LastSeenAt)
        {
            LastSeenAt = moment;
        }
    }

    private static string Clamp(string value, int max) =>
        value.Length <= max ? value : value[..max];
}

/// <summary>Wire spellings for <see cref="ViolationSide"/>.</summary>
public static class ViolationTokens
{
    /// <summary>The token for a side.</summary>
    /// <param name="side">The side.</param>
    /// <returns>The stable token.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The side is not a known member.</exception>
    public static string For(ViolationSide side) => side switch
    {
        ViolationSide.Publish => "PUBLISH",
        ViolationSide.Consume => "CONSUME",
        _ => throw new ArgumentOutOfRangeException(
            nameof(side), side, $"Unknown violation side '{side}'."),
    };

    /// <summary>Parses a side token.</summary>
    /// <param name="token">The token.</param>
    /// <param name="side">The side, when it parsed.</param>
    /// <returns>Success, or a failure naming what was expected.</returns>
    public static Result Parse(string? token, out ViolationSide side)
    {
        switch (token?.Trim().ToUpperInvariant())
        {
            case "PUBLISH":
                side = ViolationSide.Publish;
                return Result.Success();

            case "CONSUME":
                side = ViolationSide.Consume;
                return Result.Success();

            default:
                side = ViolationSide.Publish;
                return Result.Failure(
                    ConcordatCodes.ViolationReportInvalid,
                    $"'{token}' is not a violation side. Expected PUBLISH or CONSUME.");
        }
    }
}
