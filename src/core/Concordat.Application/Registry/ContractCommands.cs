using Concordat.Application.Abstractions;
using Concordat.Domain.Contracts;
using Concordat.Domain.Governance;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;

namespace Concordat.Application.Registry;

/// <summary>A subject and version selector, as supplied over the wire.</summary>
/// <param name="Subject">The subject name.</param>
/// <param name="Selector"><c>latest</c>, an ordinal, or <c>&gt;=N</c>.</param>
public sealed record SubjectRefInput(string? Subject, string? Selector);

/// <summary>Creates a contract.</summary>
/// <param name="EnvironmentName">Which environment.</param>
/// <param name="Name">A name unique within the environment.</param>
/// <param name="Enforcement"><c>OFF</c>, <c>MONITOR</c> or <c>ENFORCE</c>; defaults to MONITOR.</param>
public sealed record CreateContractCommand(
    string? EnvironmentName, string? Name, string? Enforcement) : ICommand<Contract>;

/// <summary>Adds a publish binding to a contract.</summary>
/// <param name="EnvironmentName">Which environment.</param>
/// <param name="ContractName">Which contract.</param>
/// <param name="Exchange">The exchange.</param>
/// <param name="RoutingKeyPattern">Which routing keys it governs.</param>
/// <param name="Subjects">The subjects permitted.</param>
/// <param name="BrokerId">A specific broker, or null for every broker in the environment.</param>
/// <param name="VirtualHost">The virtual host; defaults to <c>/</c>.</param>
/// <param name="Precedence">Which binding wins when two overlap.</param>
public sealed record AddPublishBindingCommand(
    string? EnvironmentName,
    string? ContractName,
    string? Exchange,
    string? RoutingKeyPattern,
    IReadOnlyList<SubjectRefInput>? Subjects,
    Guid? BrokerId,
    string? VirtualHost,
    int? Precedence) : ICommand<Contract>;

/// <summary>Adds a consume binding to a contract.</summary>
/// <param name="EnvironmentName">Which environment.</param>
/// <param name="ContractName">Which contract.</param>
/// <param name="Queue">The queue.</param>
/// <param name="Subjects">The subjects expected.</param>
/// <param name="BrokerId">A specific broker, or null for every broker in the environment.</param>
/// <param name="VirtualHost">The virtual host; defaults to <c>/</c>.</param>
public sealed record AddConsumeBindingCommand(
    string? EnvironmentName,
    string? ContractName,
    string? Queue,
    IReadOnlyList<SubjectRefInput>? Subjects,
    Guid? BrokerId,
    string? VirtualHost) : ICommand<Contract>;

/// <summary>Changes how much a contract may do.</summary>
/// <param name="EnvironmentName">Which environment.</param>
/// <param name="ContractName">Which contract.</param>
/// <param name="Enforcement">The new mode.</param>
public sealed record SetContractEnforcementCommand(
    string? EnvironmentName, string? ContractName, string? Enforcement) : ICommand<Contract>;

/// <summary>Lists an environment's contracts.</summary>
/// <param name="EnvironmentName">Which environment.</param>
public sealed record ListContractsQuery(string? EnvironmentName)
    : IQuery<IReadOnlyList<Contract>>;

/// <summary>Reads one contract.</summary>
/// <param name="EnvironmentName">Which environment.</param>
/// <param name="ContractName">Which contract.</param>
public sealed record GetContractQuery(string? EnvironmentName, string? ContractName)
    : IQuery<Contract>;

// ------------------------------------------------------------------------ resolution

/// <summary>One publish route an SDK wants resolved.</summary>
/// <param name="Exchange">The exchange it publishes to.</param>
/// <param name="RoutingKey">A concrete routing key, not a pattern.</param>
public sealed record PublishTarget(string? Exchange, string? RoutingKey);

