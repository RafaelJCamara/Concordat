using Concordat.Application.Abstractions;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Concordat.Formats.Abstractions;

namespace Concordat.Application.Registry;

/// <summary>Creates a subject.</summary>
/// <param name="EnvironmentId">The environment.</param>
/// <param name="Name">The subject name.</param>
/// <param name="Format">The schema language, fixed for the subject's life.</param>
/// <param name="Owner">Who owns the contract.</param>
/// <param name="CompatibilityPolicy">An explicit policy, or null to inherit the environment default.</param>
/// <param name="ContentModel">Whether unknown properties are permitted.</param>
public sealed record CreateSubjectCommand(
    EnvironmentId EnvironmentId,
    string Name,
    SchemaFormat Format,
    string Owner,
    CompatibilityPolicy? CompatibilityPolicy,
    ContentModel ContentModel) : ICommand<Subject>;

/// <summary>Handles <see cref="CreateSubjectCommand"/>.</summary>
public sealed class CreateSubjectHandler(
    ISubjectRepository subjects, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<CreateSubjectCommand, Subject>
{
    /// <inheritdoc />
    public async Task<Result<Subject>> HandleAsync(
        CreateSubjectCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var name = SubjectName.Create(command.Name);
        if (name.IsFailure)
        {
            return Result<Subject>.Failure(name.Error!);
        }

        var owner = ActorId.Create(command.Owner);
        if (owner.IsFailure)
        {
            return Result<Subject>.Failure(owner.Error!);
        }

        var existing = await subjects.FindAsync(command.EnvironmentId, name.Value, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            return Result<Subject>.Failure(
                ConcordatCodes.SubjectAlreadyExists,
                $"Subject '{name.Value}' already exists in this environment.");
        }

        var subject = Subject.Create(
            command.EnvironmentId,
            name.Value,
            command.Format,
            owner.Value,
            command.CompatibilityPolicy,
            clock.GetUtcNow(),
            command.ContentModel);

        if (subject.IsFailure)
        {
            return subject;
        }

        subjects.Add(subject.Value);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return subject;
    }
}

/// <summary>Fetches one subject with its versions.</summary>
/// <param name="EnvironmentId">The environment.</param>
/// <param name="Name">The subject name.</param>
public sealed record GetSubjectQuery(EnvironmentId EnvironmentId, string Name) : IQuery<Subject>;

/// <summary>Handles <see cref="GetSubjectQuery"/>.</summary>
public sealed class GetSubjectHandler(ISubjectRepository subjects)
    : IQueryHandler<GetSubjectQuery, Subject>
{
    /// <inheritdoc />
    public async Task<Result<Subject>> HandleAsync(
        GetSubjectQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var name = SubjectName.Create(query.Name);
        if (name.IsFailure)
        {
            return Result<Subject>.Failure(name.Error!);
        }

        var subject = await subjects.FindAsync(query.EnvironmentId, name.Value, cancellationToken)
            .ConfigureAwait(false);

        return subject is null
            ? Result<Subject>.Failure(
                ConcordatCodes.SubjectNotFound,
                $"No subject '{name.Value}' in this environment.")
            : Result<Subject>.Success(subject);
    }
}

/// <summary>Lists the subjects in an environment.</summary>
/// <param name="EnvironmentId">The environment.</param>
public sealed record ListSubjectsQuery(EnvironmentId EnvironmentId)
    : IQuery<IReadOnlyList<Subject>>;

/// <summary>Handles <see cref="ListSubjectsQuery"/>.</summary>
public sealed class ListSubjectsHandler(ISubjectRepository subjects)
    : IQueryHandler<ListSubjectsQuery, IReadOnlyList<Subject>>
{
    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<Subject>>> HandleAsync(
        ListSubjectsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var found = await subjects.ListAsync(query.EnvironmentId, cancellationToken)
            .ConfigureAwait(false);

        return Result<IReadOnlyList<Subject>>.Success(found);
    }
}

/// <summary>Checks a proposed schema without writing anything.</summary>
/// <param name="EnvironmentId">The environment.</param>
/// <param name="SubjectName">The subject.</param>
/// <param name="Body">The proposed schema.</param>
public sealed record CheckCompatibilityQuery(
    EnvironmentId EnvironmentId, string SubjectName, string? Body)
    : IQuery<CompatibilityCheckResult>;

/// <summary>The dry-run answer.</summary>
/// <param name="Compatible">Whether the proposal satisfies the policy.</param>
/// <param name="SchemaId">The id the proposal would receive.</param>
/// <param name="Policy">The policy it was evaluated under.</param>
/// <param name="Report">Findings, including ones the policy tolerated.</param>
/// <param name="SuggestedSemver">The label the change warrants, given the current tip.</param>
public sealed record CompatibilityCheckResult(
    bool Compatible,
    string SchemaId,
    CompatibilityPolicy Policy,
    CompatibilityReport Report,
    string? SuggestedSemver);

/// <summary>Handles <see cref="CheckCompatibilityQuery"/>.</summary>
/// <remarks>Never writes. A dry run that persisted anything would be a trap in CI.</remarks>
public sealed class CheckCompatibilityHandler(
    ISubjectRepository subjects, ISchemaRepository schemas, ICompatibilityEvaluator evaluator)
    : IQueryHandler<CheckCompatibilityQuery, CompatibilityCheckResult>
{
    /// <inheritdoc />
    public async Task<Result<CompatibilityCheckResult>> HandleAsync(
        CheckCompatibilityQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var name = SubjectName.Create(query.SubjectName);
        if (name.IsFailure)
        {
            return Result<CompatibilityCheckResult>.Failure(name.Error!);
        }

        var subject = await subjects.FindAsync(query.EnvironmentId, name.Value, cancellationToken)
            .ConfigureAwait(false);

        if (subject is null)
        {
            return Result<CompatibilityCheckResult>.Failure(
                ConcordatCodes.SubjectNotFound,
                $"No subject '{name.Value}' in this environment.");
        }

        var priors = new List<PriorSchema>();
        foreach (var version in subject.Versions.Where(v => v.Status is not VersionStatus.Rejected))
        {
            var schema = await schemas.FindAsync(version.SchemaId, cancellationToken)
                .ConfigureAwait(false);
            if (schema is not null)
            {
                priors.Add(new PriorSchema(version.Ordinal, schema.Body));
            }
        }

        var policy = subject.EffectivePolicy(CompatibilityPolicy.Default);
        var evaluated = evaluator.Evaluate(
            subject.Format, query.Body, priors, policy, subject.ContentModel);

        if (evaluated.IsFailure)
        {
            return Result<CompatibilityCheckResult>.Failure(evaluated.Error!);
        }

        return Result<CompatibilityCheckResult>.Success(new CompatibilityCheckResult(
            evaluated.Value.Report.IsCompatible,
            evaluated.Value.Schema.Id.Value,
            policy,
            evaluated.Value.Report,
            SuggestSemver(subject, evaluated.Value.Report.SuggestedBump)));
    }

    private static string? SuggestSemver(Subject subject, SemverBump bump)
    {
        var latest = subject.Versions
            .Where(v => v.SemanticVersion is not null && v.Status is not VersionStatus.Rejected)
            .Select(v => v.SemanticVersion!.Value)
            .DefaultIfEmpty()
            .Max();

        if (latest == default)
        {
            return bump is SemverBump.None ? null : "1.0.0";
        }

        return bump switch
        {
            SemverBump.Major => new SemanticVersion(latest.Major + 1, 0, 0).ToString(),
            SemverBump.Minor => new SemanticVersion(latest.Major, latest.Minor + 1, 0).ToString(),
            SemverBump.Patch => new SemanticVersion(latest.Major, latest.Minor, latest.Patch + 1).ToString(),
            _ => null,
        };
    }
}
