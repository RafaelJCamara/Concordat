using Concordat.Application.Abstractions;
using Concordat.Application.Registry;
using Concordat.Domain.Contracts;
using Concordat.Domain.Identity;
using Concordat.Domain.Registry;

namespace Concordat.Api;

/// <summary>The contract routes under <c>/v1/environments/{env}/contracts</c> (M7.3).</summary>
public static class ContractEndpoints
{
    /// <summary>Maps the contract routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapContractEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/v1/environments/{env}/contracts").WithTags("Contracts");

        group.MapGet("/", ListContracts)
            .WithSummary("List contracts")
            .Produces<IReadOnlyList<ContractResponse>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/", CreateContract)
            .WithSummary("Create a contract")
            .WithDescription(
                "Defaults to MONITOR. A contract that started blocking the moment it was " +
                "written would be authored by guessing and then discovered in production.")
            .Produces<ContractResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequireScope(Scope.ContractWrite);

        // Before /{contract}, or 'resolve' would be read as a contract name.
        group.MapPost("/resolve", Resolve)
            .WithSummary("Resolve a topology against the environment's contracts")
            .WithDescription(
                "What an SDK calls at startup: one request for its whole topology rather than " +
                "one per route, because a fleet-wide restart is the real load pattern. A route " +
                "nothing governs is answered with an empty contract list and OFF, not omitted " +
                "— the SDK needs to tell 'ungoverned' from 'I forgot to ask'. A route governed " +
                "by more than one contract returns all of them, with the strictest enforcement " +
                "and the union of their subjects, so the ambiguity is visible rather than " +
                "resolved by name order.")
            .Produces<ResolveContractsResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{contract}", GetContract)
            .WithSummary("Get a contract and its bindings")
            .Produces<ContractResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{contract}/publishes", AddPublishBinding)
            .WithSummary("Add a publish binding")
            .WithDescription(
                "Refused when the pattern overlaps an existing binding that carries different " +
                "subjects and neither declares precedence. Overlap is intersection, not text: " +
                "'orders.*' and '*.created' both match 'orders.created'.")
            .Produces<ContractResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequireScope(Scope.ContractWrite);

        group.MapPost("/{contract}/consumes", AddConsumeBinding)
            .WithSummary("Add a consume binding")
            .Produces<ContractResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequireScope(Scope.ContractWrite);

        group.MapPut("/{contract}/enforcement", SetEnforcement)
            .WithSummary("Change how much a contract may do")
            .Produces<ContractResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireScope(Scope.ContractWrite);

        return app;
    }

    private static async Task<IResult> ListContracts(
        string env, IDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(new ListContractsQuery(env), cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Ok(result.Value.Select(ContractResponse.From).ToList());
    }

    private static async Task<IResult> GetContract(
        string env, string contract, IDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(
            new GetContractQuery(env, contract), cancellationToken).ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Ok(ContractResponse.From(result.Value));
    }

    private static async Task<IResult> CreateContract(
        string env,
        CreateContractRequest request,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await dispatcher.SendAsync(
            new CreateContractCommand(env, request.Name, request.Enforcement),
            cancellationToken).ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Created(
                $"/v1/environments/{env}/contracts/{result.Value.Name}",
                ContractResponse.From(result.Value));
    }

    private static async Task<IResult> AddPublishBinding(
        string env,
        string contract,
        AddPublishBindingRequest request,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await dispatcher.SendAsync(
            new AddPublishBindingCommand(
                env,
                contract,
                request.Exchange,
                request.RoutingKeyPattern,
                request.Subjects,
                request.BrokerId,
                request.VirtualHost,
                request.Precedence),
            cancellationToken).ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Created(
                $"/v1/environments/{env}/contracts/{contract}",
                ContractResponse.From(result.Value));
    }

    private static async Task<IResult> AddConsumeBinding(
        string env,
        string contract,
        AddConsumeBindingRequest request,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await dispatcher.SendAsync(
            new AddConsumeBindingCommand(
                env, contract, request.Queue, request.Subjects,
                request.BrokerId, request.VirtualHost),
            cancellationToken).ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Created(
                $"/v1/environments/{env}/contracts/{contract}",
                ContractResponse.From(result.Value));
    }

    private static async Task<IResult> SetEnforcement(
        string env,
        string contract,
        SetEnforcementRequest request,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await dispatcher.SendAsync(
            new SetContractEnforcementCommand(env, contract, request.Enforcement),
            cancellationToken).ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Ok(ContractResponse.From(result.Value));
    }

    private static async Task<IResult> Resolve(
        string env,
        ResolveContractsRequest request,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await dispatcher.QueryAsync(
            new ResolveContractsQuery(
                env,
                request.BrokerId,
                request.VirtualHost,
                [.. (request.Publishes ?? []).Select(p => new PublishTarget(p.Exchange, p.RoutingKey))],
                request.Consumes ?? []),
            cancellationToken).ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Ok(ResolveContractsResponse.From(result.Value));
    }
}

