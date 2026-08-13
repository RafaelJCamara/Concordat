using Concordat.Infrastructure.Persistence;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;

namespace Concordat.EndToEnd;

/// <summary>
/// The whole stack: a real registry over a real database, and a real broker.
/// </summary>
/// <remarks>
/// <para>
/// <b>This suite exists because both halves were already tested and the seam between them was
/// not.</b> <c>Concordat.RabbitMq.Tests</c> runs the middleware against a real broker with a
/// <c>FakeClient</c>; <c>Concordat.Api.IntegrationTests</c> runs the registry against a real
/// database with no client and no broker. Nothing exercised a real
/// <see cref="Concordat.Client.ConcordatClient"/> talking HTTP to a real registry and feeding
/// the middleware — which is precisely where M3.1 found three protocol bugs, none of which
/// would have failed against a mock written from the same wrong assumptions.
/// </para>
/// <para>
/// The registry is hosted in-process by <see cref="WebApplicationFactory{TEntryPoint}"/>, so
/// the client's <see cref="HttpClient"/> reaches it without a socket. That is still a real
/// HTTP round trip through the real pipeline — routing, model binding, serialisation and the
/// Problem Details mapping all run — which is what matters here. The broker is a genuine
/// container, because AMQP framing and header survival cannot be faked usefully.
/// </para>
/// </remarks>
public sealed class StackFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:17-alpine").Build();

    // Pinned to match the version M2.5 measured header survival against. A broker that
    // changes underneath this suite produces failures nobody can reproduce.
    private readonly IContainer _rabbit = new ContainerBuilder("rabbitmq:4.1")
        .WithPortBinding(5672, true)
        .WithWaitStrategy(
            Wait.ForUnixContainer().UntilMessageIsLogged("Server startup complete"))
        .Build();

    /// <summary>The broker's host and mapped port.</summary>
    public string BrokerHost => _rabbit.Hostname;

    /// <summary>The mapped AMQP port.</summary>
    public int BrokerPort => _rabbit.GetMappedPublicPort(5672);

    async Task IAsyncLifetime.InitializeAsync()
    {
        // Started together: they are independent, and starting two containers in sequence
        // doubles the slowest part of this suite for no reason.
        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync()).ConfigureAwait(false);

        using var scope = Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ConcordatDbContext>();
        await context.Database.MigrateAsync().ConfigureAwait(false);
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _postgres.DisposeAsync().ConfigureAwait(false);
        await _rabbit.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override IHost CreateHost(IHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureServices(services =>
        {
            var descriptor = services.Single(
                d => d.ServiceType == typeof(DbContextOptions<ConcordatDbContext>));
            services.Remove(descriptor);

            services.AddDbContext<ConcordatDbContext>(
                options => options.UseNpgsql(_postgres.GetConnectionString()));
        });

        return base.CreateHost(builder);
    }
}

/// <summary>Marks a class as sharing one stack.</summary>
[CollectionDefinition(Name)]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "'Collection' is xunit's own term for a shared-fixture group.")]
public sealed class StackCollection : ICollectionFixture<StackFixture>
{
    /// <summary>The collection name.</summary>
    public const string Name = "stack";
}
