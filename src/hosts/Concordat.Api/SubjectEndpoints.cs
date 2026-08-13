using Concordat.Application.Abstractions;
using Concordat.Application.Registry;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;

namespace Concordat.Api;

/// <summary>The subject and version routes under <c>/v1/environments/{env}</c>.</summary>
public static class SubjectEndpoints
{
    /// <summary>Maps the subject routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapSubjectEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/v1/environments/{env}/subjects")
            .WithTags("Subjects");

        group.MapPost("/", CreateSubject)
            .WithSummary("Create a subject")
            .WithDescription(
                "A subject must exist before any version can be registered. Creation is " +
                "explicit rather than implicit so a typo in a subject name fails loudly " +
                "instead of silently starting a second contract.")
            .Produces<SubjectResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/", ListSubjects)
            .WithSummary("List subjects in an environment")
            .Produces<IReadOnlyList<SubjectResponse>>();

        group.MapGet("/{subject}", GetSubject)
            .WithSummary("Get a subject and its versions")
            .Produces<SubjectResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{subject}/versions", ListVersions)
            .WithSummary("List versions")
            .Produces<IReadOnlyList<VersionResponse>>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapGet("/{subject}/versions/{ordinal}", GetVersion)
            .WithSummary("Get one version")
            .WithDescription(
                "The ordinal may be an integer or the literal 'latest'. 'latest' resolves the " +
                "gated pointer (ADR-017), not the highest ordinal, so a version awaiting " +
                "approval is never returned.")
            .Produces<VersionResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{subject}/versions", RegisterVersion)
            .WithSummary("Register a version")
            .WithDescription(
                "A breaking change succeeds with status AWAITING_APPROVAL and does not move " +
                "the latest pointer, so CI never wedges and the proposal stays reviewable.")
            // 200 and 201 are both success and mean different things: 200 is the idempotent
            // re-registration of the tip, where no ordinal was allocated. A client that treats
            // them alike will double-count versions.
            .Produces<RegisterVersionResponse>(StatusCodes.Status201Created)
            .Produces<RegisterVersionResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{subject}/versions/{ordinal:int}/approve", ApproveVersion)
            .WithSummary("Approve a pending version")
            .Produces<SubjectResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{subject}/versions/{ordinal:int}/reject", RejectVersion)
            .WithSummary("Reject a pending version")
            .Produces<SubjectResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{subject}/compatibility", CheckCompatibility)
            .WithSummary("Check a schema without registering it")
            .WithDescription("A dry run. Never writes.")
            .Produces<CompatibilityResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{subject}/versions/{from:int}/diff/{to:int}", DiffVersions)
            .WithSummary("Compare two versions")
            .WithDescription(
                "Reports every difference, not only policy violations. A diff answers 'what " +
                "changed'; filtering it by the current policy would hide differences that " +
                "matter to a consumer generating code.")
            .Produces<DiffResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPatch("/{subject}", UpdateSubject)
            .WithSummary("Change a subject's owner, or deprecate it")
            .WithDescription(
                "Deprecation is advisory: a deprecated subject still accepts versions, because " +
                "existing producers need to be able to patch their contract.")
            .Produces<SubjectResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/{subject}", RetireSubject)
            .WithSummary("Retire a subject")
            .WithDescription(
                "The soft delete, and terminal. There is no hard delete: schemas are never " +
                "removed (ADR-015), and removing a subject needs the registered-consumer " +
                "check and audit entry that arrive in M7.")
            .Produces<SubjectResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{subject}/compatibility-policy", GetPolicy)
            .WithSummary("Get the compatibility policy")
            .Produces<PolicyResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/{subject}/compatibility-policy", SetPolicy)
            .WithSummary("Set the compatibility policy")
            .WithDescription("Send nulls to clear it and inherit the environment default.")
            .Produces<PolicyResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> CreateSubject(
        string env,
        CreateSubjectRequest request,
        IDispatcher dispatcher,
        IEnvironmentResolver environments,
        CancellationToken cancellationToken)
    {
        if (!TryParseFormat(request.Format, out var format))
        {
            return Invalid($"Unknown format '{request.Format}'. Expected json, avro or protobuf.");
        }

        if (!TryParseContentModel(request.ContentModel, out var contentModel))
        {
            return Invalid($"Unknown content model '{request.ContentModel}'. Expected open or closed.");
        }

        var parsed = ParsePolicy(request.CompatibilityMode, request.CompatibilitySurface, out var policy);
        if (parsed.IsFailure)
        {
            return ProblemDetailsMapping.From(parsed.Error!);
        }

        var result = await dispatcher.SendAsync(
            new CreateSubjectCommand(
                environments.Resolve(env), request.Name, format, request.Owner,
                policy, contentModel),
            cancellationToken).ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Created(
                $"/v1/environments/{env}/subjects/{result.Value.Name.Value}",
                SubjectResponse.From(result.Value));
    }

