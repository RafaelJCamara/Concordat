using Concordat.Application.Abstractions;
using Concordat.Domain.Governance;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;

namespace Concordat.Application.Registry;

/// <summary>Deletes a retired subject, and every version it held, for good (ADR-046).</summary>
/// <param name="EnvironmentId">The environment.</param>
/// <param name="SubjectName">The subject.</param>
/// <param name="Force">Whether the caller said they mean it. Anything but <see langword="true"/> is refused.</param>
public sealed record PurgeSubjectCommand(EnvironmentId EnvironmentId, string SubjectName, bool Force)
    : ICommand<PurgedSubject>;

/// <summary>What a deletion removed.</summary>
/// <param name="Name">The subject's name, which is free to be registered again.</param>
/// <param name="VersionsRemoved">How many versions went with it.</param>
public sealed record PurgedSubject(string Name, int VersionsRemoved);

/// <summary>Handles <see cref="PurgeSubjectCommand"/>.</summary>
/// <remarks>
/// <para>
/// <b>The DESIGN §4 rule, in the order that tells a caller the most useful thing first and costs the
/// least before it does:</b> the flag (nothing is read), the subject (404), that it is retired (the
/// aggregate's rule), that no contract binds it and no registered service consumes it, and that no
/// schema another subject's version uses refers to it. The first refusal is the one reported.
/// </para>
/// <para>
/// <b>Schemas are not deleted</b> (ADR-015): they are content-addressed and global, and another subject, in
/// this tenant or another, may hold the same bytes. What goes is the subject row and its versions. A
/// schema that was reachable only through this subject is unreachable afterwards, which is what a
/// consumer holding its id will see (<c>404 schema_not_found</c>, as for any id the caller's tenant does
/// not reach); the ADR says so, and so does the screen that asks for the confirmation.
/// </para>
/// <para>
/// <b>Audited as the authenticated caller, in the same unit of work</b>, and the entry survives the
/// subject: it names the versions removed, because it is the only record left of them.
/// </para>
/// <para>
/// <b>Check-then-delete, not a lock.</b> A binding added between the checks and the commit would point at
/// a subject that is no longer there. Adding a binding resolves its subjects first, so the window is the
/// length of this request, on a subject that has been retired (which refuses new versions) and that no one
/// is meant to be binding. Closing it needs a lock across two aggregates and is not built.
/// </para>
/// </remarks>
public sealed class PurgeSubjectHandler(
    ISubjectRepository subjects,
    IContractRepository contracts,
    IServiceRegistrationRepository services,
    IEnvironmentResolver environments,
    ICallerContext caller,
    IAuditLog audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
    : ICommandHandler<PurgeSubjectCommand, PurgedSubject>
{
    /// <summary>How many names a refusal lists before it only counts the rest.</summary>
    internal const int NamesListed = 5;

    /// <inheritdoc />
    public async Task<Result<PurgedSubject>> HandleAsync(
        PurgeSubjectCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!command.Force)
        {
            return Result<PurgedSubject>.Failure(
                ConcordatCodes.PurgeRequiresForce,
                "Deleting a subject for good cannot be undone. Send force=true to say you mean it.");
        }

        var found = await UpdateSubjectHandler
            .Find(subjects, command.EnvironmentId, command.SubjectName, cancellationToken)
            .ConfigureAwait(false);

        if (found.IsFailure)
        {
            return Result<PurgedSubject>.Failure(found.Error!);
        }

        var subject = found.Value;

        var purgeable = subject.CheckPurgeable();
        if (purgeable.IsFailure)
        {
            return Result<PurgedSubject>.Failure(purgeable.Error!);
        }

        var users = await NamesUsing(subject.Name, command.EnvironmentId, cancellationToken)
            .ConfigureAwait(false);

        if (users.Count > 0)
        {
            return Result<PurgedSubject>.Failure(
                ConcordatCodes.SubjectInUse,
                $"Subject '{subject.Name}' is still in use by {Describe(users)}. Remove it from them first.");
        }

        var referrers = await ReferrersAsync(subject.Name, command.EnvironmentId, cancellationToken)
            .ConfigureAwait(false);

        if (referrers.Count > 0)
        {
            return Result<PurgedSubject>.Failure(
                ConcordatCodes.SubjectReferenced,
                $"A schema used by {Describe(referrers)} refers to subject '{subject.Name}'. " +
                "Delete or replace those first.");
        }

        var removed = subject.Versions.Count;

        subjects.Remove(subject);

        audit.Append(AuditEntry.Record(
            command.EnvironmentId,
            AuditAction.SubjectPurged,
            caller.Current.Actor,
            subject.Name.Value,
            clock.GetUtcNow(),
            $"{removed} {(removed == 1 ? "version" : "versions")} removed"));

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result<PurgedSubject>.Success(new PurgedSubject(subject.Name.Value, removed));
    }

    /// <summary>The contracts that bind the subject and the services that consume it, as labelled names.</summary>
    private async Task<IReadOnlyList<string>> NamesUsing(
        SubjectName subject, EnvironmentId environmentId, CancellationToken cancellationToken)
    {
        var users = new List<string>();

        foreach (var contract in await contracts.ListAsync(environmentId, cancellationToken).ConfigureAwait(false))
        {
            if (contract.Publishes.Any(p => p.Subjects.Any(r => r.Subject == subject))
                || contract.Consumes.Any(c => c.Subjects.Any(r => r.Subject == subject)))
            {
                users.Add($"contract '{contract.Name}'");
            }
        }

        foreach (var service in await services.ListAsync(environmentId, cancellationToken).ConfigureAwait(false))
        {
            if (service.Consumes.Any(r => r.Subject == subject) || service.Produces.Any(r => r.Subject == subject))
            {
                users.Add($"service '{service.Name}'");
            }
        }

        return users;
    }

    /// <summary>
    /// The versions whose schema refers to <em>this</em> subject, in this environment, wherever the version lives.
    /// </summary>
    /// <remarks>
    /// A reference carries an environment (<c>concordat://prod/acme.Order/1</c>), so a version in another
    /// environment can hold one, and a subject of the same name in another environment is not what it
    /// names. The repository answers by name; which environment a reference means is decided here, with the
    /// resolver every other reader of a reference uses (<see cref="ReferenceIntegrity.ResolveAsync"/>).
    /// </remarks>
    private async Task<IReadOnlyList<string>> ReferrersAsync(
        SubjectName subject, EnvironmentId environmentId, CancellationToken cancellationToken)
    {
        var candidates = await subjects.FindReferrersAsync(subject, cancellationToken).ConfigureAwait(false);

        return
        [
            .. candidates
                .Where(c => !(c.Environment == environmentId && c.Subject == subject))
                .Where(c => c.References.Any(r => Names(r, subject, environmentId)))
                .Select(c => c.Environment == environmentId
                    ? $"{c.Subject}@{c.Version}"
                    : $"{c.Subject}@{c.Version} (in another environment)"),
        ];
    }

    private bool Names(string reference, SubjectName subject, EnvironmentId environmentId)
    {
        var parsed = ConcordatRef.Create(reference);

        return parsed.IsSuccess
            && parsed.Value.Subject == subject
            && environments.Resolve(parsed.Value.Environment) == environmentId;
    }

    private static string Describe(IReadOnlyList<string> names) =>
        names.Count <= NamesListed
            ? string.Join(", ", names)
            : $"{string.Join(", ", names.Take(NamesListed))} and {names.Count - NamesListed} more";
}