// ------------------------------------------------------------------------- requests

/// <summary>Request to create a contract.</summary>
/// <param name="Name">A name unique within the environment.</param>
/// <param name="Enforcement"><c>OFF</c>, <c>MONITOR</c> or <c>ENFORCE</c>; defaults to MONITOR.</param>
public sealed record CreateContractRequest(string Name, string? Enforcement = null);

/// <summary>A subject and which of its versions a binding accepts.</summary>
/// <param name="Subject">The subject name.</param>
/// <param name="Selector"><c>latest</c>, an ordinal, or <c>&gt;=N</c>.</param>
public sealed record SubjectRefRequest(string Subject, string Selector);

/// <summary>Request to add a publish binding.</summary>
/// <param name="Exchange">The exchange.</param>
/// <param name="RoutingKeyPattern">An AMQP topic pattern.</param>
/// <param name="Subjects">The subjects permitted.</param>
/// <param name="BrokerId">A specific broker, or omit for every broker in the environment.</param>
/// <param name="VirtualHost">The virtual host; defaults to <c>/</c>.</param>
/// <param name="Precedence">Which binding wins when two overlap.</param>
public sealed record AddPublishBindingRequest(
    string Exchange,
    string RoutingKeyPattern,
    IReadOnlyList<SubjectRefInput> Subjects,
    Guid? BrokerId = null,
    string? VirtualHost = null,
    int? Precedence = null);

/// <summary>Request to add a consume binding.</summary>
/// <param name="Queue">The queue.</param>
/// <param name="Subjects">The subjects expected.</param>
/// <param name="BrokerId">A specific broker, or omit for every broker in the environment.</param>
/// <param name="VirtualHost">The virtual host; defaults to <c>/</c>.</param>
public sealed record AddConsumeBindingRequest(
    string Queue,
    IReadOnlyList<SubjectRefInput> Subjects,
    Guid? BrokerId = null,
    string? VirtualHost = null);

/// <summary>Request to change enforcement.</summary>
/// <param name="Enforcement"><c>OFF</c>, <c>MONITOR</c> or <c>ENFORCE</c>.</param>
public sealed record SetEnforcementRequest(string Enforcement);

/// <summary>One route an SDK wants resolved.</summary>
/// <param name="Exchange">The exchange.</param>
/// <param name="RoutingKey">A concrete routing key, not a pattern.</param>
public sealed record PublishTargetRequest(string Exchange, string RoutingKey);

/// <summary>Request to resolve a topology.</summary>
/// <param name="BrokerId">Which broker the caller is connected to.</param>
/// <param name="VirtualHost">Its virtual host; defaults to <c>/</c>.</param>
/// <param name="Publishes">The routes it publishes on.</param>
/// <param name="Consumes">The queues it consumes from.</param>
public sealed record ResolveContractsRequest(
    Guid? BrokerId = null,
    string? VirtualHost = null,
    IReadOnlyList<PublishTargetRequest>? Publishes = null,
    IReadOnlyList<string>? Consumes = null);

// ------------------------------------------------------------------------ responses

/// <summary>A subject a binding carries.</summary>
/// <param name="Subject">The subject name.</param>
/// <param name="Selector">Which versions apply.</param>
public sealed record SubjectRefResponse(string Subject, string Selector)
{
    /// <summary>Projects a reference.</summary>
    /// <param name="reference">The reference.</param>
    /// <returns>The wire form.</returns>
    public static SubjectRefResponse From(SubjectRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        return new SubjectRefResponse(reference.Subject.Value, reference.Selector.ToString());
    }
}

/// <summary>A publish binding.</summary>
/// <param name="Exchange">The exchange.</param>
/// <param name="RoutingKeyPattern">The pattern.</param>
/// <param name="BrokerId">The broker, or null for all of them.</param>
/// <param name="VirtualHost">The virtual host.</param>
/// <param name="Precedence">Which binding wins when two overlap.</param>
/// <param name="Subjects">The subjects permitted.</param>
public sealed record PublishBindingResponse(
    string Exchange,
    string RoutingKeyPattern,
    Guid? BrokerId,
    string VirtualHost,
    int? Precedence,
    IReadOnlyList<SubjectRefResponse> Subjects);