    private static async Task<IResult> ListSubjects(
        string env,
        IDispatcher dispatcher,
        IEnvironmentResolver environments,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(
            new ListSubjectsQuery(environments.Resolve(env)), cancellationToken).ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Ok(result.Value.Select(SubjectResponse.From).ToList());
    }

    private static async Task<IResult> GetSubject(
        string env,
        string subject,
        IDispatcher dispatcher,
        IEnvironmentResolver environments,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(
            new GetSubjectQuery(environments.Resolve(env), subject), cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Ok(SubjectResponse.From(result.Value));
    }

    private static async Task<IResult> ListVersions(
        string env,
        string subject,
        IDispatcher dispatcher,
        IEnvironmentResolver environments,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(
            new GetSubjectQuery(environments.Resolve(env), subject), cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Ok(result.Value.Versions.Select(VersionResponse.From).ToList());
    }

    private static async Task<IResult> GetVersion(
        string env,
        string subject,
        string ordinal,
        IDispatcher dispatcher,
        IEnvironmentResolver environments,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(
            new GetSubjectQuery(environments.Resolve(env), subject), cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            return ProblemDetailsMapping.From(result.Error!);
        }

        var found = string.Equals(ordinal, "latest", StringComparison.OrdinalIgnoreCase)
            ? result.Value.LatestVersion()
            : int.TryParse(ordinal, out var n)
                ? result.Value.Versions.FirstOrDefault(v => v.Ordinal == n)
                : null;

        return found is null
            ? ProblemDetailsMapping.From(new DomainError(
                ConcordatCodes.VersionNotFound,
                $"No version '{ordinal}' on subject '{subject}'."))
            : TypedResults.Ok(VersionResponse.From(found));
    }

    private static async Task<IResult> RegisterVersion(
        string env,
        string subject,
        RegisterVersionRequest request,
        IDispatcher dispatcher,
        IEnvironmentResolver environments,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.SendAsync(
            new RegisterVersionCommand(
                environments.Resolve(env), subject, request.Schema,
                request.SemanticVersion, request.Changelog, request.RegisteredBy),
            cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            return ProblemDetailsMapping.From(result.Error!);
        }

        var value = result.Value;
        var body = new RegisterVersionResponse(
            value.SubjectName,
            value.Ordinal,
            value.SchemaId,
            value.Status.ToString().ToUpperInvariant() switch
            {
                "AWAITINGAPPROVAL" => "AWAITING_APPROVAL",
                var other => other,
            },
            value.Created,
            [.. value.Report.AllDivergences.Select(BreakingChangeResponse.From)],
            [.. value.Portability.Select(PortabilityResponse.From)]);

        // 200 rather than 201 when nothing was created: re-registering the tip is idempotent
        // and allocated no ordinal, so claiming creation would be a lie a retrying client acts on.
        return value.Created
            ? TypedResults.Created(
                $"/v1/environments/{env}/subjects/{subject}/versions/{value.Ordinal}", body)
            : TypedResults.Ok(body);
    }

    private static Task<IResult> ApproveVersion(
        string env, string subject, int ordinal, DecideVersionRequest request,
        IDispatcher dispatcher, IEnvironmentResolver environments,
        CancellationToken cancellationToken) =>
        Decide(env, subject, ordinal, request, approve: true, dispatcher, environments, cancellationToken);

    private static Task<IResult> RejectVersion(
        string env, string subject, int ordinal, DecideVersionRequest request,
        IDispatcher dispatcher, IEnvironmentResolver environments,
        CancellationToken cancellationToken) =>
        Decide(env, subject, ordinal, request, approve: false, dispatcher, environments, cancellationToken);

    private static async Task<IResult> Decide(
        string env, string subject, int ordinal, DecideVersionRequest request, bool approve,
        IDispatcher dispatcher, IEnvironmentResolver environments,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.SendAsync(
            new DecideVersionCommand(
                environments.Resolve(env), subject, ordinal, request.DecidedBy, approve),
            cancellationToken).ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Ok(SubjectResponse.From(result.Value));
    }

    private static async Task<IResult> CheckCompatibility(
        string env,
        string subject,
        CheckCompatibilityRequest request,
        IDispatcher dispatcher,
        IEnvironmentResolver environments,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(
            new CheckCompatibilityQuery(environments.Resolve(env), subject, request.Schema),
            cancellationToken).ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Ok(CompatibilityResponse.From(result.Value));
    }

    private static async Task<IResult> DiffVersions(
        string env, string subject, int from, int to,
        IDispatcher dispatcher, IEnvironmentResolver environments,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(
            new DiffVersionsQuery(environments.Resolve(env), subject, from, to), cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            return ProblemDetailsMapping.From(result.Error!);
        }

        var value = result.Value;
        return TypedResults.Ok(new DiffResponse(
            value.From,
            value.To,
            value.FromSchemaId,
            value.ToSchemaId,
            value.Identical,
            PolicyResponse.From(value.Policy),
            [.. value.Divergences.Select(BreakingChangeResponse.From)]));
    }

    private static async Task<IResult> UpdateSubject(
        string env, string subject, UpdateSubjectRequest request,
        IDispatcher dispatcher, IEnvironmentResolver environments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await dispatcher.SendAsync(
            new UpdateSubjectCommand(
                environments.Resolve(env), subject, request.Owner, request.Deprecate),
            cancellationToken).ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Ok(SubjectResponse.From(result.Value));
    }

    private static async Task<IResult> RetireSubject(
        string env, string subject,
        IDispatcher dispatcher, IEnvironmentResolver environments,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.SendAsync(
            new RetireSubjectCommand(environments.Resolve(env), subject), cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Ok(SubjectResponse.From(result.Value));
    }

    private static async Task<IResult> GetPolicy(
        string env, string subject, IDispatcher dispatcher, IEnvironmentResolver environments,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(
            new GetSubjectQuery(environments.Resolve(env), subject), cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Ok(PolicyResponse.From(result.Value.CompatibilityPolicy));
    }

    private static async Task<IResult> SetPolicy(
        string env,
        string subject,
        SetCompatibilityPolicyRequest request,
        IDispatcher dispatcher,
        IEnvironmentResolver environments,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken)
    {
        var parsed = ParsePolicy(request.Mode, request.Surface, out var policy);
        if (parsed.IsFailure)
        {
            return ProblemDetailsMapping.From(parsed.Error!);
        }

        var found = await dispatcher.QueryAsync(
            new GetSubjectQuery(environments.Resolve(env), subject), cancellationToken)
            .ConfigureAwait(false);

        if (found.IsFailure)
        {
            return ProblemDetailsMapping.From(found.Error!);
        }

        var applied = found.Value.SetCompatibilityPolicy(policy);
        if (applied.IsFailure)
        {
            return ProblemDetailsMapping.From(applied.Error!);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(PolicyResponse.From(found.Value.CompatibilityPolicy));
    }

    // --------------------------------------------------------------- parsing

    private static Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult Invalid(string message) =>
        ProblemDetailsMapping.From(new DomainError("invalid_request", message));

    private static bool TryParseFormat(string? value, out SchemaFormat format)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case WireTokens.FormatJson: format = SchemaFormat.Json; return true;
            case WireTokens.FormatAvro: format = SchemaFormat.Avro; return true;
            case WireTokens.FormatProtobuf: format = SchemaFormat.Protobuf; return true;
            default: format = default; return false;
        }
    }

    private static bool TryParseContentModel(string? value, out ContentModel model)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "open": model = ContentModel.Open; return true;
            case "closed": model = ContentModel.Closed; return true;
            default: model = default; return false;
        }
    }

    // Returns Result, not Result<CompatibilityPolicy?>: Result<T> is constrained to notnull so
    // that Value is never a silent null, and "no policy" is a legitimate success here.
    private static Result ParsePolicy(string? mode, string? surface, out CompatibilityPolicy? policy)
    {
        policy = null;

        if (string.IsNullOrWhiteSpace(mode) && string.IsNullOrWhiteSpace(surface))
        {
            // Both absent means "inherit", which is not the same as "the default" (M1.1).
            return Result.Success();
        }

        if (string.IsNullOrWhiteSpace(mode) || string.IsNullOrWhiteSpace(surface))
        {
            return Result.Failure(
                "invalid_request",
                "A compatibility policy needs both a mode and a surface, or neither to inherit.");
        }

        if (!Enum.TryParse<CompatibilityMode>(
                mode.Replace("_", "", StringComparison.Ordinal), true, out var parsedMode))
        {
            return Result.Failure("invalid_request", $"Unknown compatibility mode '{mode}'.");
        }

        if (!Enum.TryParse<CompatibilitySurface>(
                surface.Replace("_", "", StringComparison.Ordinal), true, out var parsedSurface))
        {
            return Result.Failure("invalid_request", $"Unknown compatibility surface '{surface}'.");
        }

        policy = new CompatibilityPolicy(parsedMode, parsedSurface);
        return Result.Success();
    }
}