/// <summary>What the environment's contracts say about one route or queue.</summary>
/// <param name="Contracts">
/// Every contract that matched, in name order. Empty when nothing governs the route.
/// </param>
/// <param name="Enforcement">
/// The <b>strictest</b> enforcement among the matches, or <c>Off</c> when nothing matched.
/// </param>
/// <param name="Subjects">The union of what the matching contracts permit.</param>
/// <remarks>
/// <para>
/// <b>All of them, not the first one</b> (decision 21). The overlap invariant holds
/// <em>within</em> a contract — two bindings that intersect and carry different subjects are
/// refused unless precedence separates them — and there was never a check across contracts.
/// Nothing stopped <c>orders-v1</c> and <c>orders-legacy</c> from both binding
/// <c>orders.created</c> to different subjects, and resolve answered with whichever sorted
/// first by name. That is the arbitrary outcome the within-contract invariant exists to
/// prevent, one level up, and a publisher could not tell it was happening.
/// </para>
/// <para>
/// Reporting all of them was chosen over checking at binding time, which would have made
/// authoring a contract an environment-wide operation needing a story for concurrent writers.
/// The ambiguity is surfaced where the topology is actually known instead.
/// </para>
/// <para>
/// <b>Strictest enforcement, union of subjects</b> is the combination that fails safe in both
/// directions while the ambiguity stands: taking the loosest mode would let an authoring
/// mistake quietly switch enforcement off, and intersecting the subjects would refuse a
/// publisher that one contract plainly permits. Neither silently picks a winner.
/// </para>
/// </remarks>
public sealed record ResolvedBinding(
    IReadOnlyList<string> Contracts,
    EnforcementMode Enforcement,
    IReadOnlyList<SubjectRef> Subjects)
{
    /// <summary>Whether any contract governs the route.</summary>
    public bool IsGoverned => Contracts.Count > 0;

    /// <summary>Whether more than one contract governs it, which nobody intended.</summary>
    public bool IsAmbiguous => Contracts.Count > 1;
}

/// <summary>Everything an SDK asked about, answered.</summary>
/// <param name="Publishes">One entry per requested route, in request order.</param>
/// <param name="Consumes">One entry per requested queue, in request order.</param>
public sealed record ResolvedContracts(
    IReadOnlyList<ResolvedBinding> Publishes,
    IReadOnlyList<ResolvedBinding> Consumes);

/// <summary>
/// Resolves an SDK's whole topology against the environment's contracts in one call.
/// </summary>
/// <param name="EnvironmentName">Which environment.</param>
/// <param name="BrokerId">Which broker the SDK is connected to.</param>
/// <param name="VirtualHost">Its virtual host.</param>
/// <param name="Publishes">The routes it publishes on.</param>
/// <param name="Consumes">The queues it consumes from.</param>
/// <remarks>
/// One request rather than one per route, for the same reason bootstrap exists: a fleet-wide
/// restart is the real load pattern, and an SDK that asked per route would multiply that by its
/// topology size against a registry that sits on the delivery path.
/// </remarks>
public sealed record ResolveContractsQuery(
    string? EnvironmentName,
    Guid? BrokerId,
    string? VirtualHost,
    IReadOnlyList<PublishTarget>? Publishes,
    IReadOnlyList<string>? Consumes) : IQuery<ResolvedContracts>;

