using Concordat.Application.Abstractions;
using Concordat.Domain.Registry;
using Concordat.Infrastructure.Identity;
using Concordat.Infrastructure.Notifications;
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
        services.AddScoped<IEnvironmentRepository, EnvironmentRepository>();
        services.AddScoped<IContractRepository, ContractRepository>();
        services.AddScoped<IServiceRegistrationRepository, ServiceRegistrationRepository>();
        services.AddScoped<IAuditLog, AuditLog>();
        services.AddScoped<IOutbox, Outbox>();
        services.AddScoped<ISubscriptionRepository, SubscriptionRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IApiKeyRepository, ApiKeyRepository>();

        // Singleton: it holds no state beyond the derived dummy hash, and that is exactly the
        // thing that must be computed once rather than per request.
        services.AddSingleton<IPasswordHasher, AspNetPasswordHasher>();
        services.AddScoped<ICredentialStore, DataProtectionCredentialStore>();
        services.AddScoped<IBrokerHealthProbe, RabbitMqBrokerHealthProbe>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        return services;
    }

    /// <summary>Registers the notification channels and the SMTP settings they read (M7.5).</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureSmtp">How to reach an SMTP server.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <remarks>
    /// <b>Both channels are registered whether or not they are configured.</b> A channel is
    /// only reached when a subscription names it, and an unconfigured one fails loudly with the
    /// reason recorded on the message — which is far better than a subscription that can be
    /// created, looks correct in every listing, and resolves to nothing at delivery time.
    /// </remarks>
    public static IServiceCollection AddConcordatNotifications(
        this IServiceCollection services, Action<SmtpOptions>? configureSmtp = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.Configure(configureSmtp ?? (_ => { }));

        services.AddScoped<INotificationChannel, EmailNotificationChannel>();

        // A typed client: a webhook talks to arbitrary third-party endpoints, and a shared
        // handler pool with a bounded timeout is what stops one slow receiver from exhausting
        // sockets or holding the pump open.
        services.AddHttpClient<WebhookNotificationChannel>(
            client => client.Timeout = TimeSpan.FromSeconds(10));

        services.AddScoped<INotificationChannel>(
            provider => provider.GetRequiredService<WebhookNotificationChannel>());

        return services;
    }
}
