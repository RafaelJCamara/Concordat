using System.Net.Http.Json;
using System.Text.Json;
using Concordat.Infrastructure;
using Concordat.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
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
public class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder("postgres:17-alpine").Build();

    /// <summary>JSON options matching the API's own.</summary>
    public static JsonSerializerOptions Json { get; } =
        new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Which deployment flavour to host as.
    /// </summary>
    /// <remarks>
    /// Overridden by <c>CloudApiFactory</c>. It is a configuration value rather than a service
    /// override on purpose: the point of M9.1 is that the profile is read once at the
    /// composition root, so a test that reached in and replaced <c>ITenantContext</c> directly
    /// would be proving something weaker than the thing that ships.
    /// </remarks>
    protected virtual ConcordatProfile Profile => ConcordatProfile.SelfHosted;

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

    /// <summary>Applies settings the app reads before any service is registered.</summary>
    /// <param name="builder">The web host builder.</param>
    /// <remarks>
    /// <b><c>UseSetting</c>, not <c>ConfigureAppConfiguration</c>.</b> Under minimal hosting
    /// the app's top-level statements read <c>builder.Configuration</c> and build the host
    /// before any <c>ConfigureAppConfiguration</c> callback this factory registers has run — so
    /// a value added that way is present in configuration and was never seen by the code that
    /// needed it. It fails silently, as a profile that stayed self-hosted while the test
    /// believed it was Cloud.
    /// </remarks>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseSetting("Concordat:Profile", Profile.ToString());

        // The in-memory TestServer never populates Connection.RemoteIpAddress, which would
        // otherwise collapse every request across the whole run into one shared rate-limit
        // partition. See Program.cs's DisableRateLimiting comment.
        builder.UseSetting("Concordat:DisableRateLimiting", "true");
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

            // Our two background services are removed, and only ours. A timer draining the
            // outbox mid-assertion makes every count in these tests a race; notification tests
            // resolve NotificationDispatcher and pump deliberately, which is also the only way
            // to assert on what one pass did. The unclaimed-instance warning is removed because
            // it polls the database on a timer for a condition these tests toggle constantly,
            // and its findings would be noise on a fixture that both claims and does not.
            // Removing IHostedService wholesale would take the test server's own host service
            // with it and nothing would start.
            foreach (var ours in services
                .Where(d => d.ServiceType == typeof(IHostedService) &&
                            (d.ImplementationType == typeof(OutboxPump) ||
                             d.ImplementationType == typeof(UnclaimedInstanceWarning)))
                .ToList())
            {
                services.Remove(ours);
            }
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

        // THE STATUS IS CHECKED FIRST, AND THAT WAS LEARNED THE SLOW WAY.
        //
        // Without this, a 500 whose RFC 9457 body shares no field names with T deserialises to a
        // T with every value defaulted -- so a failing endpoint reads as one that ran and did
        // nothing. That is a worse lie than an exception: it sends whoever is debugging into the
        // handler to work out why it returned zeros, when the handler never ran.
        if (!response.IsSuccessStatusCode)
        {
            var problem = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            Assert.Fail(
                $"Expected a success response to deserialise as {typeof(T).Name}, got " +
                $"{(int)response.StatusCode} {response.StatusCode}: {problem}");
        }

        var body = await response.Content.ReadFromJsonAsync<T>(Json).ConfigureAwait(false);
        Assert.NotNull(body);
        return body;
    }

    /// <summary>Reads an RFC 9457 problem body.</summary>
    /// <param name="response">The response.</param>
    /// <returns>The parts a test cares about.</returns>
    public static async Task<ApiProblem> ReadProblemAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        // Deliberately does not go through ReadAsync: a problem body is expected on a failure
        // status, so asserting success there would refuse exactly the responses this reads.
        var body = await response.Content.ReadFromJsonAsync<ApiProblem>(Json).ConfigureAwait(false);
        Assert.NotNull(body);
        return body;
    }
}

/// <summary>The parts of a problem response tests assert on.</summary>
/// <param name="ConcordatCode">The stable code clients branch on.</param>
/// <param name="Detail">The human-readable explanation.</param>
/// <param name="Title">The short summary.</param>
public sealed record ApiProblem(string? ConcordatCode, string? Detail, string? Title);

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
