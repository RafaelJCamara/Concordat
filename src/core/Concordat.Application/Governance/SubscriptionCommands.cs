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
    IReadOnlyList<string>? Events) : ICommand<NotificationSubscription>;

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
    IUnitOfWork unitOfWork,
    TimeProvider clock)
    : ICommandHandler<CreateSubscriptionCommand, NotificationSubscription>
{
    /// <inheritdoc />
    public async Task<Result<NotificationSubscription>> HandleAsync(
        CreateSubscriptionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var environment = await EnvironmentPolicies.RequireAsync(
            environments, command.EnvironmentName, cancellationToken).ConfigureAwait(false);

        if (environment.IsFailure)
        {
            return Result<NotificationSubscription>.Failure(environment.Error!);
        }

        if (!ChannelTokens.TryParse(command.Channel, out var channel))
        {
            return Result<NotificationSubscription>.Failure(
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
                return Result<NotificationSubscription>.Failure(
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
            return created;
        }

        subscriptions.Add(created.Value);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return created;
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
