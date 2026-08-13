using Concordat.Application.Abstractions;
using Concordat.Domain.Registry;
using Concordat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// A real PostgreSQL instance in a container, shared across a test class.
/// </summary>
/// <remarks>
/// A real database rather than the in-memory provider, on purpose. Everything M1.5 actually
/// needs to prove — unique constraints, check constraints, <c>xmin</c> concurrency, and the
/// migration applying at all — either does not exist in the in-memory provider or behaves
/// differently there.
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    // The image is pinned rather than floating: a test suite whose database version changes
    // under it produces failures nobody can reproduce.
    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder("postgres:17-alpine").Build();

    /// <summary>The tenant used unless a test overrides it.</summary>
    public static TenantId DefaultTenant => TenantId.SelfHosted;

    /// <summary>Starts the container and applies migrations.</summary>
    public async Task InitializeAsync()
    {
        await _container.StartAsync().ConfigureAwait(false);

        // Applying the real migration, not EnsureCreated: EnsureCreated builds the schema from
        // the model and would happily pass while the migration itself was broken.
        await using var context = NewContext();
        await context.Database.MigrateAsync().ConfigureAwait(false);
    }

    /// <summary>Stops the container.</summary>
    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>Creates a context bound to the given tenant.</summary>
    /// <param name="tenant">The tenant to scope to, or the default when omitted.</param>
    /// <returns>A new context. The caller disposes it.</returns>
    public ConcordatDbContext NewContext(TenantId? tenant = null)
    {
        var options = new DbContextOptionsBuilder<ConcordatDbContext>()
            .UseNpgsql(_container.GetConnectionString())
            .Options;

        return new ConcordatDbContext(options, new FixedTenant(tenant ?? DefaultTenant));
    }

    private sealed class FixedTenant(TenantId tenant) : ITenantContext
    {
        public TenantId Current { get; } = tenant;
    }
}

/// <summary>Marks a class as sharing one PostgreSQL container.</summary>
/// <remarks>
/// Named for xunit's collection concept, not the BCL's — CA1711 is suppressed rather than
/// renamed because "collection" is the term xunit itself uses for this.
/// </remarks>
[CollectionDefinition(Name)]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "'Collection' is xunit's own term for a shared-fixture group.")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    /// <summary>The collection name.</summary>
    public const string Name = "postgres";
}
