using Concordat.Application.Abstractions;
using Concordat.Application.Registry;
using Concordat.Domain.Governance;
using Concordat.Domain.Results;

namespace Concordat.Application.Governance;

/// <summary>Subscribes a channel to an environment's events (M7.5).</summary>
/// <param name="EnvironmentName">Which environment.</param>
/// <param name="Channel"><c>EMAIL</c> or <c>WEBHOOK</c>.</param>
/// <param name="Endpoint">An email address, or an <c>https</c> URL.</param>
/// <param name="Events">Which events, or empty for all of them.</param>
public sealed record CreateSubscriptionCommand(
    string? EnvironmentName,
    string? Channel,
    string? Endpoint,
    IReadOnlyList<string>? Events) : ICommand<CreatedSubscription>;

/// <summary>What creating a subscription produced.</summary>
/// <param name="Subscription">The subscription.</param>
/// <param name="SigningSecret">
/// The webhook signing secret, for a <c>WEBHOOK</c> subscription — null for <c>EMAIL</c>.
/// <b>Returned once.</b> The registry stores only an encrypted reference to it
/// (<see cref="NotificationSubscription.SigningKeyRef"/>) and cannot show it again; losing it
/// means deleting the subscription and creating a new one.
/// </param>
public sealed record CreatedSubscription(
    NotificationSubscription Subscription, string? SigningSecret);

/// <summary>Turns a subscription's delivery on or off.</summary>
/// <param name="EnvironmentName">Which environment.</param>
/// <param name="Id">The subscription.</param>
/// <param name="Enabled">Whether to deliver.</param>
public sealed record SetSubscriptionEnabledCommand(
    string? EnvironmentName, Guid Id, bool Enabled) : ICommand<NotificationSubscription>;

/// <summary>Deletes a subscription.</summary>
/// <param name="EnvironmentName">Which environment.</param>
/// <param name="Id">The subscription.</param>
public sealed record DeleteSubscriptionCommand(string? EnvironmentName, Guid Id) : ICommand<bool>;

/// <summary>Lists an environment's subscriptions.</summary>
/// <param name="EnvironmentName">Which environment.</param>
public sealed record ListSubscriptionsQuery(string? EnvironmentName)
    : IQuery<IReadOnlyList<NotificationSubscription>>;

