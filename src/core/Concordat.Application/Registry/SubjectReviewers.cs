using Concordat.Application.Abstractions;
using Concordat.Domain.Governance;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;

namespace Concordat.Application.Registry;

/// <summary>
/// Names who may approve or reject a subject's held versions, or clears the set so any holder of
/// the scope may (ADR-043).
/// </summary>
/// <param name="EnvironmentId">The environment.</param>
/// <param name="SubjectName">The subject.</param>
/// <param name="Reviewers">The actors, as typed. An empty list clears the set.</param>
public sealed record SetSubjectReviewersCommand(
    EnvironmentId EnvironmentId, string SubjectName, IReadOnlyList<string> Reviewers)
    : ICommand<Subject>;

/// <summary>Handles <see cref="SetSubjectReviewersCommand"/>.</summary>
/// <remarks>
/// <para>
/// <b>Audited as the authenticated caller, in the same unit of work as the change</b>, for the reason
/// <see cref="SetCompatibilityPolicyHandler"/> spells out: the command carries no actor, and the row
/// cannot outlive a change that rolled back. The entry names the previous and the new set, because
/// "who could decide this, and who made it so" is the question the row exists to answer.
/// </para>
/// <para>
/// <b>A set that is the one already held writes nothing.</b> Not saved, not recorded, as an idempotent
/// registration and a policy set to the policy it has already behave.
/// </para>
/// <para>
/// <b>Nobody is notified.</b> Decided, not omitted (ADR-043): a new notification event is a new
/// vocabulary and, because an empty event filter means every event, a message to every subscription
/// that has one; the audit trail is where this is looked up.
/// </para>
/// </remarks>
public sealed class SetSubjectReviewersHandler(
    ISubjectRepository subjects,
    ICallerContext caller,
    IAuditLog audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
    : ICommandHandler<SetSubjectReviewersCommand, Subject>
{
    /// <inheritdoc />
    public async Task<Result<Subject>> HandleAsync(
        SetSubjectReviewersCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var subject = await UpdateSubjectHandler
            .Find(subjects, command.EnvironmentId, command.SubjectName, cancellationToken)
            .ConfigureAwait(false);

        if (subject.IsFailure)
        {
            return subject;
        }

        var parsed = new List<ActorId>(command.Reviewers.Count);

        for (var i = 0; i < command.Reviewers.Count; i++)
        {
            var actor = ActorId.Create(command.Reviewers[i]);

            if (actor.IsFailure)
            {
                // Which entry, so a form with twenty lines can say which one is wrong. The domain's
                // own message ("An actor id is required.") would not.
                return Result<Subject>.Failure(
                    ConcordatCodes.ReviewersInvalid,
                    $"Reviewer {i + 1} is not an actor id: {actor.Error!.Message}");
            }

            parsed.Add(actor.Value);
        }

        var previous = subject.Value.Reviewers;

        var applied = subject.Value.SetReviewers(parsed);
        if (applied.IsFailure)
        {
            return Result<Subject>.Failure(applied.Error!);
        }

        if (!applied.Value)
        {
            return subject;
        }

        // Both sets as stored (the new one after the aggregate's own de-duplication). A reviewer is
        // not a secret: the set is on the subject any reader can open.
        audit.Append(AuditEntry.Record(
            command.EnvironmentId,
            AuditAction.SubjectReviewersChanged,
            caller.Current.Actor,
            subject.Value.Name.Value,
            clock.GetUtcNow(),
            $"reviewers {Describe(previous)} -> {Describe(subject.Value.Reviewers)}"));

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return subject;
    }

    private static string Describe(IReadOnlyList<ActorId> reviewers) =>
        reviewers.Count == 0 ? "anyone with the scope" : string.Join(", ", reviewers.Select(r => r.Value));
}
