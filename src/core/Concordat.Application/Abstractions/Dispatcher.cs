using Concordat.Domain.Results;
using Microsoft.Extensions.DependencyInjection;

namespace Concordat.Application.Abstractions;

/// <summary>A request that changes state.</summary>
/// <typeparam name="TResult">What the handler returns on success.</typeparam>
public interface ICommand<TResult>;

/// <summary>A request that reads state.</summary>
/// <typeparam name="TResult">What the handler returns on success.</typeparam>
public interface IQuery<TResult>;

/// <summary>Handles one command.</summary>
/// <typeparam name="TCommand">The command type.</typeparam>
/// <typeparam name="TResult">What it returns on success.</typeparam>
public interface ICommandHandler<in TCommand, TResult>
    where TCommand : ICommand<TResult>
    where TResult : notnull
{
    /// <summary>Executes the command.</summary>
    /// <param name="command">The command.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The result, or the first domain rule that was violated.</returns>
    Task<Result<TResult>> HandleAsync(TCommand command, CancellationToken cancellationToken);
}

/// <summary>Handles one query.</summary>
/// <typeparam name="TQuery">The query type.</typeparam>
/// <typeparam name="TResult">What it returns on success.</typeparam>
public interface IQueryHandler<in TQuery, TResult>
    where TQuery : IQuery<TResult>
    where TResult : notnull
{
    /// <summary>Executes the query.</summary>
    /// <param name="query">The query.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The result, or the reason it could not be produced.</returns>
    Task<Result<TResult>> HandleAsync(TQuery query, CancellationToken cancellationToken);
}

/// <summary>Routes a command or query to its handler.</summary>
/// <remarks>
/// Hand-rolled rather than MediatR, which is now commercially licensed and would conflict
/// with ADR-009. The whole abstraction is two interfaces and a service-locator lookup; the
/// dependency was never carrying its weight.
/// </remarks>
public interface IDispatcher
{
    /// <summary>Sends a command to its handler.</summary>
    /// <typeparam name="TResult">What the command returns.</typeparam>
    /// <param name="command">The command.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The handler's result.</returns>
    Task<Result<TResult>> SendAsync<TResult>(
        ICommand<TResult> command, CancellationToken cancellationToken = default)
        where TResult : notnull;

    /// <summary>Sends a query to its handler.</summary>
    /// <typeparam name="TResult">What the query returns.</typeparam>
    /// <param name="query">The query.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The handler's result.</returns>
    Task<Result<TResult>> QueryAsync<TResult>(
        IQuery<TResult> query, CancellationToken cancellationToken = default)
        where TResult : notnull;
}

/// <inheritdoc />
public sealed class Dispatcher(IServiceProvider services) : IDispatcher
{
    /// <inheritdoc />
    public Task<Result<TResult>> SendAsync<TResult>(
        ICommand<TResult> command, CancellationToken cancellationToken = default)
        where TResult : notnull
    {
        ArgumentNullException.ThrowIfNull(command);

        var handlerType = typeof(ICommandHandler<,>).MakeGenericType(command.GetType(), typeof(TResult));
        var handler = services.GetRequiredService(handlerType);

        return Invoke<TResult>(handler, handlerType, command, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<TResult>> QueryAsync<TResult>(
        IQuery<TResult> query, CancellationToken cancellationToken = default)
        where TResult : notnull
    {
        ArgumentNullException.ThrowIfNull(query);

        var handlerType = typeof(IQueryHandler<,>).MakeGenericType(query.GetType(), typeof(TResult));
        var handler = services.GetRequiredService(handlerType);

        return Invoke<TResult>(handler, handlerType, query, cancellationToken);
    }

    private static Task<Result<TResult>> Invoke<TResult>(
        object handler, Type handlerType, object request, CancellationToken cancellationToken)
        where TResult : notnull
    {
        var method = handlerType.GetMethod(nameof(ICommandHandler<ICommand<TResult>, TResult>.HandleAsync))
            ?? throw new InvalidOperationException($"No HandleAsync on {handlerType}.");

        return (Task<Result<TResult>>)method.Invoke(handler, [request, cancellationToken])!;
    }
}
