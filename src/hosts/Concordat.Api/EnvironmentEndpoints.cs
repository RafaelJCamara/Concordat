using Concordat.Application.Abstractions;
using Concordat.Application.Registry;
using Concordat.Domain.Identity;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Environment = Concordat.Domain.Registry.Environment;

namespace Concordat.Api;

/// <summary>The environment and broker routes under <c>/v1/environments</c> (M7.1).</summary>
public static class EnvironmentEndpoints
{
    /// <summary>Maps the environment routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapEnvironmentEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/v1/environments").WithTags("Environments");

        group.MapGet("/", ListEnvironments)
            .WithSummary("List environments")
            .Produces<IReadOnlyList<EnvironmentResponse>>();

        group.MapPost("/", CreateEnvironment)
            .WithSummary("Create an environment")
            .WithDescription(
                "An environment named 'prod', 'production' or 'live' defaults to CI_ONLY " +
                "registration; everything else defaults to OPEN. An over-permissive " +
                "production registry is polluted silently and permanently, while an " +
                "over-strict scratch one produces a clear error and a config change.")
            .Produces<EnvironmentResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequireScope(Scope.EnvWrite);

        group.MapGet("/{env}", GetEnvironment)
            .WithSummary("Get an environment and its brokers")
            .Produces<EnvironmentResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPatch("/{env}", UpdateEnvironment)
            .WithSummary("Change an environment's description or policies")
            .WithDescription(
                "Changing the default compatibility policy takes effect immediately for every " +
                "subject that has not set its own — which is the point of inheritance, and " +
                "worth knowing before changing it on an environment with traffic.")
            .Produces<EnvironmentResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireScope(Scope.EnvWrite);

        // Its own route pair rather than only a field on PATCH /{env}. This is the control an
        // operator reaches for during an incident — "stop everything writing to prod" — and it
        // is the one an auditor asks to see. Read access is deliberately looser than write:
        // anyone who can see the environment can see what it admits, because a producer author
        // debugging a refusal needs to read the policy that refused them.
        group.MapGet("/{env}/registration-policy", GetRegistrationPolicy)
            .WithSummary("Read who may register schemas here")
            .Produces<RegistrationPolicyResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/{env}/registration-policy", SetRegistrationPolicy)
            .WithSummary("Change who may register schemas here")
            .WithDescription(
                "OPEN admits any caller with subject:write. CI_ONLY additionally requires the " +
                "'ci' scope, which is what separates a build pipeline from a producer — both " +
                "otherwise arrive with an API key and write access. CLOSED admits nobody: " +
                "schemas reach the environment only by promotion. Enforced server-side, on " +
                "both subject creation and version registration.")
            .Produces<RegistrationPolicyResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireScope(Scope.EnvWrite);

        group.MapPost("/{env}/brokers", AddBroker)
            .WithSummary("Register a broker")
            .WithDescription(
                "A broker's identity is its host, port and virtual host together, so the same " +
                "host may appear more than once under different virtual hosts. Display names " +
                "must be unique, because they are how an operator tells entries apart.")
            .Produces<EnvironmentResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequireScope(Scope.BrokerWrite);

        group.MapPost("/{env}/brokers/{brokerId:guid}/check", CheckBroker)
            .WithSummary("Test a broker connection and record the result")
            .WithDescription(
                "Completes a real AMQP handshake against the specific virtual host, rather " +
                "than probing the port: a socket that accepts tells you a load balancer " +
                "answered, not that the virtual host exists or the credentials work. An " +
                "unreachable broker is recorded and returned, never an error — a registry " +
                "that failed because a broker was down would have adopted someone else's " +
                "outage.")
            .Produces<EnvironmentResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireScope(Scope.BrokerRead);

        group.MapPut("/{env}/brokers/{brokerId:guid}/credentials", SetBrokerCredential)
            .WithSummary("Set or replace a broker's credentials")
            .WithDescription(
                "Write-only (ADR-012). There is no endpoint that returns a credential and no " +
                "field on any response that could carry one — reads report only whether one " +
                "exists. Replacing rotates in place, so the previous secret is not left behind.")
            .Produces<EnvironmentResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireScope(Scope.BrokerWrite);

        group.MapDelete("/{env}/brokers/{brokerId:guid}/credentials", RemoveBrokerCredential)
            .WithSummary("Remove a broker's stored credentials")
            .Produces<EnvironmentResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireScope(Scope.BrokerWrite);

        group.MapDelete("/{env}/brokers/{brokerId:guid}", RemoveBroker)
            .WithSummary("Remove a broker")
            .Produces<EnvironmentResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireScope(Scope.BrokerWrite);

