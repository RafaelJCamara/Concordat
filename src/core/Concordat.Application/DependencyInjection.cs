using Concordat.Application.Abstractions;
using Concordat.Application.Registry;
using Concordat.Domain.Registry;
using Concordat.Formats.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Concordat.Application;

/// <summary>
/// Resolves per-format services from whatever implementations are registered.
/// </summary>
/// <remarks>
/// Formats are registered at the composition root, so the Application layer never references
/// a concrete format. Asking for one that is not registered fails loudly rather than falling
/// back to JSON — a silent fallback would produce a confidently wrong verdict for an Avro
/// schema.
/// </remarks>
public sealed class SchemaFormatRegistry(
    IEnumerable<ISchemaCanonicalizer> canonicalizers,
    IEnumerable<ICompatibilityChecker> checkers,
    IEnumerable<ISchemaReferenceExtractor> extractors,
    IEnumerable<ISchemaPortabilityChecker> portability) : ISchemaFormatRegistry
{
    /// <inheritdoc />
    public ISchemaPortabilityChecker? PortabilityChecker(SchemaFormat format) =>
        portability.FirstOrDefault(p => p.Format == format);

    /// <inheritdoc />
    public ISchemaCanonicalizer Canonicalizer(SchemaFormat format) =>
        canonicalizers.FirstOrDefault(c => c.Format == format)
        ?? throw new NotSupportedException($"No canonicaliser registered for {format}.");

    /// <inheritdoc />
    public ICompatibilityChecker Checker(SchemaFormat format) =>
        checkers.FirstOrDefault(c => c.Format == format)
        ?? throw new NotSupportedException($"No compatibility checker registered for {format}.");

    /// <inheritdoc />
    public ISchemaReferenceExtractor ReferenceExtractor(SchemaFormat format) =>
        extractors.FirstOrDefault(e => e.Format == format)
        ?? throw new NotSupportedException($"No reference extractor registered for {format}.");
}

/// <summary>Resolves bundlers from whatever implementations are registered.</summary>
/// <param name="bundlers">The registered bundlers.</param>
public sealed class SchemaBundlerRegistry(IEnumerable<ISchemaBundler> bundlers)
    : ISchemaBundlerRegistry
{
    /// <inheritdoc />
    public ISchemaBundler Bundler(SchemaFormat format) =>
        bundlers.FirstOrDefault(b => b.Format == format)
        ?? throw new NotSupportedException($"No bundler registered for {format}.");
}

/// <summary>Registration helpers for the application layer.</summary>
public static class DependencyInjection
{
    /// <summary>Registers the dispatcher, the evaluator and every handler.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddConcordatApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IDispatcher, Dispatcher>();
        services.AddScoped<ISchemaFormatRegistry, SchemaFormatRegistry>();
        services.AddScoped<ISchemaBundlerRegistry, SchemaBundlerRegistry>();
        services.AddScoped<ICompatibilityEvaluator, CompatibilityEvaluator>();

        services.AddScoped<
            ICommandHandler<RegisterVersionCommand, RegisterVersionResult>, RegisterVersionHandler>();
        services.AddScoped<ICommandHandler<CreateSubjectCommand, Subject>, CreateSubjectHandler>();

        // M7.1 environments and brokers.
        services.AddScoped<
            ICommandHandler<CreateEnvironmentCommand, Domain.Registry.Environment>,
            CreateEnvironmentHandler>();
        services.AddScoped<
            ICommandHandler<UpdateEnvironmentCommand, Domain.Registry.Environment>,
            UpdateEnvironmentHandler>();
        services.AddScoped<
            ICommandHandler<AddBrokerCommand, Domain.Registry.Environment>, AddBrokerHandler>();
        services.AddScoped<
            ICommandHandler<RemoveBrokerCommand, Domain.Registry.Environment>, RemoveBrokerHandler>();
        services.AddScoped<
            ICommandHandler<CheckBrokerCommand, Domain.Registry.Environment>, CheckBrokerHandler>();
        services.AddScoped<
            IQueryHandler<GetEnvironmentQuery, Domain.Registry.Environment>, GetEnvironmentHandler>();
        services.AddScoped<
            IQueryHandler<ListEnvironmentsQuery, IReadOnlyList<Domain.Registry.Environment>>,
            ListEnvironmentsHandler>();
        services.AddScoped<ICommandHandler<DecideVersionCommand, Subject>, DecideVersionHandler>();
        services.AddScoped<ICommandHandler<UpdateSubjectCommand, Subject>, UpdateSubjectHandler>();
        services.AddScoped<ICommandHandler<RetireSubjectCommand, Subject>, RetireSubjectHandler>();

        services.AddScoped<IQueryHandler<GetSubjectQuery, Subject>, GetSubjectHandler>();
        services.AddScoped<
            IQueryHandler<ListSubjectsQuery, IReadOnlyList<Subject>>, ListSubjectsHandler>();
        services.AddScoped<
            IQueryHandler<CheckCompatibilityQuery, CompatibilityCheckResult>, CheckCompatibilityHandler>();
        services.AddScoped<IQueryHandler<GetSchemaQuery, Schema>, GetSchemaHandler>();
        services.AddScoped<
            IQueryHandler<GetBundledSchemaQuery, BundledSchema>, GetBundledSchemaHandler>();
        services.AddScoped<
            IQueryHandler<GetSchemaUsagesQuery, IReadOnlyList<SchemaUsage>>, GetSchemaUsagesHandler>();
        services.AddScoped<IQueryHandler<DiffVersionsQuery, DiffResult>, DiffVersionsHandler>();
        services.AddScoped<IQueryHandler<BootstrapQuery, BootstrapResult>, BootstrapHandler>();

        return services;
    }
}
