using Concordat.Application.Abstractions;
using Concordat.Domain.Governance;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Environment = Concordat.Domain.Registry.Environment;

namespace Concordat.Application.Registry;

/// <summary>Creates an environment (M7.1).</summary>
/// <param name="Name">The name that will appear in every route.</param>
/// <param name="Description">What it is for.</param>
/// <param name="CompatibilityMode">The default policy's who-breaks axis, or null.</param>
/// <param name="CompatibilitySurface">The default policy's what-breaks axis, or null.</param>
/// <param name="RegistrationPolicy">Who may register, or null to choose by name.</param>
/// <param name="AllowPreReleaseVersions">Whether pre-release labels are accepted here.</param>
public sealed record CreateEnvironmentCommand(
    string? Name,
    string? Description,
    string? CompatibilityMode,
    string? CompatibilitySurface,
    string? RegistrationPolicy,
    bool AllowPreReleaseVersions = false) : ICommand<Environment>;

/// <summary>Changes an environment's settings.</summary>
/// <param name="Name">Which environment.</param>
/// <param name="Description">A new description, or null to leave it.</param>
/// <param name="CompatibilityMode">A new default mode, or null to leave it.</param>
/// <param name="CompatibilitySurface">A new default surface, or null to leave it.</param>
/// <param name="RegistrationPolicy">A new registration policy, or null to leave it.</param>
/// <param name="AllowPreReleaseVersions">A new pre-release policy, or null to leave it.</param>
public sealed record UpdateEnvironmentCommand(
    string? Name,
    string? Description,
    string? CompatibilityMode,
    string? CompatibilitySurface,
    string? RegistrationPolicy,
    bool? AllowPreReleaseVersions = null) : ICommand<Environment>;

/// <summary>Registers a broker in an environment.</summary>
/// <param name="EnvironmentName">Which environment.</param>
/// <param name="DisplayName">A human label, unique in the environment.</param>
/// <param name="Uri">An <c>amqp</c> or <c>amqps</c> endpoint.</param>
/// <param name="VirtualHost">The virtual host, or null for <c>/</c>.</param>
public sealed record AddBrokerCommand(
    string? EnvironmentName,
    string? DisplayName,
    string? Uri,
    string? VirtualHost) : ICommand<Environment>;

/// <summary>Removes a broker from an environment.</summary>
/// <param name="EnvironmentName">Which environment.</param>
/// <param name="BrokerId">Which broker.</param>
public sealed record RemoveBrokerCommand(string? EnvironmentName, Guid BrokerId)
    : ICommand<Environment>;

/// <summary>Lists environments.</summary>
public sealed record ListEnvironmentsQuery : IQuery<IReadOnlyList<Environment>>;

/// <summary>Reads one environment.</summary>
/// <param name="Name">Its name.</param>
public sealed record GetEnvironmentQuery(string? Name) : IQuery<Environment>;

