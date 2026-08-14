using Concordat.Application.Abstractions;
using Concordat.Application.Governance;
using Concordat.Application.Registry;
using Concordat.Domain.Governance;
using Concordat.Domain.Registry;

namespace Concordat.Api;

/// <summary>M7.4's governance routes: services, impact, promotion and the audit trail.</summary>
public static class GovernanceEndpoints
{
    /// <summary>Maps the governance routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapGovernanceEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var services = app.MapGroup("/v1/environments/{env}/services").WithTags("Governance");

        services.MapPost("/", RegisterService)
            .WithSummary("Declare a service's producer and consumer intent")
            .WithDescription(
                "Called by an SDK at startup and by CI on deploy. An upsert keyed on the " +
                "service name: fifty pods reporting the same intent are one row, because the " +
                "alternative reports fifty affected consumers where there is one.")
            .Produces<ServiceResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        services.MapGet("/", ListServices)
            .WithSummary("List the services registered in an environment")
            .Produces<IReadOnlyList<ServiceResponse>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        services.MapGet("/{service}", GetService)
            .WithSummary("Read one service registration")
            .Produces<ServiceResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        var subjects = app.MapGroup("/v1/environments/{env}/subjects/{subject}")
            .WithTags("Governance");

        subjects.MapGet("/impact", ImpactOfRegistered)
            .WithSummary("Who breaks if this subject changes")
            .WithDescription(
                "Analyses a version already in the registry. The check runs FORWARD — can a " +
                "consumer still holding its declared version read data written under this one " +
                "— which is the opposite of the direction registration asks.")
            .Produces<ImpactResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        subjects.MapPost("/impact", ImpactOfCandidate)
            .WithSummary("Who breaks if I register this schema")
            .WithDescription(
                "The form CI calls before pushing: the schema is not registered, and nothing " +
                "is written by asking.")
            .Produces<ImpactResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        subjects.MapPost("/promote", Promote)
            .WithSummary("Promote a version into another environment")
            .WithDescription(
                "Re-checks the schema against the target environment's history under the " +
                "target's policy — a version compatible in dev says nothing about prod. The " +
                "schema id does not change, so an in-flight envelope stays resolvable.")
            .Produces<PromotionResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        app.MapGet("/v1/audit", Audit)
            .WithTags("Governance")
            .WithSummary("Read the audit trail, newest first")
            .WithDescription(
                "Records what happened, in the same transaction as the change itself. A " +
                "refused request never reaches a commit, so it leaves no entry.")
            .Produces<IReadOnlyList<AuditResponse>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> RegisterService(
        string env,
        RegisterServiceRequest request,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await dispatcher.SendAsync(
            new RegisterServiceCommand(
                env, request.Name, request.Produces, request.Consumes, request.ReportedBy),
            cancellationToken).ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Ok(ServiceResponse.From(result.Value));
    }

