using Concordat.Application.Abstractions;
using Concordat.Domain.Registry;
using Concordat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Concordat.Infrastructure;

/// <summary>
/// Binds a fixed tenant for every operation.
/// </summary>
/// <remarks>
/// The self-hosted implementation of <see cref="ITenantContext"/>. Cloud swaps this at the
/// composition root for one that resolves per request (DESIGN §8) — no code above this line
/// changes, which is the point of wiring the seam from M1.5.
/// </remarks>
public sealed class SingleTenantContext : ITenantContext
{
    /// <inheritdoc />
    public TenantId Current => TenantId.SelfHosted;
}

/// <summary>Registration helpers for the infrastructure layer.</summary>
public static class DependencyInjection
{
    /// <summary>Registers the database context and the self-hosted tenant binding.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">A PostgreSQL connection string.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddConcordatPersistence(
        this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ITenantContext, SingleTenantContext>();
        services.AddDbContext<ConcordatDbContext>(options => options.UseNpgsql(connectionString));

        services.AddScoped<ISubjectRepository, SubjectRepository>();
        services.AddScoped<ISchemaRepository, SchemaRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        return services;
    }
}
