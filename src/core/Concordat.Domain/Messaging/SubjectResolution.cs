using Concordat.Domain.Registry;
using Concordat.Domain.Results;

namespace Concordat.Domain.Messaging;

/// <summary>The outcome of resolving a subject: resolved, absent, or unusable.</summary>
/// <remarks>
/// <para>
/// Three outcomes, for the same reason <see cref="EnvelopeReadResult"/> has three.
/// <b>Absent is not a failure.</b> <c>properties.type</c> is optional in AMQP, and ADR-011
/// records the consequence plainly: a publisher that sets no type gets no subject. That is the
/// ordinary state of an un-instrumented brownfield publisher, and the estate it describes is
/// what <c>concordat infer</c> (ADR-014) exists to help.
/// </para>
/// <para>
/// <b>Unusable is a failure</b>, and a different one: someone did set a type and it cannot
/// become a subject. Collapsing the two would force a choice between nagging every legacy
/// publisher and silently swallowing a typo in an instrumented one.
/// </para>
/// </remarks>
public sealed class SubjectResolution
{
    private SubjectResolution(SubjectName? subject, DomainError? error)
    {
        Subject = subject;
        Error = error;
    }

    /// <summary>The subject, when one was resolved.</summary>
    public SubjectName? Subject { get; }

    /// <summary>Why resolution failed, when it did.</summary>
    public DomainError? Error { get; }

    /// <summary>Whether a subject was resolved.</summary>
    public bool IsResolved => Subject is not null;

    /// <summary>Whether a type was supplied but could not become a subject.</summary>
    public bool IsUnusable => Error is not null;

    /// <summary>The message carries nothing to resolve from.</summary>
    public static SubjectResolution NoSubject { get; } = new(null, null);

    /// <summary>A subject was resolved.</summary>
    /// <param name="subject">The subject.</param>
    /// <returns>The outcome.</returns>
    public static SubjectResolution Resolved(SubjectName subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return new SubjectResolution(subject, null);
    }

    /// <summary>A type was supplied but cannot become a subject.</summary>
    /// <param name="code">A constant from <see cref="ConcordatCodes"/>.</param>
    /// <param name="message">What was wrong, and what to write instead.</param>
    /// <returns>The outcome.</returns>
    public static SubjectResolution Unusable(string code, string message) =>
        new(null, new DomainError(code, message));
}
