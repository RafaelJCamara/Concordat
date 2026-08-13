namespace Concordat.Domain.Messaging;

/// <summary>
/// What a publisher knows about the message it is about to send.
/// </summary>
/// <remarks>
/// <para>
/// Transport-neutral by construction. There is deliberately <b>no <see cref="System.Type"/>
/// here</b>: the moment a CLR type reaches the seam, .NET's type system starts leaking into
/// subject names that four other SDKs have to reproduce (ADR-019). Turning
/// <c>typeof(T).FullName</c> into a string is the caller's job, one layer up, where it is
/// obviously a .NET concern.
/// </para>
/// <para>
/// The routing fields are unused by v1's resolver and present because the alternatives DESIGN
/// §3 names — <c>ExchangeRoutingKey</c>, <c>Queue</c> — need them, and adding a parameter to a
/// published seam later is worse than carrying two nulls now.
/// </para>
/// </remarks>
public sealed record PublishContext
{
    /// <summary>The message type as the publisher declared it, from <c>properties.type</c>.</summary>
    public string? MessageType { get; init; }

    /// <summary>The exchange being published to, if any.</summary>
    public string? Exchange { get; init; }

    /// <summary>The routing key, if any.</summary>
    public string? RoutingKey { get; init; }

    /// <summary>The message headers, for a resolver that reads a framework's own convention.</summary>
    public IReadOnlyDictionary<string, object?>? Headers { get; init; }
}

/// <summary>
/// Decides which subject a message being published belongs to (ADR-011).
/// </summary>
/// <remarks>
/// <para>
/// <b>Publish side only.</b> That is the whole point of the design, not an implementation
/// detail: the envelope then carries <c>concordat-subject</c>, so a consumer reads the subject
/// off the message rather than re-deriving it. RabbitMQ's publisher and consumer name
/// different things — <c>(exchange, routing key)</c> versus <c>(queue)</c> — and any scheme
/// requiring both to compute the same answer from what each can see cannot work.
/// </para>
/// <para>
/// A seam rather than a constant because no two client libraries put the type in the same
/// place, and because each non-.NET SDK will have its own convention.
/// </para>
/// </remarks>
public interface ISubjectResolver
{
    /// <summary>Resolves the subject for a message about to be published.</summary>
    /// <param name="context">What the publisher knows.</param>
    /// <returns>Resolved, absent, or unusable — never an exception.</returns>
    SubjectResolution Resolve(PublishContext context);
}
