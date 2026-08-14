using Concordat.Application.Abstractions;
using Concordat.Domain.Registry;
using Concordat.Infrastructure.Billing;
using Concordat.Infrastructure.Identity;
using Concordat.Infrastructure.Notifications;
using Concordat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Concordat.Infrastructure;

/// <summary>Registration helpers for the infrastructure layer.</summary>
public static class DependencyInjection
{
    /// <summary>Registers the database context and everything the chosen profile implies.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">A PostgreSQL connection string.</param>
    /// <param name="profile">Which deployment flavour this process is (M9.1).</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <remarks>
    /// <b>The profile is read here and nowhere else.</b> DESIGN §10 asks for one image serving
    /// both flavours, and the way that stays true is that the difference is a registration
    /// rather than a branch — query code that tested a flag would eventually test it wrongly,
    /// and the failure mode of getting tenancy wrong is silently serving another organisation's
    /// data.
    /// </remarks>
    public static IServiceCollection AddConcordatPersistence(
        this IServiceCollection services,
        string connectionString,
        ConcordatProfile profile = ConcordatProfile.SelfHosted)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Scoped in both flavours, even though the self-hosted answer never changes: a
        // singleton here would be a lifetime that has to be revisited the day Cloud is
        // switched on, and revisiting a lifetime under time pressure is how a request ends up
        // holding another request's tenant.
        if (profile is ConcordatProfile.Cloud)
        {
            services.AddScoped<ITenantContext, CallerTenantContext>();
            services.AddScoped<IBillingGate, MeteredBillingGate>();
        }
        else
        {
            services.AddScoped<ITenantContext, SingleTenantContext>();

            // Not a stub: ADR-009 puts every feature under Apache-2.0 and a self-hosted
            // install has nobody to bill, so "yes" is the honest answer rather than a
            // placeholder for one.
            services.AddScoped<IBillingGate, UnmeteredBillingGate>();
        }

        services.AddDbContext<ConcordatDbContext>(options => options.UseNpgsql(connectionString));

        services.AddScoped<ISubjectRepository, SubjectRepository>();
        services.AddScoped<ISchemaRepository, SchemaRepository>();
        services.AddScoped<IEnvironmentRepository, EnvironmentRepository>();
        services.AddScoped<IContractRepository, ContractRepository>();
        services.AddScoped<IServiceRegistrationRepository, ServiceRegistrationRepository>();
        services.AddScoped<IAuditLog, AuditLog>();
        services.AddScoped<IDeploymentLog, DeploymentLog>();
        services.AddScoped<IOutbox, Outbox>();
        services.AddScoped<ISubscriptionRepository, SubscriptionRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<IBillingSubscriptionRepository, BillingSubscriptionRepository>();
        services.AddScoped<IUsageMeter, UsageMeter>();
        services.AddScoped<IApiKeyRepository, ApiKeyRepository>();

        // Singleton: it holds no state beyond the derived dummy hash, and that is exactly the
        // thing that must be computed once rather than per request.
        services.AddSingleton<IPasswordHasher, AspNetPasswordHasher>();
        services.AddScoped<ICredentialStore, DataProtectionCredentialStore>();
        services.AddScoped<IBrokerHealthProbe, RabbitMqBrokerHealthProbe>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        // Scoped, not singleton: it reads the current tenant, and a singleton would answer with
        // whichever organisation happened to be in scope when it was first resolved.
        services.AddScoped<IEnvironmentResolver, DerivedEnvironmentResolver>();

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
