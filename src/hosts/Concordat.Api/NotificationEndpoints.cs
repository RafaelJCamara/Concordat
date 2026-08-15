using Concordat.Application.Abstractions;
using Concordat.Application.Governance;
using Concordat.Domain.Governance;
using Concordat.Domain.Identity;

namespace Concordat.Api;

/// <summary>M7.5's notification routes.</summary>
public static class NotificationEndpoints
{
    /// <summary>Maps the notification routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/v1/environments/{env}/notifications").WithTags("Notifications");

        group.MapGet("/", List)
            .WithSummary("List an environment's notification subscriptions")
            .Produces<IReadOnlyList<SubscriptionResponse>>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireScope(Scope.EnvRead);

        group.MapPost("/", Create)
            .WithSummary("Subscribe a channel to an environment's events")
            .WithDescription(
                "An empty event list means every event. The alternative is a subscription that " +
                "is configured, enabled, and silently delivers nothing — which looks correct " +
                "from every screen. A WEBHOOK subscription also gets a signing secret, " +
                "returned here once: every delivery to it carries an X-Concordat-Signature " +
                "header, an HMAC-SHA256 of the body keyed on that secret, so the receiver can " +
                "tell this registry sent it from anything else that found the URL.")
            .Produces<CreatedSubscriptionResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireScope(Scope.EnvWrite);

        group.MapPut("/{id:guid}/enabled", SetEnabled)
            .WithSummary("Mute or unmute a subscription without losing its settings")
            .Produces<SubscriptionResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireScope(Scope.EnvWrite);

        group.MapDelete("/{id:guid}", Delete)
            .WithSummary("Delete a subscription")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireScope(Scope.EnvWrite);

        app.MapGet("/v1/notifications/outbox", Depth)
            .WithTags("Notifications")
            .WithSummary("How much is waiting to be delivered")
            .WithDescription(
                "A non-zero parked count is the signal that a subscriber has been failing long " +
                "enough to be given up on. Nothing else will say so.")
            .Produces<OutboxDepthResponse>()
            .RequireScope(Scope.EnvRead);

        return app;
    }

    private static async Task<IResult> List(
        string env, IDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(new ListSubscriptionsQuery(env), cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Ok(result.Value.Select(SubscriptionResponse.From).ToList());
    }

    private static async Task<IResult> Create(
        string env,
        CreateSubscriptionRequest request,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await dispatcher.SendAsync(
            new CreateSubscriptionCommand(env, request.Channel, request.Endpoint, request.Events),
            cancellationToken).ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Created(
                $"/v1/environments/{env}/notifications/{result.Value.Subscription.Id}",
                new CreatedSubscriptionResponse(
                    SubscriptionResponse.From(result.Value.Subscription),
                    result.Value.SigningSecret));
    }

    private static async Task<IResult> SetEnabled(
        string env,
        Guid id,
        SetSubscriptionEnabledRequest request,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await dispatcher.SendAsync(
            new SetSubscriptionEnabledCommand(env, id, request.Enabled),
            cancellationToken).ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Ok(SubscriptionResponse.From(result.Value));
    }

    private static async Task<IResult> Delete(
        string env, Guid id, IDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var result = await dispatcher.SendAsync(
            new DeleteSubscriptionCommand(env, id), cancellationToken).ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.NoContent();
    }

    private static async Task<IResult> Depth(IOutbox outbox, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outbox);

        var (pending, parked) = await outbox.DepthAsync(cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(new OutboxDepthResponse(pending, parked));
    }
}

/// <summary>Request to create a subscription.</summary>
/// <param name="Channel"><c>EMAIL</c> or <c>WEBHOOK</c>.</param>
/// <param name="Endpoint">An email address, or an <c>https</c> URL.</param>
/// <param name="Events">Which events, or omit for all of them.</param>
public sealed record CreateSubscriptionRequest(
    string Channel, string Endpoint, IReadOnlyList<string>? Events = null);

/// <summary>Request to mute or unmute a subscription.</summary>
/// <param name="Enabled">Whether to deliver.</param>
public sealed record SetSubscriptionEnabledRequest(bool Enabled);

/// <summary>A notification subscription.</summary>
/// <param name="Id">Its identity.</param>
/// <param name="Channel"><c>EMAIL</c> or <c>WEBHOOK</c>.</param>
/// <param name="Endpoint">Where notifications go.</param>
/// <param name="Events">Which events, or empty for all of them.</param>
/// <param name="Enabled">Whether it is currently delivering.</param>
/// <param name="CreatedAt">When it was created.</param>
public sealed record SubscriptionResponse(
    Guid Id,
    string Channel,
    string Endpoint,
    IReadOnlyList<string> Events,
    bool Enabled,
    DateTimeOffset CreatedAt)
{
    /// <summary>Projects a subscription.</summary>
    /// <param name="subscription">The subscription.</param>
    /// <returns>The wire form.</returns>
    public static SubscriptionResponse From(NotificationSubscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        return new SubscriptionResponse(
            subscription.Id,
            ChannelTokens.For(subscription.Channel),
            subscription.Endpoint,
            [.. subscription.Events.Select(NotificationTokens.For)],
            subscription.Enabled,
            subscription.CreatedAt);
    }
}

/// <summary>A newly created subscription and its one-time webhook signing secret.</summary>
/// <param name="Subscription">The subscription.</param>
/// <param name="SigningSecret">
/// The secret to configure on the receiving end, for a WEBHOOK subscription. <b>This is the
/// only time it is ever returned</b> — the registry stores only an encrypted reference and
/// cannot show it again. Null for an EMAIL subscription, which has nothing to sign.
/// </param>
public sealed record CreatedSubscriptionResponse(
    SubscriptionResponse Subscription, string? SigningSecret);

/// <summary>How much the outbox is holding.</summary>
/// <param name="Pending">Staged and not yet delivered.</param>
/// <param name="Parked">Given up on after repeated failures, and kept as evidence.</param>
public sealed record OutboxDepthResponse(int Pending, int Parked);