    private static async Task<IResult> ListServices(
        string env, IDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(new ListServicesQuery(env), cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Ok(result.Value.Select(ServiceResponse.From).ToList());
    }

    private static async Task<IResult> GetService(
        string env, string service, IDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(
            new GetServiceQuery(env, service), cancellationToken).ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Ok(ServiceResponse.From(result.Value));
    }

    private static Task<IResult> ImpactOfRegistered(
        string env,
        string subject,
        int? version,
        IDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        AnalyseAsync(
            new AnalyseImpactQuery(env, subject, null, version), dispatcher, cancellationToken);

    private static Task<IResult> ImpactOfCandidate(
        string env,
        string subject,
        ImpactRequest request,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return AnalyseAsync(
            new AnalyseImpactQuery(env, subject, request.Schema, request.Version),
            dispatcher,
            cancellationToken);
    }

    private static async Task<IResult> AnalyseAsync(
        AnalyseImpactQuery query, IDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(query, cancellationToken).ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Ok(ImpactResponse.From(result.Value));
    }

    private static async Task<IResult> Promote(
        string env,
        string subject,
        PromoteRequest request,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await dispatcher.SendAsync(
            new PromoteVersionCommand(
                env, request.ToEnvironment, subject, request.Version, request.PromotedBy),
            cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            return ProblemDetailsMapping.From(result.Error!);
        }

        var promoted = result.Value;

        return TypedResults.Created(
            $"/v1/environments/{promoted.ToEnvironment}/subjects/{promoted.Subject}" +
            $"/versions/{promoted.TargetOrdinal}",
            PromotionResponse.From(promoted));
    }

    private static async Task<IResult> Audit(
        string? env,
        string? action,
        string? target,
        DateTimeOffset? since,
        DateTimeOffset? until,
        int? limit,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(
            new QueryAuditQuery(env, action, target, since, until, limit),
            cancellationToken).ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Ok(result.Value.Select(AuditResponse.From).ToList());
    }
}

// ------------------------------------------------------------------------- requests

/// <summary>Request to declare a service's intent.</summary>
/// <param name="Name">The service name, unique within the environment.</param>
/// <param name="Produces">The subjects it publishes.</param>
/// <param name="Consumes">The subjects it reads, and at which versions.</param>
/// <param name="ReportedBy">Who reported it — the SDK, or a CI job.</param>
public sealed record RegisterServiceRequest(
    string Name,
    IReadOnlyList<SubjectRefInput>? Produces = null,
    IReadOnlyList<SubjectRefInput>? Consumes = null,
    string? ReportedBy = null);

/// <summary>Request to analyse an unregistered candidate.</summary>
/// <param name="Schema">The schema as authored. Nothing is written by asking.</param>
/// <param name="Version">A registered version to analyse instead, when no schema is supplied.</param>
public sealed record ImpactRequest(string? Schema = null, int? Version = null);

/// <summary>Request to promote a version.</summary>
/// <param name="ToEnvironment">Where it is going.</param>
/// <param name="Version">Which version, or omit for the source's latest.</param>
/// <param name="PromotedBy">Who is promoting.</param>
public sealed record PromoteRequest(
    string ToEnvironment, int? Version = null, string? PromotedBy = null);

// ------------------------------------------------------------------------ responses

/// <summary>A service's declared intent.</summary>
/// <param name="Name">The service name.</param>
/// <param name="Produces">The subjects it publishes.</param>
/// <param name="Consumes">The subjects it reads.</param>
/// <param name="FirstSeenAt">When it first declared itself.</param>
/// <param name="LastSeenAt">When it last declared itself.</param>
/// <param name="Stale">Whether nothing has been heard from it in a long time.</param>
public sealed record ServiceResponse(
    string Name,
    IReadOnlyList<SubjectRefResponse> Produces,
    IReadOnlyList<SubjectRefResponse> Consumes,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    bool Stale)
{
    /// <summary>Projects a registration.</summary>
    /// <param name="service">The registration.</param>
    /// <returns>The wire form.</returns>
    public static ServiceResponse From(ServiceRegistration service)
    {
        ArgumentNullException.ThrowIfNull(service);

        return new ServiceResponse(
            service.Name,
            [.. service.Produces.Select(SubjectRefResponse.From)],
            [.. service.Consumes.Select(SubjectRefResponse.From)],
            service.FirstSeenAt,
            service.LastSeenAt,
            service.IsStale(DateTimeOffset.UtcNow));
    }
}

/// <summary>What a change does to one registered consumer.</summary>
/// <param name="Service">The service name.</param>
/// <param name="Selector">What it declared.</param>
/// <param name="ReaderOrdinal">The version its verdict was computed against.</param>
/// <param name="Breaks">Whether the change stops it reading new messages.</param>
/// <param name="Certainty"><c>CHECKED</c>, <c>FOLLOWS_LATEST</c> or <c>UNRESOLVABLE</c>.</param>
/// <param name="LastSeenAt">When it last declared itself.</param>
/// <param name="Stale">Whether nothing has been heard from it in a long time.</param>
/// <param name="Reasons">The divergences that break it.</param>
public sealed record ConsumerImpactResponse(
    string Service,
    string Selector,
    int? ReaderOrdinal,
    bool Breaks,
    string Certainty,
    DateTimeOffset LastSeenAt,
    bool Stale,
    IReadOnlyList<string> Reasons);

/// <summary>The impact of a change.</summary>
/// <param name="Subject">The subject analysed.</param>
/// <param name="CandidateOrdinal">The version analysed, or null for an unregistered candidate.</param>
/// <param name="CandidateSchemaId">The candidate's content-addressed id.</param>
/// <param name="BreakingCount">How many registered consumers break.</param>
/// <param name="Consumers">Every registered consumer, breaking ones first.</param>
/// <param name="Contracts">The contracts whose bindings carry this subject.</param>
public sealed record ImpactResponse(
    string Subject,
    int? CandidateOrdinal,
    string? CandidateSchemaId,
    int BreakingCount,
    IReadOnlyList<ConsumerImpactResponse> Consumers,
    IReadOnlyList<string> Contracts)
{
    /// <summary>Projects a report.</summary>
    /// <param name="report">The report.</param>
    /// <returns>The wire form.</returns>
    public static ImpactResponse From(ImpactReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return new ImpactResponse(
            report.Subject,
            report.CandidateOrdinal,
            report.CandidateSchemaId,
            report.BreakingCount,
            [.. report.Consumers.Select(c => new ConsumerImpactResponse(
                c.Service,
                c.Selector,
                c.ReaderOrdinal,
                c.Breaks,
                Certainty(c.Certainty),
                c.LastSeenAt,
                c.Stale,
                c.Reasons))],
            report.Contracts);
    }

    // Explicit, not the uppercased member name -- the class of bug M6.1 found across the API.
    private static string Certainty(ImpactCertainty certainty) => certainty switch
    {
        ImpactCertainty.Checked => "CHECKED",
        ImpactCertainty.FollowsLatest => "FOLLOWS_LATEST",
        ImpactCertainty.Unresolvable => "UNRESOLVABLE",
        _ => throw new ArgumentOutOfRangeException(nameof(certainty), certainty, null),
    };
}

/// <summary>What a promotion produced.</summary>
/// <param name="Subject">The subject.</param>
/// <param name="FromEnvironment">Where it came from.</param>
/// <param name="ToEnvironment">Where it went.</param>
/// <param name="SourceOrdinal">The version promoted.</param>
/// <param name="TargetOrdinal">The ordinal it landed on.</param>
/// <param name="SchemaId">The content-addressed id, identical in both environments.</param>
/// <param name="Status"><c>ACTIVE</c>, or <c>AWAITING_APPROVAL</c> when the target disagrees.</param>
/// <param name="Created">False when the target already had this schema at its tip.</param>
/// <param name="SubjectCreated">Whether the target subject was created by this promotion.</param>
/// <param name="Divergences">
/// Everything the target's engine found, including divergences its policy tolerated — the same
/// shape registration returns, because a promotion is a registration.
/// </param>
public sealed record PromotionResponse(
    string Subject,
    string FromEnvironment,
    string ToEnvironment,
    int SourceOrdinal,
    int TargetOrdinal,
    string SchemaId,
    string Status,
    bool Created,
    bool SubjectCreated,
    IReadOnlyList<BreakingChangeResponse> Divergences)
{
    /// <summary>Projects a promotion.</summary>
    /// <param name="result">The result.</param>
    /// <returns>The wire form.</returns>
    public static PromotionResponse From(PromotionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new PromotionResponse(
            result.Subject,
            result.FromEnvironment,
            result.ToEnvironment,
            result.SourceOrdinal,
            result.TargetOrdinal,
            result.SchemaId,
            WireTokens.For(result.Status),
            result.Created,
            result.SubjectCreated,
            [.. result.Report.AllDivergences.Select(BreakingChangeResponse.From)]);
    }
}

/// <summary>One line of the audit trail.</summary>
/// <param name="At">When, in UTC.</param>
/// <param name="Action">The stable action token.</param>
/// <param name="Actor">Who did it.</param>
/// <param name="Target">What it happened to.</param>
/// <param name="Detail">One line of context, or null.</param>
public sealed record AuditResponse(
    DateTimeOffset At, string Action, string Actor, string Target, string? Detail)
{
    /// <summary>Projects an entry.</summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The wire form.</returns>
    public static AuditResponse From(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new AuditResponse(
            entry.At,
            AuditTokens.For(entry.Action),
            entry.Actor.Value,
            entry.Target,
            entry.Detail);
    }
}
