using Concordat.Application.Abstractions;
using Concordat.Application.Registry;
using Concordat.Domain.Contracts;
using Concordat.Domain.Governance;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;

namespace Concordat.Application.Governance;

/// <summary>
/// Declares a service's producer and consumer intent (M7.4).
/// </summary>
/// <param name="EnvironmentName">Which environment it runs in.</param>
/// <param name="Name">The service name.</param>
/// <param name="Produces">The subjects it publishes.</param>
/// <param name="Consumes">The subjects it reads, and at which versions.</param>
/// <param name="ReportedBy">Who reported it — the SDK, or a CI job.</param>
/// <remarks>
/// An upsert keyed on (environment, name). Fifty pods reporting at startup are one row: the
/// alternative reports fifty affected consumers where there is one, which is worse than
/// reporting none.
/// </remarks>
public sealed record RegisterServiceCommand(
    string? EnvironmentName,
    string? Name,
    IReadOnlyList<SubjectRefInput>? Produces,
    IReadOnlyList<SubjectRefInput>? Consumes,
    string? ReportedBy) : ICommand<ServiceRegistration>;

/// <summary>Lists the services registered in an environment.</summary>
/// <param name="EnvironmentName">Which environment.</param>
public sealed record ListServicesQuery(string? EnvironmentName)
    : IQuery<IReadOnlyList<ServiceRegistration>>;

/// <summary>Reads one service registration.</summary>
/// <param name="EnvironmentName">Which environment.</param>
/// <param name="Name">The service name.</param>
public sealed record GetServiceQuery(string? EnvironmentName, string? Name)
    : IQuery<ServiceRegistration>;

/// <summary>Handles <see cref="RegisterServiceCommand"/>.</summary>
public sealed class RegisterServiceHandler(
    IEnvironmentRepository environments,
    IServiceRegistrationRepository services,
    IAuditLog audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
    : ICommandHandler<RegisterServiceCommand, ServiceRegistration>
{
    /// <inheritdoc />
    public async Task<Result<ServiceRegistration>> HandleAsync(
        RegisterServiceCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var environment = await EnvironmentPolicies.RequireAsync(
            environments, command.EnvironmentName, cancellationToken).ConfigureAwait(false);

        if (environment.IsFailure)
        {
            return Result<ServiceRegistration>.Failure(environment.Error!);
        }

        var actor = ActorId.Create(command.ReportedBy ?? "sdk");
        if (actor.IsFailure)
        {
            return Result<ServiceRegistration>.Failure(actor.Error!);
        }

        // Empty lists are legitimate: a service that has only been instrumented on one side
        // still exists, and refusing it would make partial adoption impossible.
        var produces = GovernancePolicies.ParseSubjects(command.Produces, out var producing);
        if (produces.IsFailure)
        {
            return Result<ServiceRegistration>.Failure(produces.Error!);
        }

        var consumes = GovernancePolicies.ParseSubjects(command.Consumes, out var consuming);
        if (consumes.IsFailure)
        {
            return Result<ServiceRegistration>.Failure(consumes.Error!);
        }

        var now = clock.GetUtcNow();
        var name = command.Name?.Trim() ?? string.Empty;

        var existing = await services.FindAsync(environment.Value.Id, name, cancellationToken)
            .ConfigureAwait(false);

        ServiceRegistration registration;

        if (existing is null)
        {
            var created = ServiceRegistration.Create(
                environment.Value.Id, command.Name, producing, consuming, now);

            if (created.IsFailure)
            {
                return created;
            }

            registration = created.Value;
            services.Add(registration);
        }
        else
        {
            existing.Report(producing, consuming, now);
            registration = existing;
        }

        audit.Append(AuditEntry.Record(
            environment.Value.Id,
            AuditAction.ServiceRegistered,
            actor.Value,
            registration.Name,
            now,
            $"produces {producing.Count}, consumes {consuming.Count}"));

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result<ServiceRegistration>.Success(registration);
    }
}

/// <summary>Handles <see cref="ListServicesQuery"/>.</summary>
public sealed class ListServicesHandler(
    IEnvironmentRepository environments, IServiceRegistrationRepository services)
    : IQueryHandler<ListServicesQuery, IReadOnlyList<ServiceRegistration>>
{
    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<ServiceRegistration>>> HandleAsync(
        ListServicesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var environment = await EnvironmentPolicies.RequireAsync(
            environments, query.EnvironmentName, cancellationToken).ConfigureAwait(false);

        return environment.IsFailure
            ? Result<IReadOnlyList<ServiceRegistration>>.Failure(environment.Error!)
            : Result<IReadOnlyList<ServiceRegistration>>.Success(
                await services.ListAsync(environment.Value.Id, cancellationToken)
                    .ConfigureAwait(false));
    }
}

/// <summary>Handles <see cref="GetServiceQuery"/>.</summary>
public sealed class GetServiceHandler(
    IEnvironmentRepository environments, IServiceRegistrationRepository services)
    : IQueryHandler<GetServiceQuery, ServiceRegistration>
{
    /// <inheritdoc />
    public async Task<Result<ServiceRegistration>> HandleAsync(
        GetServiceQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var environment = await EnvironmentPolicies.RequireAsync(
            environments, query.EnvironmentName, cancellationToken).ConfigureAwait(false);

        if (environment.IsFailure)
        {
            return Result<ServiceRegistration>.Failure(environment.Error!);
        }

        var name = query.Name?.Trim() ?? string.Empty;

        var found = await services.FindAsync(environment.Value.Id, name, cancellationToken)
            .ConfigureAwait(false);

        return found is null
            ? Result<ServiceRegistration>.Failure(
                ConcordatCodes.ServiceNotFound,
                $"No service named '{name}' is registered in " +
                $"'{environment.Value.Name.Value}'.")
            : Result<ServiceRegistration>.Success(found);
    }
}

/// <summary>Parsing shared by the governance handlers.</summary>
internal static class GovernancePolicies
{
    /// <summary>Parses a supplied subject list, where empty is legitimate.</summary>
    /// <remarks>
    /// Unlike <c>ContractPolicies.ParseSubjects</c>, which refuses an empty list: a binding
    /// that carries no subjects governs a route while saying nothing about it, but a service
    /// that produces nothing is just a consumer.
    /// </remarks>
    public static Result ParseSubjects(
        IReadOnlyList<SubjectRefInput>? supplied, out IReadOnlyList<SubjectRef> refs)
    {
        refs = [];

        if (supplied is null || supplied.Count is 0)
        {
            return Result.Success();
        }

        var parsed = new List<SubjectRef>(supplied.Count);

        foreach (var input in supplied)
        {
            var name = SubjectName.Create(input.Subject);
            if (name.IsFailure)
            {
                return Result.Failure(name.Error!);
            }

            var selector = VersionSelector.Parse(input.Selector);
            if (selector.IsFailure)
            {
                return Result.Failure(selector.Error!);
            }

            parsed.Add(new SubjectRef(name.Value, selector.Value));
        }

        refs = parsed;
        return Result.Success();
    }
}