/// <summary>Handles <see cref="CreateEnvironmentCommand"/>.</summary>
/// <remarks>
/// <para>
/// <b>The id is derived from the name, not generated.</b> Before M7 the
/// <c>/v1/environments/{env}/…</c> routes worked without this aggregate because
/// <c>DerivedEnvironmentResolver</c> hashed the name to a stable id, and every subject ever
/// registered carries one of those ids.
/// </para>
/// <para>
/// Generating a fresh id here would orphan all of them: the subject rows would point at an
/// environment that no longer had a row, and nothing would say so. Adopting the derived id
/// instead makes the aggregate's arrival invisible to existing data — which is why the
/// recorded M7 commitment offered exactly these two options, and this is the one that needs no
/// migration and cannot half-apply.
/// </para>
/// <para>
/// The cost is that environment ids are a function of their names, so two tenants both naming
/// an environment <c>prod</c> would collide on the primary key. That is fine while tenancy is
/// implicit and single, and it is a real constraint on M9 — recorded there rather than
/// discovered there.
/// </para>
/// </remarks>
public sealed class CreateEnvironmentHandler(
    IEnvironmentRepository environments,
    IEnvironmentResolver resolver,
    IAuditLog audit,
    ICallerContext caller,
    IBillingGate billing,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
    : ICommandHandler<CreateEnvironmentCommand, Environment>
{
    /// <inheritdoc />
    public async Task<Result<Environment>> HandleAsync(
        CreateEnvironmentCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var name = EnvironmentName.Create(command.Name);
        if (name.IsFailure)
        {
            return Result<Environment>.Failure(name.Error!);
        }

        var policyParsed = EnvironmentPolicies.ParseCompatibility(
            command.CompatibilityMode, command.CompatibilitySurface, out var policy);

        if (policyParsed.IsFailure)
        {
            return Result<Environment>.Failure(policyParsed.Error!);
        }

        var registrationParsed = EnvironmentPolicies.ParseRegistration(
            command.RegistrationPolicy, out var registration);

        if (registrationParsed.IsFailure)
        {
            return Result<Environment>.Failure(registrationParsed.Error!);
        }

        var existing = await environments.FindAsync(name.Value, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            return Result<Environment>.Failure(
                ConcordatCodes.EnvironmentAlreadyExists,
                $"An environment named '{name.Value}' already exists.");
        }

        // The plan limit is checked here, at the point of creation, and nowhere else. Reads are
        // never refused for a billing reason: the registry is on the delivery path, and a limit
        // that could stop a consumer resolving a schema would turn a billing dispute into a
        // production outage.
        var allowed = await billing.MayCreateAsync(Meter.Environments, cancellationToken)
            .ConfigureAwait(false);

        if (!allowed.Allowed)
        {
            return Result<Environment>.Failure(
                ConcordatCodes.PlanLimitReached,
                $"This organisation is at its plan's limit of {allowed.Limit} environments. " +
                "Upgrade, or retire something you no longer need — nothing already registered " +
                "stops working.");
        }

        var created = Environment.Create(
            command.Name,
            clock.GetUtcNow(),
            command.Description,
            policy,
            registration,
            resolver.Resolve(name.Value.Value),
            command.AllowPreReleaseVersions);

        if (created.IsFailure)
        {
            return Result<Environment>.Failure(created.Error!);
        }

        environments.Add(created.Value);

        audit.Append(AuditEntry.Record(
            created.Value.Id,
            AuditAction.EnvironmentCreated,
            caller.Current.Actor,
            created.Value.Name.Value,
            clock.GetUtcNow(),
            $"registration {created.Value.RegistrationPolicy}, default " +
            $"{WireTokens.For(created.Value.DefaultCompatibilityPolicy.Mode)}/" +
            $"{WireTokens.For(created.Value.DefaultCompatibilityPolicy.Surface)}"));

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result<Environment>.Success(created.Value);
    }
}

/// <summary>Handles <see cref="UpdateEnvironmentCommand"/>.</summary>
public sealed class UpdateEnvironmentHandler(
    IEnvironmentRepository environments,
    IAuditLog audit,
    ICallerContext caller, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<UpdateEnvironmentCommand, Environment>
{
    /// <inheritdoc />
    public async Task<Result<Environment>> HandleAsync(
        UpdateEnvironmentCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var found = await EnvironmentPolicies.RequireAsync(
            environments, command.Name, cancellationToken).ConfigureAwait(false);

        if (found.IsFailure)
        {
            return found;
        }

        var environment = found.Value;

        if (command.Description is not null)
        {
            environment.Describe(command.Description);
        }

        // Both axes or neither: a policy is a pair, and applying half of one would leave the
        // environment in a state nobody asked for.
        if (command.CompatibilityMode is not null || command.CompatibilitySurface is not null)
        {
            var parsed = EnvironmentPolicies.ParseCompatibility(
                command.CompatibilityMode, command.CompatibilitySurface, out var policy);

            if (parsed.IsFailure)
            {
                return Result<Environment>.Failure(parsed.Error!);
            }

            environment.SetDefaultCompatibilityPolicy(policy!.Value);
        }

        if (command.RegistrationPolicy is not null)
        {
            var parsed = EnvironmentPolicies.ParseRegistration(
                command.RegistrationPolicy, out var registration);

            if (parsed.IsFailure)
            {
                return Result<Environment>.Failure(parsed.Error!);
            }

            environment.SetRegistrationPolicy(registration!.Value);
        }

        if (command.AllowPreReleaseVersions is { } allowPreRelease)
        {
            // Turning it off does not invalidate labels already registered. Versions are
            // immutable and their labels are history; re-judging the past on a policy change
            // would make the audit trail disagree with the data.
            environment.SetPreReleasePolicy(allowPreRelease);
        }

        audit.Append(AuditEntry.Record(
            environment.Id,
            AuditAction.EnvironmentUpdated,
            caller.Current.Actor,
            environment.Name.Value,
            clock.GetUtcNow(),
            $"registration {environment.RegistrationPolicy}, default " +
            $"{WireTokens.For(environment.DefaultCompatibilityPolicy.Mode)}/" +
            $"{WireTokens.For(environment.DefaultCompatibilityPolicy.Surface)}"));

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result<Environment>.Success(environment);
    }
}

/// <summary>Handles <see cref="AddBrokerCommand"/>.</summary>
public sealed class AddBrokerHandler(
    IEnvironmentRepository environments,
    IAuditLog audit,
    ICallerContext caller, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<AddBrokerCommand, Environment>
{
    /// <inheritdoc />
    public async Task<Result<Environment>> HandleAsync(
        AddBrokerCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var found = await EnvironmentPolicies.RequireAsync(
            environments, command.EnvironmentName, cancellationToken).ConfigureAwait(false);

        if (found.IsFailure)
        {
            return found;
        }

        var broker = BrokerConnection.Create(
            command.DisplayName, command.Uri, command.VirtualHost);

        if (broker.IsFailure)
        {
            return Result<Environment>.Failure(broker.Error!);
        }

        var added = found.Value.AddBroker(broker.Value);
        if (added.IsFailure)
        {
            return Result<Environment>.Failure(added.Error!);
        }

        audit.Append(AuditEntry.Record(
            found.Value.Id,
            AuditAction.BrokerAdded,
            caller.Current.Actor,
            broker.Value.DisplayName,
            clock.GetUtcNow(),
            $"{broker.Value.Uri} vhost {broker.Value.VirtualHost}"));

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result<Environment>.Success(found.Value);
    }
}

/// <summary>Handles <see cref="RemoveBrokerCommand"/>.</summary>
public sealed class RemoveBrokerHandler(
    IEnvironmentRepository environments,
    IAuditLog audit,
    ICallerContext caller, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<RemoveBrokerCommand, Environment>
{
    /// <inheritdoc />
    public async Task<Result<Environment>> HandleAsync(
        RemoveBrokerCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var found = await EnvironmentPolicies.RequireAsync(
            environments, command.EnvironmentName, cancellationToken).ConfigureAwait(false);

        if (found.IsFailure)
        {
            return found;
        }

        // Captured before removal: afterwards there is nothing left to name in the entry, and
        // "which broker was removed" is the only thing anyone will ask this row.
        var label = found.Value.Brokers
            .FirstOrDefault(b => b.Id == command.BrokerId)?.DisplayName
            ?? command.BrokerId.ToString();

        var removed = found.Value.RemoveBroker(command.BrokerId);
        if (removed.IsFailure)
        {
            return Result<Environment>.Failure(removed.Error!);
        }

        audit.Append(AuditEntry.Record(
            found.Value.Id,
            AuditAction.BrokerRemoved,
            caller.Current.Actor,
            label,
            clock.GetUtcNow()));

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result<Environment>.Success(found.Value);
    }
}

/// <summary>Handles <see cref="ListEnvironmentsQuery"/>.</summary>
public sealed class ListEnvironmentsHandler(IEnvironmentRepository environments)
    : IQueryHandler<ListEnvironmentsQuery, IReadOnlyList<Environment>>
{
    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<Environment>>> HandleAsync(
        ListEnvironmentsQuery query, CancellationToken cancellationToken) =>
        Result<IReadOnlyList<Environment>>.Success(
            await environments.ListAsync(cancellationToken).ConfigureAwait(false));
}

/// <summary>Handles <see cref="GetEnvironmentQuery"/>.</summary>
public sealed class GetEnvironmentHandler(IEnvironmentRepository environments)
    : IQueryHandler<GetEnvironmentQuery, Environment>
{
    /// <inheritdoc />
    public Task<Result<Environment>> HandleAsync(
        GetEnvironmentQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return EnvironmentPolicies.RequireAsync(environments, query.Name, cancellationToken);
    }
}

/// <summary>Parsing shared by the environment handlers.</summary>
internal static class EnvironmentPolicies
{
    /// <summary>Loads an environment by name, or fails with a code the API can map.</summary>
    public static async Task<Result<Environment>> RequireAsync(
        IEnvironmentRepository environments, string? name, CancellationToken cancellationToken)
    {
        var parsed = EnvironmentName.Create(name);
        if (parsed.IsFailure)
        {
            return Result<Environment>.Failure(parsed.Error!);
        }

        var environment = await environments.FindAsync(parsed.Value, cancellationToken)
            .ConfigureAwait(false);

        return environment is null
            ? Result<Environment>.Failure(
                ConcordatCodes.EnvironmentNotFound,
                $"No environment named '{parsed.Value.Value}'.")
            : Result<Environment>.Success(environment);
    }

    /// <summary>Parses a policy pair, where both members are optional together.</summary>
    public static Result ParseCompatibility(
        string? mode, string? surface, out CompatibilityPolicy? policy)
    {
        policy = null;

        if (mode is null && surface is null)
        {
            return Result.Success();
        }

        if (mode is null || surface is null)
        {
            return Result.Failure(
                ConcordatCodes.EnvironmentNameInvalid,
                "A compatibility policy needs both a mode and a surface (ADR-016).");
        }

        if (!Enum.TryParse<CompatibilityMode>(
                mode.Replace("_", "", StringComparison.Ordinal), true, out var parsedMode))
        {
            return Result.Failure(
                ConcordatCodes.EnvironmentNameInvalid, $"Unknown compatibility mode '{mode}'.");
        }

        if (!Enum.TryParse<CompatibilitySurface>(
                surface.Replace("_", "", StringComparison.Ordinal), true, out var parsedSurface))
        {
            return Result.Failure(
                ConcordatCodes.EnvironmentNameInvalid, $"Unknown compatibility surface '{surface}'.");
        }

        policy = new CompatibilityPolicy(parsedMode, parsedSurface);
        return Result.Success();
    }

    /// <summary>Parses a registration policy.</summary>
    public static Result ParseRegistration(string? value, out RegistrationPolicy? policy)
    {
        policy = null;

        if (value is null)
        {
            return Result.Success();
        }

        if (!Enum.TryParse<RegistrationPolicy>(
                value.Replace("_", "", StringComparison.Ordinal), true, out var parsed))
        {
            return Result.Failure(
                ConcordatCodes.EnvironmentNameInvalid,
                $"Unknown registration policy '{value}'. Expected OPEN, CI_ONLY or CLOSED.");
        }

        policy = parsed;
        return Result.Success();
    }
}

/// <summary>Runs a health check against one broker and records the outcome.</summary>
/// <param name="EnvironmentName">Which environment.</param>
/// <param name="BrokerId">Which broker.</param>
public sealed record CheckBrokerCommand(string? EnvironmentName, Guid BrokerId)
    : ICommand<Environment>;

/// <summary>Handles <see cref="CheckBrokerCommand"/>.</summary>
/// <remarks>
/// The outcome is persisted rather than merely returned, so the answer survives the request
/// and an operator listing environments sees when each broker was last reachable without
/// re-probing every one of them.
/// </remarks>
public sealed class CheckBrokerHandler(
    IEnvironmentRepository environments,
    IBrokerHealthProbe probe,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
    : ICommandHandler<CheckBrokerCommand, Environment>
{
    /// <inheritdoc />
    public async Task<Result<Environment>> HandleAsync(
        CheckBrokerCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var found = await EnvironmentPolicies.RequireAsync(
            environments, command.EnvironmentName, cancellationToken).ConfigureAwait(false);

        if (found.IsFailure)
        {
            return found;
        }

        var broker = found.Value.Broker(command.BrokerId);
        if (broker is null)
        {
            return Result<Environment>.Failure(
                ConcordatCodes.BrokerNotFound, "No broker with that id in this environment.");
        }

        var result = await probe.ProbeAsync(broker, cancellationToken).ConfigureAwait(false);

        broker.RecordCheck(result.Reachable, clock.GetUtcNow(), result.Error);

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result<Environment>.Success(found.Value);
    }
}

/// <summary>Sets or replaces a broker's credentials (M7.2).</summary>
/// <param name="EnvironmentName">Which environment.</param>
/// <param name="BrokerId">Which broker.</param>
/// <param name="Username">The AMQP user.</param>
/// <param name="Password">Its password.</param>
public sealed record SetBrokerCredentialCommand(
    string? EnvironmentName,
    Guid BrokerId,
    string? Username,
    string? Password) : ICommand<Environment>;

/// <summary>Removes a broker's stored credentials.</summary>
/// <param name="EnvironmentName">Which environment.</param>
/// <param name="BrokerId">Which broker.</param>
public sealed record RemoveBrokerCredentialCommand(string? EnvironmentName, Guid BrokerId)
    : ICommand<Environment>;

/// <summary>Handles <see cref="SetBrokerCredentialCommand"/>.</summary>
/// <remarks>
/// The secret goes straight from the command into the store and is never assigned to the
/// aggregate. What the broker records is the reference, which is what makes returning the
/// updated environment from a write endpoint safe.
/// </remarks>
public sealed class SetBrokerCredentialHandler(
    IEnvironmentRepository environments,
    ICredentialStore credentials,
    IAuditLog audit,
    ICallerContext caller,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
    : ICommandHandler<SetBrokerCredentialCommand, Environment>
{
    /// <inheritdoc />
    public async Task<Result<Environment>> HandleAsync(
        SetBrokerCredentialCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.Username) ||
            string.IsNullOrWhiteSpace(command.Password))
        {
            return Result<Environment>.Failure(
                ConcordatCodes.CredentialInvalid,
                "A broker credential needs both a username and a password.");
        }

        var found = await EnvironmentPolicies.RequireAsync(
            environments, command.EnvironmentName, cancellationToken).ConfigureAwait(false);

        if (found.IsFailure)
        {
            return found;
        }

        var broker = found.Value.Broker(command.BrokerId);
        if (broker is null)
        {
            return Result<Environment>.Failure(
                ConcordatCodes.BrokerNotFound, "No broker with that id in this environment.");
        }

        var reference = await credentials.StoreAsync(
            new BrokerCredential(command.Username, command.Password),
            broker.CredentialRef,
            cancellationToken).ConfigureAwait(false);

        broker.SetCredentialRef(reference);

        // The detail carries the broker and nothing else. Not the username, not the reference,
        // and obviously not the secret: this is the one entry whose whole purpose is to record
        // that a credential changed, in a table designed to be read widely.
        audit.Append(AuditEntry.Record(
            found.Value.Id,
            AuditAction.BrokerCredentialSet,
            caller.Current.Actor,
            broker.DisplayName,
            clock.GetUtcNow()));

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result<Environment>.Success(found.Value);
    }
}

/// <summary>Handles <see cref="RemoveBrokerCredentialCommand"/>.</summary>
public sealed class RemoveBrokerCredentialHandler(
    IEnvironmentRepository environments,
    ICredentialStore credentials,
    IAuditLog audit,
    ICallerContext caller,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
    : ICommandHandler<RemoveBrokerCredentialCommand, Environment>
{
    /// <inheritdoc />
    public async Task<Result<Environment>> HandleAsync(
        RemoveBrokerCredentialCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var found = await EnvironmentPolicies.RequireAsync(
            environments, command.EnvironmentName, cancellationToken).ConfigureAwait(false);

        if (found.IsFailure)
        {
            return found;
        }

        var broker = found.Value.Broker(command.BrokerId);
        if (broker is null)
        {
            return Result<Environment>.Failure(
                ConcordatCodes.BrokerNotFound, "No broker with that id in this environment.");
        }

        if (broker.CredentialRef is { } reference)
        {
            // The stored secret goes first. Clearing the reference alone would leave an
            // unreachable ciphertext row behind for as long as the database lives.
            await credentials.RemoveAsync(reference, cancellationToken).ConfigureAwait(false);
            broker.SetCredentialRef(null);

            audit.Append(AuditEntry.Record(
                found.Value.Id,
                AuditAction.BrokerCredentialRemoved,
                caller.Current.Actor,
                broker.DisplayName,
                clock.GetUtcNow()));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result<Environment>.Success(found.Value);
    }
}
