using System.Net.Http.Json;
using System.Text.Json;
using Concordat.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// The real API, in-process, over a real PostgreSQL container.
/// </summary>
/// <remarks>
/// No mocked persistence and no test doubles for the pipeline: the point of these tests is
/// that canonicalisation, identity, the compatibility engine, the aggregate and the database
/// agree with each other, which is exactly what a mocked layer would hide.
/// </remarks>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder("postgres:17-alpine").Build();

    /// <summary>JSON options matching the API's own.</summary>
    public static JsonSerializerOptions Json { get; } =
        new(JsonSerializerDefaults.Web);

    async Task IAsyncLifetime.InitializeAsync()
    {
        await _container.StartAsync().ConfigureAwait(false);

        using var scope = Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ConcordatDbContext>();
        await context.Database.MigrateAsync().ConfigureAwait(false);
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _container.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override IHost CreateHost(IHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureServices(services =>
        {
            // Replace the DbContext registration with one pointed at the container.
            var descriptor = services.Single(
                d => d.ServiceType == typeof(DbContextOptions<ConcordatDbContext>));
            services.Remove(descriptor);

            services.AddDbContext<ConcordatDbContext>(
                options => options.UseNpgsql(_container.GetConnectionString()));
        });

        return base.CreateHost(builder);
    }

    /// <summary>Reads a JSON response body, failing the test if the status is unexpected.</summary>
    /// <typeparam name="T">The body type.</typeparam>
    /// <param name="response">The response.</param>
    /// <returns>The deserialised body.</returns>
    public static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var body = await response.Content.ReadFromJsonAsync<T>(Json).ConfigureAwait(false);
        Assert.NotNull(body);
        return body;
    }
}

/// <summary>Marks a class as sharing one API host and database.</summary>
[CollectionDefinition(Name)]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "'Collection' is xunit's own term for a shared-fixture group.")]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    /// <summary>The collection name.</summary>
    public const string Name = "api";
}
