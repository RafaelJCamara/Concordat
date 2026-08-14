using Concordat.Domain.Registry;

namespace Concordat.Domain.Governance;

/// <summary>
/// Something worth telling someone about (M7.5).
/// </summary>
/// <remarks>
/// A deliberately small, closed set. Every member is an event a team would configure an alert
/// for; anything that only matters when you are already looking at the registry belongs in the
/// audit trail instead, which records far more and notifies about none of it.
/// </remarks>
public enum NotificationEvent
{
    /// <summary>A version was registered and went live.</summary>
    VersionRegistered,

    /// <summary>A breaking change was recorded and is waiting for a reviewer (ADR-017).</summary>
    BreakingChangeSubmitted,

    /// <summary>A reviewer approved a breaking change; it is now live.</summary>
    BreakingChangeApproved,

    /// <summary>A reviewer declined a breaking change.</summary>
    BreakingChangeRejected,

    /// <summary>A version was promoted into another environment.</summary>
    VersionPromoted,

    /// <summary>A subject was deprecated.</summary>
    SubjectDeprecated,

    /// <summary>A publisher sent a message a contract forbids.</summary>
    EnforcementViolation,
}

/// <summary>The wire spellings of <see cref="NotificationEvent"/>.</summary>
/// <remarks>
/// Normative for the same reason every other token in this system is (ADR-019): these strings
/// are what a subscription is written against and what a webhook body carries, so a C# rename
/// must not silently stop delivering to someone.
/// </remarks>
public static class NotificationTokens
{
    private static readonly Dictionary<NotificationEvent, string> Tokens = new()
    {
        [NotificationEvent.VersionRegistered] = "VERSION_REGISTERED",
        [NotificationEvent.BreakingChangeSubmitted] = "BREAKING_CHANGE_SUBMITTED",
        [NotificationEvent.BreakingChangeApproved] = "BREAKING_CHANGE_APPROVED",
        [NotificationEvent.BreakingChangeRejected] = "BREAKING_CHANGE_REJECTED",
        [NotificationEvent.VersionPromoted] = "VERSION_PROMOTED",
        [NotificationEvent.SubjectDeprecated] = "SUBJECT_DEPRECATED",
        [NotificationEvent.EnforcementViolation] = "ENFORCEMENT_VIOLATION",
    };

    private static readonly Dictionary<string, NotificationEvent> Events =
        Tokens.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every token, for error messages and documentation.</summary>
    public static IReadOnlyCollection<string> All => Tokens.Values;

    /// <summary>Maps an event to its wire token.</summary>
    /// <param name="value">The event.</param>
    /// <returns>The stable token.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The event is not a known member.</exception>
    public static string For(NotificationEvent value) =>
        Tokens.TryGetValue(value, out var token)
            ? token
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown notification event.");

    /// <summary>Parses a wire token.</summary>
    /// <param name="token">The token.</param>
    /// <param name="value">The event, when it parsed.</param>
    /// <returns>Whether it parsed.</returns>
    public static bool TryParse(string? token, out NotificationEvent value)
    {
        value = default;

        return !string.IsNullOrWhiteSpace(token) && Events.TryGetValue(token.Trim(), out value);
    }
}

/// <summary>
/// A notification staged for delivery, written in the same transaction as the change that
/// caused it (M7.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the outbox, and the transaction is the entire point.</b> Sending an email or a
/// webhook inside a request handler means a change can commit and its notification vanish, or a
/// notification can go out for a change that then rolls back — "your breaking change was
/// approved" about an approval that did not happen. Writing a row alongside the change makes
/// the two atomic, and a separate pump makes delivery someone else's problem.
/// </para>
/// <para>
/// <b>Delivery is at-least-once, and that is a promise to the receiver, not an apology.</b> A
/// crash between sending and marking sent must re-send rather than drop: a duplicate "breaking
/// change awaiting approval" is noise, and a missing one is the outage this exists to prevent.
/// Webhook receivers get <see cref="Id"/> to deduplicate on.
/// </para>
/// </remarks>
public sealed class OutboxMessage
{
    /// <summary>The longest permitted subject/target name, in characters.</summary>
    public const int MaxTargetLength = 512;

    /// <summary>The longest permitted body, in characters.</summary>
    public const int MaxBodyLength = 8192;