        return app;
    }

    private static async Task<IResult> ListEnvironments(
        IDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(new ListEnvironmentsQuery(), cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Ok(result.Value.Select(EnvironmentResponse.From).ToList());
    }

    private static async Task<IResult> GetEnvironment(
        string env, IDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(new GetEnvironmentQuery(env), cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Ok(EnvironmentResponse.From(result.Value));
    }

    private static async Task<IResult> CreateEnvironment(
        CreateEnvironmentRequest request,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await dispatcher.SendAsync(
            new CreateEnvironmentCommand(
                request.Name,
                request.Description,
                request.CompatibilityMode,
                request.CompatibilitySurface,
                request.RegistrationPolicy),
            cancellationToken).ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Created(
                $"/v1/environments/{result.Value.Name.Value}",
                EnvironmentResponse.From(result.Value));
    }

    private static async Task<IResult> UpdateEnvironment(
        string env,
        UpdateEnvironmentRequest request,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await dispatcher.SendAsync(
            new UpdateEnvironmentCommand(
                env,
                request.Description,
                request.CompatibilityMode,
                request.CompatibilitySurface,
                request.RegistrationPolicy),
            cancellationToken).ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Ok(EnvironmentResponse.From(result.Value));
    }

    private static async Task<IResult> GetRegistrationPolicy(
        string env, IDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(new GetEnvironmentQuery(env), cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Ok(RegistrationPolicyResponse.From(result.Value));
    }

    private static async Task<IResult> SetRegistrationPolicy(
        string env,
        SetRegistrationPolicyRequest request,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // An omitted policy is refused rather than treated as "leave it alone". PATCH means
        // "change what I named"; PUT on a single value means "make it this", and answering 200
        // to a request that changed nothing would tell an operator mid-incident that they had
        // closed an environment they had not.
        if (string.IsNullOrWhiteSpace(request.Policy))
        {
            return ProblemDetailsMapping.From(new DomainError(
                "invalid_request",
                "A policy is required: OPEN, CI_ONLY or CLOSED."));
        }

        // Reuses UpdateEnvironmentCommand rather than adding a command that would duplicate its
        // parsing and its audit entry. Passing null for the other fields is what that command
        // already means by "leave it alone".
        var result = await dispatcher.SendAsync(
            new UpdateEnvironmentCommand(env, null, null, null, request.Policy),
            cancellationToken).ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Ok(RegistrationPolicyResponse.From(result.Value));
    }

    private static async Task<IResult> AddBroker(
        string env,
        AddBrokerRequest request,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await dispatcher.SendAsync(
            new AddBrokerCommand(env, request.DisplayName, request.Uri, request.VirtualHost),
            cancellationToken).ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Created(
                $"/v1/environments/{env}", EnvironmentResponse.From(result.Value));
    }

    private static async Task<IResult> CheckBroker(
        string env,
        Guid brokerId,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.SendAsync(
            new CheckBrokerCommand(env, brokerId), cancellationToken).ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Ok(EnvironmentResponse.From(result.Value));
    }

    private static async Task<IResult> SetBrokerCredential(
        string env,
        Guid brokerId,
        SetBrokerCredentialRequest request,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await dispatcher.SendAsync(
            new SetBrokerCredentialCommand(env, brokerId, request.Username, request.Password),
            cancellationToken).ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Ok(EnvironmentResponse.From(result.Value));
    }

    private static async Task<IResult> RemoveBrokerCredential(
        string env,
        Guid brokerId,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.SendAsync(
            new RemoveBrokerCredentialCommand(env, brokerId), cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Ok(EnvironmentResponse.From(result.Value));
    }

    private static async Task<IResult> RemoveBroker(
        string env,
        Guid brokerId,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.SendAsync(
            new RemoveBrokerCommand(env, brokerId), cancellationToken).ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Ok(EnvironmentResponse.From(result.Value));
    }
}

/// <summary>Request to create an environment.</summary>
/// <param name="Name">Lowercase letters, digits and hyphens.</param>
/// <param name="Description">What it is for.</param>
/// <param name="CompatibilityMode">Default who-breaks axis; omit for the standard default.</param>
/// <param name="CompatibilitySurface">Default what-breaks axis; required when a mode is given.</param>
/// <param name="RegistrationPolicy"><c>OPEN</c>, <c>CI_ONLY</c> or <c>CLOSED</c>.</param>
public sealed record CreateEnvironmentRequest(
    string Name,
    string? Description = null,
    string? CompatibilityMode = null,
    string? CompatibilitySurface = null,
    string? RegistrationPolicy = null);

/// <summary>Request to change an environment. Omitted members are left alone.</summary>
/// <param name="Description">A new description.</param>
/// <param name="CompatibilityMode">A new default who-breaks axis.</param>
/// <param name="CompatibilitySurface">A new default what-breaks axis.</param>
/// <param name="RegistrationPolicy">A new registration policy.</param>
public sealed record UpdateEnvironmentRequest(
    string? Description = null,
    string? CompatibilityMode = null,
    string? CompatibilitySurface = null,
    string? RegistrationPolicy = null);

/// <summary>Request to set a broker's credentials.</summary>
/// <param name="Username">The AMQP user.</param>
/// <param name="Password">Its password. Encrypted at rest and never returned.</param>
public sealed record SetBrokerCredentialRequest(string Username, string Password);

/// <summary>Request to register a broker.</summary>
/// <param name="DisplayName">A human label, unique in the environment.</param>
/// <param name="Uri">An <c>amqp</c> or <c>amqps</c> endpoint.</param>
/// <param name="VirtualHost">The virtual host; defaults to <c>/</c>.</param>
public sealed record AddBrokerRequest(
    string DisplayName, string Uri, string? VirtualHost = null);

/// <summary>One registered broker.</summary>
/// <param name="BrokerId">Its identity.</param>
/// <param name="DisplayName">The operator-facing label.</param>
/// <param name="Uri">The endpoint.</param>
/// <param name="VirtualHost">The virtual host.</param>
/// <param name="UseTls">Whether the scheme is <c>amqps</c>.</param>
/// <param name="HasCredentials">
/// Whether a credential is stored. <b>Never the credential itself</b> — credentials are
/// write-only over the API (ADR-012).
/// </param>
/// <param name="Status"><c>UNKNOWN</c>, <c>REACHABLE</c> or <c>UNREACHABLE</c>.</param>
/// <param name="LastCheckedAt">When the last health check ran.</param>
/// <param name="LastError">Why the last check failed.</param>
public sealed record BrokerResponse(
    Guid BrokerId,
    string DisplayName,
    string Uri,
    string VirtualHost,
    bool UseTls,
    bool HasCredentials,
    string Status,
    DateTimeOffset? LastCheckedAt,
    string? LastError)
{
    /// <summary>Projects a broker.</summary>
    /// <param name="broker">The broker.</param>
    /// <returns>The wire form.</returns>
    public static BrokerResponse From(BrokerConnection broker)
    {
        ArgumentNullException.ThrowIfNull(broker);

        return new BrokerResponse(
            broker.Id,
            broker.DisplayName,
            broker.Uri.ToString(),
            broker.VirtualHost,
            broker.UseTls,
            broker.CredentialRef is not null,
            broker.Status.ToString().ToUpperInvariant(),
            broker.LastCheckedAt,
            broker.LastError);
    }
}

/// <summary>Request to change who may register schemas in an environment.</summary>
/// <param name="Policy"><c>OPEN</c>, <c>CI_ONLY</c> or <c>CLOSED</c>.</param>
public sealed record SetRegistrationPolicyRequest(string Policy);

/// <summary>Who may register schemas in an environment, and what that requires of them.</summary>
/// <param name="Environment">The environment.</param>
/// <param name="Policy"><c>OPEN</c>, <c>CI_ONLY</c> or <c>CLOSED</c>.</param>
/// <param name="RequiredScopes">
/// What a credential must carry to register here, so a caller debugging a refusal can compare
/// it against their own key instead of guessing which of the three policies is in force.
/// </param>
public sealed record RegistrationPolicyResponse(
    string Environment,
    string Policy,
    IReadOnlyList<string> RequiredScopes)
{
    /// <summary>Projects an environment's policy.</summary>
    /// <param name="environment">The environment.</param>
    /// <returns>The wire form.</returns>
    public static RegistrationPolicyResponse From(Environment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        // CLOSED lists nothing rather than an impossible scope: no credential admits you, and
        // naming one would send somebody to go and request it.
        IReadOnlyList<string> required = environment.RegistrationPolicy switch
        {
            Domain.Registry.RegistrationPolicy.Open => [Scope.SubjectWrite],
            Domain.Registry.RegistrationPolicy.CiOnly => [Scope.SubjectWrite, Scope.Ci],
            _ => [],
        };

        return new RegistrationPolicyResponse(
            environment.Name.Value,
            EnvironmentResponse.RegistrationToken(environment.RegistrationPolicy),
            required);
    }
}

/// <summary>An environment and its brokers.</summary>
/// <param name="Name">The name that appears in every route.</param>
/// <param name="Description">What it is for.</param>
/// <param name="DefaultCompatibilityPolicy">What subjects inherit when they set none.</param>
/// <param name="RegistrationPolicy">Who may register schemas here.</param>
/// <param name="CreatedAt">When it was created.</param>
/// <param name="Brokers">The registered brokers.</param>
public sealed record EnvironmentResponse(
    string Name,
    string? Description,
    PolicyResponse DefaultCompatibilityPolicy,
    string RegistrationPolicy,
    DateTimeOffset CreatedAt,
    IReadOnlyList<BrokerResponse> Brokers)
{
    /// <summary>Projects an environment.</summary>
    /// <param name="environment">The environment.</param>
    /// <returns>The wire form.</returns>
    public static EnvironmentResponse From(Environment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return new EnvironmentResponse(
            environment.Name.Value,
            environment.Description,
            PolicyResponse.From(environment.DefaultCompatibilityPolicy),
            RegistrationToken(environment.RegistrationPolicy),
            environment.CreatedAt,
            [.. environment.Brokers.Select(BrokerResponse.From)]);
    }

    // Explicit, not ToUpperInvariant() on the member name: CiOnly would serialise as CIONLY,
    // which is the class of bug M6.1 found across the rest of the API.
    internal static string RegistrationToken(RegistrationPolicy policy) => policy switch
    {
        Domain.Registry.RegistrationPolicy.Open => "OPEN",
        Domain.Registry.RegistrationPolicy.CiOnly => "CI_ONLY",
        Domain.Registry.RegistrationPolicy.Closed => "CLOSED",
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null),
    };
}
