using Concordat.Application.Abstractions;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Concordat.Formats.Abstractions;

namespace Concordat.Application.Registry;

/// <summary>Registers a new version of a subject.</summary>
/// <param name="EnvironmentId">The environment.</param>
/// <param name="SubjectName">The subject.</param>
/// <param name="Body">The schema as authored.</param>
/// <param name="SemanticVersion">An optional intent label, verified against the verdict.</param>
/// <param name="Changelog">An optional note.</param>
/// <param name="RegisteredBy">Who is registering.</param>
public sealed record RegisterVersionCommand(
    EnvironmentId EnvironmentId,
    string SubjectName,
    string? Body,
    string? SemanticVersion,
    string? Changelog,
    string RegisteredBy) : ICommand<RegisterVersionResult>;

/// <summary>What registration produced.</summary>
/// <param name="SubjectName">The subject.</param>
/// <param name="Ordinal">The version ordinal.</param>
/// <param name="SchemaId">The content-addressed schema id.</param>
/// <param name="Status">Whether the version is active or awaiting approval.</param>
/// <param name="Created">
/// <see langword="false"/> when the schema was already at the tip and no ordinal was allocated.
/// </param>
/// <param name="Report">The engine's findings, including ones the policy tolerated.</param>
/// <param name="Portability">
/// Where the registered schema relies on behaviour that differs between SDKs (M6.1). Never a
/// reason the registration failed — those refuse before this record exists — but the moment the
/// author is most likely to act on it.
/// </param>
public sealed record RegisterVersionResult(
    string SubjectName,
    int Ordinal,
    string SchemaId,
    VersionStatus Status,
    bool Created,
    CompatibilityReport Report,
    IReadOnlyList<PortabilityFinding> Portability);

/// <summary>Handles <see cref="RegisterVersionCommand"/>.</summary>
public sealed class RegisterVersionHandler(
    ISubjectRepository subjects,
    ISchemaRepository schemas,
    ICompatibilityEvaluator evaluator,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
    : ICommandHandler<RegisterVersionCommand, RegisterVersionResult>
{
    /// <inheritdoc />
    public async Task<Result<RegisterVersionResult>> HandleAsync(
        RegisterVersionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var name = SubjectName.Create(command.SubjectName);
        if (name.IsFailure)
        {
            return Result<RegisterVersionResult>.Failure(name.Error!);
        }

        var actor = ActorId.Create(command.RegisteredBy);
        if (actor.IsFailure)
        {
            return Result<RegisterVersionResult>.Failure(actor.Error!);
        }

        var subject = await subjects.FindAsync(command.EnvironmentId, name.Value, cancellationToken)
            .ConfigureAwait(false);

        if (subject is null)
        {
            return Result<RegisterVersionResult>.Failure(
                ConcordatCodes.SubjectNotFound,
                $"No subject '{name.Value}' in this environment. Create it first.");
        }

        SemanticVersion? semver = null;
        if (!string.IsNullOrWhiteSpace(command.SemanticVersion))
        {
            var parsed = SemanticVersion.Create(command.SemanticVersion);
            if (parsed.IsFailure)
            {
                return Result<RegisterVersionResult>.Failure(parsed.Error!);
            }

            semver = parsed.Value;
        }

        var priors = await LoadPriorsAsync(subject, cancellationToken).ConfigureAwait(false);
        var policy = subject.EffectivePolicy(CompatibilityPolicy.Default);

        var evaluated = evaluator.Evaluate(
            subject.Format, command.Body, priors, policy, subject.ContentModel);

        if (evaluated.IsFailure)
        {
            return Result<RegisterVersionResult>.Failure(evaluated.Error!);
        }

        var stored = await schemas.AddIfMissingAsync(evaluated.Value.Schema, cancellationToken)
            .ConfigureAwait(false);

        var registered = subject.RegisterVersion(
            stored,
            evaluated.Value.Verdict,
            semver,
            command.Changelog,
            actor.Value,
            clock.GetUtcNow());

        if (registered.IsFailure)
        {
            return Result<RegisterVersionResult>.Failure(registered.Error!);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var version = registered.Value.Version;
        return Result<RegisterVersionResult>.Success(new RegisterVersionResult(
            subject.Name.Value,
            version.Ordinal,
            version.SchemaId.Value,
            version.Status,
            registered.Value.Created,
            evaluated.Value.Report,
            evaluated.Value.Portability));
    }

    private async Task<IReadOnlyList<PriorSchema>> LoadPriorsAsync(
        Subject subject, CancellationToken cancellationToken)
    {
        var priors = new List<PriorSchema>();

        foreach (var version in CompatibilityHistory.Of(subject))
        {
            var schema = await schemas.FindAsync(version.SchemaId, cancellationToken)
                .ConfigureAwait(false);

            if (schema is not null)
            {
                priors.Add(new PriorSchema(version.Ordinal, schema.Body));
            }
        }

        return priors;
    }
}