    /// <summary>How many delivery attempts are made before a message is parked.</summary>
    /// <remarks>
    /// Parked, not deleted. A message nobody could deliver is evidence about the channel, and
    /// discarding it would make a misconfigured webhook indistinguishable from a quiet week.
    /// </remarks>
    public const int MaxAttempts = 5;

    private OutboxMessage(
        Guid id,
        EnvironmentId environmentId,
        NotificationEvent @event,
        string target,
        string body,
        DateTimeOffset occurredAt)
    {
        Id = id;
        EnvironmentId = environmentId;
        Event = @event;
        Target = target;
        Body = body;
        OccurredAt = occurredAt;
        NextAttemptAt = occurredAt;
    }

    // Materialisation only.
    private OutboxMessage()
    {
        Target = null!;
        Body = null!;
    }

    /// <summary>The message's identity, and the key a receiver deduplicates on.</summary>
    public Guid Id { get; }

    /// <summary>Which environment it happened in.</summary>
    public EnvironmentId EnvironmentId { get; }

    /// <summary>What happened.</summary>
    public NotificationEvent Event { get; }

    /// <summary>What it happened to — usually a subject name.</summary>
    public string Target { get; private set; }

    /// <summary>The human-readable summary a channel renders.</summary>
    public string Body { get; private set; }

    /// <summary>When the change happened, not when delivery was attempted.</summary>
    public DateTimeOffset OccurredAt { get; }

    /// <summary>When it was delivered, or null while it is still pending.</summary>
    public DateTimeOffset? DeliveredAt { get; private set; }

    /// <summary>How many delivery attempts have been made.</summary>
    public int Attempts { get; private set; }

    /// <summary>The earliest time the pump should try again.</summary>
    public DateTimeOffset NextAttemptAt { get; private set; }

    /// <summary>Why the last attempt failed, when one did.</summary>
    public string? LastError { get; private set; }

    /// <summary>Whether delivery has been abandoned after <see cref="MaxAttempts"/> tries.</summary>
    public bool Parked { get; private set; }

    /// <summary>Stages a notification.</summary>
    /// <param name="environmentId">Which environment.</param>
    /// <param name="event">What happened.</param>
    /// <param name="target">What it happened to.</param>
    /// <param name="body">The summary a channel renders.</param>
    /// <param name="occurredAt">When.</param>
    /// <returns>The staged message.</returns>
    /// <remarks>
    /// Over-long values are truncated rather than refused, for the same reason audit entries
    /// are: this is a side effect of a change the domain already allowed, and failing the
    /// change because its description does not fit would make notifications a source of
    /// outages.
    /// </remarks>
    public static OutboxMessage Stage(
        EnvironmentId environmentId,
        NotificationEvent @event,
        string? target,
        string? body,
        DateTimeOffset occurredAt) =>
        new(Guid.CreateVersion7(),
            environmentId,
            @event,
            Clamp(target ?? string.Empty, MaxTargetLength),
            Clamp(body ?? string.Empty, MaxBodyLength),
            occurredAt.ToUniversalTime());

    /// <summary>Records a successful delivery.</summary>
    /// <param name="at">When.</param>
    public void MarkDelivered(DateTimeOffset at)
    {
        DeliveredAt = at.ToUniversalTime();
        LastError = null;
    }

    /// <summary>Records a failed attempt and schedules the next one.</summary>
    /// <param name="at">When the attempt failed.</param>
    /// <param name="error">Why.</param>
    /// <remarks>
    /// Backoff doubles from one minute and is capped by <see cref="MaxAttempts"/>. It is
    /// deliberately coarse: a webhook endpoint that is down stays down for minutes, not
    /// milliseconds, and retrying tightly turns one broken subscriber into load on the
    /// registry.
    /// </remarks>
    public void MarkFailed(DateTimeOffset at, string? error)
    {
        Attempts++;
        LastError = error is null ? null : Clamp(error, MaxBodyLength);

        if (Attempts >= MaxAttempts)
        {
            Parked = true;
            return;
        }

        NextAttemptAt = at.ToUniversalTime() + TimeSpan.FromMinutes(Math.Pow(2, Attempts - 1));
    }

    private static string Clamp(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