/// <summary>A consume binding.</summary>
/// <param name="Queue">The queue.</param>
/// <param name="BrokerId">The broker, or null for all of them.</param>
/// <param name="VirtualHost">The virtual host.</param>
/// <param name="Subjects">The subjects expected.</param>
public sealed record ConsumeBindingResponse(
    string Queue,
    Guid? BrokerId,
    string VirtualHost,
    IReadOnlyList<SubjectRefResponse> Subjects);

/// <summary>A contract and its bindings.</summary>
/// <param name="Name">The contract name.</param>
/// <param name="Enforcement">How much it may do.</param>
/// <param name="CreatedAt">When it was created.</param>
/// <param name="Publishes">The publish bindings.</param>
/// <param name="Consumes">The consume bindings.</param>
public sealed record ContractResponse(
    string Name,
    string Enforcement,
    DateTimeOffset CreatedAt,
    IReadOnlyList<PublishBindingResponse> Publishes,
    IReadOnlyList<ConsumeBindingResponse> Consumes)
{
    /// <summary>Projects a contract.</summary>
    /// <param name="contract">The contract.</param>
    /// <returns>The wire form.</returns>
    public static ContractResponse From(Contract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);

        return new ContractResponse(
            contract.Name,
            EnforcementToken(contract.Enforcement),
            contract.CreatedAt,
            [.. contract.Publishes.Select(b => new PublishBindingResponse(
                b.Exchange,
                b.RoutingKeyPattern.Value,
                b.Scope.BrokerId,
                b.Scope.VirtualHost,
                b.Precedence,
                [.. b.Subjects.Select(SubjectRefResponse.From)]))],
            [.. contract.Consumes.Select(b => new ConsumeBindingResponse(
                b.Queue,
                b.Scope.BrokerId,
                b.Scope.VirtualHost,
                [.. b.Subjects.Select(SubjectRefResponse.From)]))]);
    }

    // Explicit, not the uppercased member name -- the class of bug M6.1 found across the API.
    internal static string EnforcementToken(EnforcementMode mode) => WireTokens.For(mode);
}

/// <summary>What the environment's contracts say about one route or queue.</summary>
/// <param name="Contracts">
/// Every contract that matched, in name order; empty when nothing governs the route.
/// </param>
/// <param name="Enforcement">
/// The strictest enforcement among them; <c>OFF</c> when nothing matched.
/// </param>
/// <param name="Subjects">The union of what they permit.</param>
/// <remarks>
/// <b>A list, because more than one contract can govern a route</b> (decision 21). The overlap
/// invariant holds within a contract and never held across them, so resolve used to answer with
/// whichever sorted first by name — the arbitrary outcome that invariant exists to prevent, one
/// level up, and invisible to the publisher it affected.
/// </remarks>
public sealed record ResolvedBindingResponse(
    IReadOnlyList<string> Contracts,
    string Enforcement,
    IReadOnlyList<SubjectRefResponse> Subjects)
{
    /// <summary>Projects a resolution.</summary>
    /// <param name="binding">The resolution.</param>
    /// <returns>The wire form.</returns>
    public static ResolvedBindingResponse From(ResolvedBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        return new ResolvedBindingResponse(
            binding.Contracts,
            ContractResponse.EnforcementToken(binding.Enforcement),
            [.. binding.Subjects.Select(SubjectRefResponse.From)]);
    }
}

/// <summary>Everything an SDK asked about, answered in request order.</summary>
/// <param name="Publishes">One entry per requested route.</param>
/// <param name="Consumes">One entry per requested queue.</param>
public sealed record ResolveContractsResponse(
    IReadOnlyList<ResolvedBindingResponse> Publishes,
    IReadOnlyList<ResolvedBindingResponse> Consumes)
{
    /// <summary>Projects a resolution.</summary>
    /// <param name="resolved">The resolution.</param>
    /// <returns>The wire form.</returns>
    public static ResolveContractsResponse From(ResolvedContracts resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        return new ResolveContractsResponse(
            [.. resolved.Publishes.Select(ResolvedBindingResponse.From)],
            [.. resolved.Consumes.Select(ResolvedBindingResponse.From)]);
    }
}