/// <summary>Handles <see cref="CreateSubscriptionCommand"/>.</summary>
public sealed class CreateSubscriptionHandler(
    IEnvironmentRepository environments,
    ISubscriptionRepository subscriptions,
    IWebhookSigningKeyStore signingKeys,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
    : ICommandHandler<CreateSubscriptionCommand, CreatedSubscription>
{
    /// <inheritdoc />
    public async Task<Result<CreatedSubscription>> HandleAsync(
        CreateSubscriptionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var environment = await EnvironmentPolicies.RequireAsync(
            environments, command.EnvironmentName, cancellationToken).ConfigureAwait(false);

        if (environment.IsFailure)
        {
            return Result<CreatedSubscription>.Failure(environment.Error!);
        }

        if (!ChannelTokens.TryParse(command.Channel, out var channel))
        {
            return Result<CreatedSubscription>.Failure(
                ConcordatCodes.SubscriptionInvalid,
                $"Unknown channel '{command.Channel}'. Expected EMAIL or WEBHOOK.");
        }

        var events = new List<NotificationEvent>();

        foreach (var token in command.Events ?? [])
        {
            if (!NotificationTokens.TryParse(token, out var parsed))
            {
                // Refused rather than ignored. A typo would otherwise produce a subscription
                // that is configured, enabled, and quietly delivers nothing it was meant to.
                return Result<CreatedSubscription>.Failure(
                    ConcordatCodes.SubscriptionInvalid,
                    $"Unknown event '{token}'. Expected one of: " +
                    $"{string.Join(", ", NotificationTokens.All)}.");
            }

            events.Add(parsed);
        }

        var created = NotificationSubscription.Create(
            environment.Value.Id, channel, command.Endpoint, events, clock.GetUtcNow());

        if (created.IsFailure)
        {
            return Result<CreatedSubscription>.Failure(created.Error!);
        }

        // A receiver has no way to verify a webhook came from this registry -- or was not
        // altered in transit past a compromised or misconfigured intermediary -- without a
        // shared secret, so every webhook subscription gets one. Generated here rather than
        // accepted from the caller: an operator-supplied secret would have to travel through
        // this request's body and this process's logs, and the whole point is a value that
        // never does.
        string? secret = null;

        if (channel is NotificationChannel.Webhook)
        {
            var (reference, generated) = await signingKeys
                .GenerateAsync(cancellationToken).ConfigureAwait(false);

            created.Value.SetSigningKeyRef(reference);
            secret = generated;
        }

        subscriptions.Add(created.Value);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result<CreatedSubscription>.Success(new CreatedSubscription(created.Value, secret));
    }
}

/// <summary>Handles <see cref="SetSubscriptionEnabledCommand"/>.</summary>
public sealed class SetSubscriptionEnabledHandler(
    IEnvironmentRepository environments,
    ISubscriptionRepository subscriptions,
    IUnitOfWork unitOfWork)
    : ICommandHandler<SetSubscriptionEnabledCommand, NotificationSubscription>
{
    /// <inheritdoc />
    public async Task<Result<NotificationSubscription>> HandleAsync(
        SetSubscriptionEnabledCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var found = await SubscriptionPolicies.RequireAsync(
            environments, subscriptions, command.EnvironmentName, command.Id, cancellationToken)
            .ConfigureAwait(false);

        if (found.IsFailure)
        {
            return found;
        }

        found.Value.SetEnabled(command.Enabled);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return found;
    }
}

/// <summary>Handles <see cref="DeleteSubscriptionCommand"/>.</summary>
public sealed class DeleteSubscriptionHandler(
    IEnvironmentRepository environments,
    ISubscriptionRepository subscriptions,
    IWebhookSigningKeyStore signingKeys,
    IUnitOfWork unitOfWork)
    : ICommandHandler<DeleteSubscriptionCommand, bool>
{
    /// <inheritdoc />
    public async Task<Result<bool>> HandleAsync(
        DeleteSubscriptionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var found = await SubscriptionPolicies.RequireAsync(
            environments, subscriptions, command.EnvironmentName, command.Id, cancellationToken)
            .ConfigureAwait(false);

        if (found.IsFailure)
        {
            return Result<bool>.Failure(found.Error!);
        }

        if (found.Value.SigningKeyRef is { } signingKeyRef)
        {
            await signingKeys.RemoveAsync(signingKeyRef, cancellationToken).ConfigureAwait(false);
        }

        subscriptions.Remove(found.Value);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result<bool>.Success(true);
    }
}

/// <summary>Handles <see cref="ListSubscriptionsQuery"/>.</summary>
public sealed class ListSubscriptionsHandler(
    IEnvironmentRepository environments, ISubscriptionRepository subscriptions)
    : IQueryHandler<ListSubscriptionsQuery, IReadOnlyList<NotificationSubscription>>
{
    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<NotificationSubscription>>> HandleAsync(
        ListSubscriptionsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var environment = await EnvironmentPolicies.RequireAsync(
            environments, query.EnvironmentName, cancellationToken).ConfigureAwait(false);

        return environment.IsFailure
            ? Result<IReadOnlyList<NotificationSubscription>>.Failure(environment.Error!)
            : Result<IReadOnlyList<NotificationSubscription>>.Success(
                await subscriptions.ListAsync(environment.Value.Id, cancellationToken)
                    .ConfigureAwait(false));
    }
}

/// <summary>Loading shared by the subscription handlers.</summary>
internal static class SubscriptionPolicies
{
    /// <summary>Loads a subscription, or fails with a code the API can map.</summary>
    public static async Task<Result<NotificationSubscription>> RequireAsync(
        IEnvironmentRepository environments,
        ISubscriptionRepository subscriptions,
        string? environmentName,
        Guid id,
        CancellationToken cancellationToken)
    {
        var environment = await EnvironmentPolicies.RequireAsync(
            environments, environmentName, cancellationToken).ConfigureAwait(false);

        if (environment.IsFailure)
        {
            return Result<NotificationSubscription>.Failure(environment.Error!);
        }

        var found = await subscriptions.FindAsync(environment.Value.Id, id, cancellationToken)
            .ConfigureAwait(false);

        return found is null
            ? Result<NotificationSubscription>.Failure(
                ConcordatCodes.SubscriptionNotFound,
                $"No subscription {id} in environment '{environment.Value.Name.Value}'.")
            : Result<NotificationSubscription>.Success(found);
    }
}
