using Concordat.Application.Abstractions;
using Concordat.Domain.Registry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Concordat.Infrastructure.Persistence;

/// <summary>
/// Builds a context for <c>dotnet ef</c> at design time only.
/// </summary>
/// <remarks>
/// <para>
/// The migration tooling needs to construct a context without running the host, and the host
/// deliberately refuses to start without a connection string. This supplies a placeholder:
/// generating a migration never opens a connection, it only needs the provider to know which
/// SQL dialect to emit.
/// </para>
/// <para>
/// The value below is <b>never used at runtime</b> — nothing resolves this factory outside the
/// EF tools. If a migration ever appears to run against it, something is wired wrong.
/// </para>
/// </remarks>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ConcordatDbContext>
{
    private const string DesignTimeConnectionString =
        "Host=localhost;Database=concordat_design_time;Username=postgres;Password=postgres";

    /// <inheritdoc />
    public ConcordatDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("CONCORDAT_DESIGN_TIME_CONNECTION")
            ?? DesignTimeConnectionString;

        var options = new DbContextOptionsBuilder<ConcordatDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new ConcordatDbContext(options, new DesignTimeTenantContext());
    }

    private sealed class DesignTimeTenantContext : ITenantContext
    {
        public TenantId Current => TenantId.SelfHosted;
    }
}