/// <summary>Handles <see cref="CreateContractCommand"/>.</summary>
public sealed class CreateContractHandler(
    IEnvironmentRepository environments,
    IContractRepository contracts,
    IAuditLog audit,
    ICallerContext caller,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
    : ICommandHandler<CreateContractCommand, Contract>
{
    /// <inheritdoc />
    public async Task<Result<Contract>> HandleAsync(
        CreateContractCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var environment = await EnvironmentPolicies.RequireAsync(
            environments, command.EnvironmentName, cancellationToken).ConfigureAwait(false);

        if (environment.IsFailure)
        {
            return Result<Contract>.Failure(environment.Error!);
        }

        var enforcement = ContractPolicies.ParseEnforcement(command.Enforcement, out var mode);
        if (enforcement.IsFailure)
        {
            return Result<Contract>.Failure(enforcement.Error!);
        }

        var existing = await contracts.FindAsync(
            environment.Value.Id, command.Name?.Trim() ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            return Result<Contract>.Failure(
                ConcordatCodes.ContractAlreadyExists,
                $"A contract named '{command.Name}' already exists in this environment.");
        }

        var created = Contract.Create(
            environment.Value.Id, command.Name, clock.GetUtcNow(), mode);

        if (created.IsFailure)
        {
            return created;
        }

        contracts.Add(created.Value);

        audit.Append(AuditEntry.Record(
            environment.Value.Id,
            AuditAction.ContractCreated,
            caller.Current.Actor,
            created.Value.Name,
            clock.GetUtcNow(),
            WireTokens.For(created.Value.Enforcement)));

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return created;
    }
}

/// <summary>Handles <see cref="AddPublishBindingCommand"/>.</summary>
public sealed class AddPublishBindingHandler(
    IEnvironmentRepository environments,
    IContractRepository contracts,
    IAuditLog audit,
    ICallerContext caller,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
    : ICommandHandler<AddPublishBindingCommand, Contract>
{
    /// <inheritdoc />
    public async Task<Result<Contract>> HandleAsync(
        AddPublishBindingCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var found = await ContractPolicies.RequireAsync(
            environments, contracts, command.EnvironmentName, command.ContractName,
            cancellationToken).ConfigureAwait(false);

        if (found.IsFailure)
        {
            return found;
        }

        if (string.IsNullOrWhiteSpace(command.Exchange))
        {
            return Result<Contract>.Failure(
                ConcordatCodes.ContractNameInvalid, "An exchange is required.");
        }

        var pattern = RoutingKeyPattern.Create(command.RoutingKeyPattern);
        if (pattern.IsFailure)
        {
            return Result<Contract>.Failure(pattern.Error!);
        }

        var subjects = ContractPolicies.ParseSubjects(command.Subjects, out var refs);
        if (subjects.IsFailure)
        {
            return Result<Contract>.Failure(subjects.Error!);
        }

        var added = found.Value.AddPublish(new PublishBinding(
            new TopologyScope(command.BrokerId, ContractPolicies.Vhost(command.VirtualHost)),
            command.Exchange.Trim(),
            pattern.Value,
            refs,
            command.Precedence));

        if (added.IsFailure)
        {
            return Result<Contract>.Failure(added.Error!);
        }

        audit.Append(AuditEntry.Record(
            found.Value.EnvironmentId,
            AuditAction.ContractBindingAdded,
            caller.Current.Actor,
            found.Value.Name,
            clock.GetUtcNow(),
            $"publish {command.Exchange.Trim()} / {pattern.Value} -> " +
            $"{string.Join(", ", refs.Select(r => $"{r.Subject.Value}@{r.Selector}"))}"));

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return found;
    }
}

/// <summary>Handles <see cref="AddConsumeBindingCommand"/>.</summary>
public sealed class AddConsumeBindingHandler(
    IEnvironmentRepository environments,
    IContractRepository contracts,
    IAuditLog audit,
    ICallerContext caller,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
    : ICommandHandler<AddConsumeBindingCommand, Contract>
{
    /// <inheritdoc />
    public async Task<Result<Contract>> HandleAsync(
        AddConsumeBindingCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var found = await ContractPolicies.RequireAsync(
            environments, contracts, command.EnvironmentName, command.ContractName,
            cancellationToken).ConfigureAwait(false);

        if (found.IsFailure)
        {
            return found;
        }

        if (string.IsNullOrWhiteSpace(command.Queue))
        {
            return Result<Contract>.Failure(
                ConcordatCodes.ContractNameInvalid, "A queue is required.");
        }

        var subjects = ContractPolicies.ParseSubjects(command.Subjects, out var refs);
        if (subjects.IsFailure)
        {
            return Result<Contract>.Failure(subjects.Error!);
        }

        var added = found.Value.AddConsume(new ConsumeBinding(
            new TopologyScope(command.BrokerId, ContractPolicies.Vhost(command.VirtualHost)),
            command.Queue.Trim(),
            refs));

        if (added.IsFailure)
        {
            return Result<Contract>.Failure(added.Error!);
        }

        audit.Append(AuditEntry.Record(
            found.Value.EnvironmentId,
            AuditAction.ContractBindingAdded,
            caller.Current.Actor,
            found.Value.Name,
            clock.GetUtcNow(),
            $"consume {command.Queue.Trim()} -> " +
            $"{string.Join(", ", refs.Select(r => $"{r.Subject.Value}@{r.Selector}"))}"));

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return found;
    }
}

/// <summary>Handles <see cref="SetContractEnforcementCommand"/>.</summary>
public sealed class SetContractEnforcementHandler(
    IEnvironmentRepository environments,
    IContractRepository contracts,
    IAuditLog audit,
    ICallerContext caller,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
    : ICommandHandler<SetContractEnforcementCommand, Contract>
{
    /// <inheritdoc />
    public async Task<Result<Contract>> HandleAsync(
        SetContractEnforcementCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var found = await ContractPolicies.RequireAsync(
            environments, contracts, command.EnvironmentName, command.ContractName,
            cancellationToken).ConfigureAwait(false);

        if (found.IsFailure)
        {
            return found;
        }

        var parsed = ContractPolicies.ParseEnforcement(command.Enforcement, out var mode);
        if (parsed.IsFailure)
        {
            return Result<Contract>.Failure(parsed.Error!);
        }

        found.Value.SetEnforcement(mode ?? EnforcementMode.Monitor);

        audit.Append(AuditEntry.Record(
            found.Value.EnvironmentId,
            AuditAction.ContractEnforcementChanged,
            caller.Current.Actor,
            found.Value.Name,
            clock.GetUtcNow(),
            WireTokens.For(found.Value.Enforcement)));

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return found;
    }
}

/// <summary>Handles <see cref="ListContractsQuery"/>.</summary>
public sealed class ListContractsHandler(
    IEnvironmentRepository environments, IContractRepository contracts)
    : IQueryHandler<ListContractsQuery, IReadOnlyList<Contract>>
{
    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<Contract>>> HandleAsync(
        ListContractsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var environment = await EnvironmentPolicies.RequireAsync(
            environments, query.EnvironmentName, cancellationToken).ConfigureAwait(false);

        return environment.IsFailure
            ? Result<IReadOnlyList<Contract>>.Failure(environment.Error!)
            : Result<IReadOnlyList<Contract>>.Success(
                await contracts.ListAsync(environment.Value.Id, cancellationToken)
                    .ConfigureAwait(false));
    }
}

/// <summary>Handles <see cref="GetContractQuery"/>.</summary>
public sealed class GetContractHandler(
    IEnvironmentRepository environments, IContractRepository contracts)
    : IQueryHandler<GetContractQuery, Contract>
{
    /// <inheritdoc />
    public Task<Result<Contract>> HandleAsync(
        GetContractQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return ContractPolicies.RequireAsync(
            environments, contracts, query.EnvironmentName, query.ContractName, cancellationToken);
    }
}

/// <summary>Handles <see cref="ResolveContractsQuery"/>.</summary>
/// <remarks>
/// <b>An unmatched route is answered, not omitted.</b> The SDK needs to know the difference
/// between "nothing governs this" and "I forgot to ask" — the first is a normal brownfield
/// state that should publish unenforced, and silently dropping the entry would make them
/// indistinguishable.
/// </remarks>
public sealed class ResolveContractsHandler(
    IEnvironmentRepository environments, IContractRepository contracts)
    : IQueryHandler<ResolveContractsQuery, ResolvedContracts>
{
    /// <inheritdoc />
    public async Task<Result<ResolvedContracts>> HandleAsync(
        ResolveContractsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var environment = await EnvironmentPolicies.RequireAsync(
            environments, query.EnvironmentName, cancellationToken).ConfigureAwait(false);

        if (environment.IsFailure)
        {
            return Result<ResolvedContracts>.Failure(environment.Error!);
        }

        var all = await contracts.ListAsync(environment.Value.Id, cancellationToken)
            .ConfigureAwait(false);

        var vhost = ContractPolicies.Vhost(query.VirtualHost);

        // A null broker means the caller did not say which one it is on. Matching against an
        // empty Guid would silently fail every scope that names a broker, so the wildcard is
        // explicit: only bindings that apply to every broker can match.
        var broker = query.BrokerId ?? Guid.Empty;

        var publishes = (query.Publishes ?? [])
            .Select(target => ResolvePublish(all, broker, vhost, target))
            .ToList();

        var consumes = (query.Consumes ?? [])
            .Select(queue => ResolveConsume(all, broker, vhost, queue))
            .ToList();

        return Result<ResolvedContracts>.Success(new ResolvedContracts(publishes, consumes));
    }

    private static ResolvedBinding ResolvePublish(
        IReadOnlyList<Contract> contracts, Guid broker, string vhost, PublishTarget target)
    {
        var matches = new List<(string Name, EnforcementMode Mode, IReadOnlyList<SubjectRef> Subjects)>();

        foreach (var contract in contracts)
        {
            var matched = contract.ResolvePublish(
                broker, vhost, target.Exchange ?? string.Empty, target.RoutingKey ?? string.Empty);

            if (matched.Count > 0)
            {
                // matched[0] and not a merge across the contract's own bindings: within one
                // contract the overlap invariant already guarantees that intersecting bindings
                // either agree on subjects or declare precedence, so the first is the answer.
                matches.Add((contract.Name, contract.Enforcement, matched[0].Subjects));
            }
        }

        return Combine(matches);
    }

    private static ResolvedBinding ResolveConsume(
        IReadOnlyList<Contract> contracts, Guid broker, string vhost, string queue)
    {
        var matches = new List<(string Name, EnforcementMode Mode, IReadOnlyList<SubjectRef> Subjects)>();

        foreach (var contract in contracts)
        {
            if (contract.ResolveConsume(broker, vhost, queue) is { } binding)
            {
                matches.Add((contract.Name, contract.Enforcement, binding.Subjects));
            }
        }

        return Combine(matches);
    }

    /// <summary>Folds every matching contract into one answer, keeping the ambiguity visible.</summary>
    private static ResolvedBinding Combine(
        List<(string Name, EnforcementMode Mode, IReadOnlyList<SubjectRef> Subjects)> matches)
    {
        if (matches.Count is 0)
        {
            return new ResolvedBinding([], EnforcementMode.Off, []);
        }

        // Ordered by name so two SDKs asking the same question get the same answer in the same
        // order, and a diff of two resolutions is about the topology rather than about row order.
        matches.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));

        var subjects = new List<SubjectRef>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var strictest = EnforcementMode.Off;

        foreach (var match in matches)
        {
            if (match.Mode > strictest)
            {
                strictest = match.Mode;
            }

            foreach (var subject in match.Subjects)
            {
                // Deduplicated on subject AND selector: two contracts naming the same subject at
                // different versions is a real disagreement the SDK should see both halves of.
                if (seen.Add($"{subject.Subject.Value}@{subject.Selector}"))
                {
                    subjects.Add(subject);
                }
            }
        }

        return new ResolvedBinding([.. matches.Select(m => m.Name)], strictest, subjects);
    }
}

/// <summary>Parsing and loading shared by the contract handlers.</summary>
internal static class ContractPolicies
{
    /// <summary>Loads a contract, or fails with a code the API can map.</summary>
    public static async Task<Result<Contract>> RequireAsync(
        IEnvironmentRepository environments,
        IContractRepository contracts,
        string? environmentName,
        string? contractName,
        CancellationToken cancellationToken)
    {
        var environment = await EnvironmentPolicies.RequireAsync(
            environments, environmentName, cancellationToken).ConfigureAwait(false);

        if (environment.IsFailure)
        {
            return Result<Contract>.Failure(environment.Error!);
        }

        var trimmed = contractName?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return Result<Contract>.Failure(
                ConcordatCodes.ContractNameInvalid, "A contract name is required.");
        }

        var contract = await contracts.FindAsync(environment.Value.Id, trimmed, cancellationToken)
            .ConfigureAwait(false);

        return contract is null
            ? Result<Contract>.Failure(
                ConcordatCodes.ContractNotFound,
                $"No contract named '{trimmed}' in environment '{environment.Value.Name.Value}'.")
            : Result<Contract>.Success(contract);
    }

    /// <summary>The virtual host, defaulting to the broker's own default.</summary>
    public static string Vhost(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "/" : value.Trim();

    /// <summary>Parses an enforcement mode.</summary>
    public static Result ParseEnforcement(string? value, out EnforcementMode? mode)
    {
        mode = null;

        if (value is null)
        {
            return Result.Success();
        }

        if (!Enum.TryParse<EnforcementMode>(value.Trim(), true, out var parsed))
        {
            return Result.Failure(
                ConcordatCodes.ContractNameInvalid,
                $"Unknown enforcement mode '{value}'. Expected OFF, MONITOR or ENFORCE.");
        }

        mode = parsed;
        return Result.Success();
    }

    /// <summary>Parses a supplied subject list.</summary>
    public static Result ParseSubjects(
        IReadOnlyList<SubjectRefInput>? supplied, out IReadOnlyList<SubjectRef> refs)
    {
        refs = [];

        if (supplied is null || supplied.Count is 0)
        {
            return Result.Failure(
                ConcordatCodes.ContractNameInvalid,
                "A binding must carry at least one subject; otherwise it says nothing.");
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
