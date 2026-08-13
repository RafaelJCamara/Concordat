using Concordat.Application.Abstractions;
using Concordat.Domain.Results;

namespace Concordat.Application.Tests;

/// <summary>
/// The hand-rolled dispatcher, whose one job the compiler cannot check.
/// </summary>
/// <remarks>
/// Callers pass a command through <see cref="ICommand{TResult}"/>, so the static type at the
/// call site says nothing about which handler should run. Routing has to come from the runtime
/// type, and getting that wrong surfaces as a resolution failure at request time rather than a
/// build error.
/// </remarks>
public class DispatcherTests
{
    private sealed record Ping(string Value) : ICommand<string>;

    private sealed record Pong(string Value) : ICommand<string>;

    private sealed record Ask(string Value) : IQuery<string>;

    private sealed class PingHandler : ICommandHandler<Ping, string>
    {
        public Task<Result<string>> HandleAsync(Ping command, CancellationToken cancellationToken) =>
            Task.FromResult(Result<string>.Success($"ping:{command.Value}"));
    }

    private sealed class PongHandler : ICommandHandler<Pong, string>
    {
        public Task<Result<string>> HandleAsync(Pong command, CancellationToken cancellationToken) =>
            Task.FromResult(Result<string>.Failure("pong_refused", "no"));
    }

    private sealed class AskHandler : IQueryHandler<Ask, string>
    {
        public CancellationToken Received { get; private set; }

        public Task<Result<string>> HandleAsync(Ask query, CancellationToken cancellationToken)
        {
            Received = cancellationToken;
            return Task.FromResult(Result<string>.Success($"ask:{query.Value}"));
        }
    }

    /// <summary>The narrowest service provider that satisfies the dispatcher.</summary>
    private sealed class Services(Dictionary<Type, object> registrations) : IServiceProvider
    {
        public object? GetService(Type serviceType) => registrations.GetValueOrDefault(serviceType);
    }

    private static Dispatcher Dispatcher(params (Type Contract, object Handler)[] handlers) =>
        new(new Services(handlers.ToDictionary(h => h.Contract, h => h.Handler)));

    [Fact]
    public async Task ACommandIsRoutedByItsRuntimeTypeNotItsStaticOne()
    {
        // Both commands satisfy ICommand<string>, which is all SendAsync sees. If routing used
        // the static type, one of these would run the other's handler.
        var dispatcher = Dispatcher(
            (typeof(ICommandHandler<Ping, string>), new PingHandler()),
            (typeof(ICommandHandler<Pong, string>), new PongHandler()));

        var ping = await dispatcher.SendAsync<string>(new Ping("a"));
        var pong = await dispatcher.SendAsync<string>(new Pong("b"));

        Assert.Equal("ping:a", ping.Value);
        Assert.Equal("pong_refused", pong.Error!.Code);
    }

    [Fact]
    public async Task AQueryIsRoutedThroughTheQueryHandlerContract()
    {
        // Commands and queries have separate handler interfaces, so a dispatcher that used one
        // for both would resolve nothing for half the application.
        var dispatcher = Dispatcher((typeof(IQueryHandler<Ask, string>), new AskHandler()));

        var result = await dispatcher.QueryAsync<string>(new Ask("c"));

        Assert.Equal("ask:c", result.Value);
    }

    [Fact]
    public async Task TheCancellationTokenReachesTheHandler()
    {
        // It is passed by reflection, where dropping an argument is not a compile error.
        var handler = new AskHandler();
        var dispatcher = Dispatcher((typeof(IQueryHandler<Ask, string>), handler));
        using var cancellation = new CancellationTokenSource();

        await dispatcher.QueryAsync<string>(new Ask("d"), cancellation.Token);

        Assert.Equal(cancellation.Token, handler.Received);
    }

    [Fact]
    public async Task AnUnregisteredHandler_FailsLoudlyRatherThanReturningNothing()
    {
        var dispatcher = Dispatcher();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => dispatcher.SendAsync<string>(new Ping("e")));
    }

    [Fact]
    public async Task ANullRequest_IsRejectedAsAnArgument()
    {
        var dispatcher = Dispatcher();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => dispatcher.SendAsync<string>(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => dispatcher.QueryAsync<string>(null!));
    }
}
