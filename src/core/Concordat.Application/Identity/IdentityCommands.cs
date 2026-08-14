using Concordat.Application.Abstractions;
using Concordat.Domain.Governance;
using Concordat.Domain.Identity;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;

namespace Concordat.Application.Identity;

/// <summary>
/// Claims an instance that has no accounts yet (M8.1).
/// </summary>
/// <param name="Email">The owner's login.</param>
/// <param name="DisplayName">What to show.</param>
/// <param name="Password">The plaintext, checked against the length rule and then hashed.</param>
/// <remarks>
/// <b>It works exactly once.</b> ADR-008 promises that <c>docker compose up</c> produces a
/// working authenticated instance with no external dependency, which means the first run has to
/// be able to create an account without one — and every run after that must not.
/// </remarks>
public sealed record BootstrapOwnerCommand(string? Email, string? DisplayName, string? Password)
    : ICommand<User>;

/// <summary>Invites a member, or changes an existing one's role.</summary>
/// <param name="Email">Their login.</param>
/// <param name="DisplayName">What to show.</param>
/// <param name="Password">An initial password.</param>
/// <param name="Role"><c>READER</c>, <c>ADMIN</c> or <c>OWNER</c>.</param>
public sealed record CreateMemberCommand(
    string? Email, string? DisplayName, string? Password, string? Role) : ICommand<User>;

/// <summary>Changes a member's role.</summary>
/// <param name="UserId">Which member.</param>
/// <param name="Role">The new role.</param>
public sealed record ChangeMemberRoleCommand(Guid UserId, string? Role) : ICommand<Membership>;

/// <summary>A member and what they may do.</summary>
/// <param name="User">The account.</param>
/// <param name="Role">Their role in this tenant.</param>
public sealed record Member(User User, Role Role);

/// <summary>Lists the tenant's members.</summary>
public sealed record ListMembersQuery : IQuery<IReadOnlyList<Member>>;

/// <summary>Issues an API key.</summary>
/// <param name="Label">What it is for.</param>
/// <param name="Scopes">What it may do.</param>
/// <param name="ExpiresAt">When it should stop working, or null.</param>
public sealed record IssueApiKeyCommand(
    string? Label, IReadOnlyList<string>? Scopes, DateTimeOffset? ExpiresAt)
    : ICommand<IssuedApiKey>;

/// <summary>Revokes an API key.</summary>
/// <param name="Id">Which key.</param>
public sealed record RevokeApiKeyCommand(Guid Id) : ICommand<ApiKey>;

/// <summary>Lists the tenant's API keys, never their secrets.</summary>
public sealed record ListApiKeysQuery : IQuery<IReadOnlyList<ApiKey>>;

/// <summary>Handles <see cref="BootstrapOwnerCommand"/>.</summary>
public sealed class BootstrapOwnerHandler(
    IUserRepository users,
    IPasswordHasher passwords,
    IAuditLog audit,
    IUnitOfWork unitOfWork,
    ITenantContext tenant,
    TimeProvider clock)
    : ICommandHandler<BootstrapOwnerCommand, User>
{
    /// <inheritdoc />
    public async Task<Result<User>> HandleAsync(
        BootstrapOwnerCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Checked first and re-checked by a unique index. A race between two first-run requests
        // would otherwise create two owners, and the loser would be a full-privilege account
        // nobody meant to make.
        if (await users.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            return Result<User>.Failure(
                ConcordatCodes.UserAlreadyExists,
                "This instance already has accounts. Bootstrap creates the first owner and " +
                "then never runs again; ask an existing owner to invite you.");
        }

        var checkedPassword = User.CheckPassword(command.Password);
        if (checkedPassword.IsFailure)
        {
            return Result<User>.Failure(checkedPassword.Error!);
        }

        var created = User.Create(
            command.Email,
            command.DisplayName,
            passwords.Hash(command.Password!),
            clock.GetUtcNow());

        if (created.IsFailure)
        {
            return created;
        }

        users.Add(created.Value);
        users.Add(Membership.Grant(
            tenant.Current, created.Value.Id, Role.Owner, clock.GetUtcNow()));

        audit.Append(AuditEntry.Record(
            null,
            AuditAction.MemberAdded,
            created.Value.Actor(),
            created.Value.Email.Value,
            clock.GetUtcNow(),
            "OWNER, created by first-run bootstrap"));

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return created;
    }
}

/// <summary>Handles <see cref="CreateMemberCommand"/>.</summary>
public sealed class CreateMemberHandler(
    IUserRepository users,
    IPasswordHasher passwords,
    IAuditLog audit,
    IUnitOfWork unitOfWork,
    ICallerContext caller,
    TimeProvider clock)
    : ICommandHandler<CreateMemberCommand, User>
{
    /// <inheritdoc />
    public async Task<Result<User>> HandleAsync(
        CreateMemberCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var parsedRole = Roles.Parse(command.Role ?? "READER", out var role);
        if (parsedRole.IsFailure)
        {
            return Result<User>.Failure(parsedRole.Error!);
        }

        var checkedPassword = User.CheckPassword(command.Password);
        if (checkedPassword.IsFailure)
        {
            return Result<User>.Failure(checkedPassword.Error!);
        }

        var address = EmailAddress.Create(command.Email);
        if (address.IsFailure)
        {
            return Result<User>.Failure(address.Error!);
        }

        var existing = await users.FindAsync(address.Value, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            return Result<User>.Failure(
                ConcordatCodes.UserAlreadyExists,
                $"An account already exists for '{address.Value}'.");
        }

        var created = User.Create(
            command.Email,
            command.DisplayName,
            passwords.Hash(command.Password!),
            clock.GetUtcNow());

        if (created.IsFailure)
        {
            return created;
        }

        var tenantId = caller.Current.TenantId;

        users.Add(created.Value);
        users.Add(Membership.Grant(tenantId, created.Value.Id, role, clock.GetUtcNow()));

        audit.Append(AuditEntry.Record(
            null,
            AuditAction.MemberAdded,
            caller.Current.Actor,
            created.Value.Email.Value,
            clock.GetUtcNow(),
            Roles.For(role)));

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return created;
    }
}

/// <summary>Handles <see cref="ChangeMemberRoleCommand"/>.</summary>
public sealed class ChangeMemberRoleHandler(
    IUserRepository users,
    IAuditLog audit,
    IUnitOfWork unitOfWork,
    ICallerContext caller,
    TimeProvider clock)
    : ICommandHandler<ChangeMemberRoleCommand, Membership>
{
    /// <inheritdoc />
    public async Task<Result<Membership>> HandleAsync(
        ChangeMemberRoleCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var parsed = Roles.Parse(command.Role, out var role);
        if (parsed.IsFailure)
        {
            return Result<Membership>.Failure(parsed.Error!);
        }

        var tenantId = caller.Current.TenantId;
        var userId = new UserId(command.UserId);

        var membership = await users.FindMembershipAsync(tenantId, userId, cancellationToken)
            .ConfigureAwait(false);

        if (membership is null)
        {
            return Result<Membership>.Failure(
                ConcordatCodes.UserNotFound, "No such member in this tenant.");
        }

        // An owner cannot demote themselves. Not paternalism: an instance whose last owner is a
        // Reader has nobody who can grant the role back, and the only fix is a database edit.
        if (membership.Role is Role.Owner &&
            role is not Role.Owner &&
            caller.Current.UserId == userId &&
            !await AnotherOwnerExistsAsync(tenantId, userId, cancellationToken).ConfigureAwait(false))
        {
            return Result<Membership>.Failure(
                ConcordatCodes.RoleInvalid,
                "You are the only owner. Promote someone else first, or this instance would " +
                "have nobody who can manage it.");
        }

        membership.ChangeRole(role);

        audit.Append(AuditEntry.Record(
            null,
            AuditAction.MemberRoleChanged,
            caller.Current.Actor,
            command.UserId.ToString(),
            clock.GetUtcNow(),
            Roles.For(role)));

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result<Membership>.Success(membership);
    }

    private async Task<bool> AnotherOwnerExistsAsync(
        TenantId tenantId, UserId excluding, CancellationToken cancellationToken)
    {
        var members = await users.ListMembersAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);

        return members.Any(m => m.Membership.Role is Role.Owner && m.User.Id != excluding);
    }
}

/// <summary>Handles <see cref="ListMembersQuery"/>.</summary>
public sealed class ListMembersHandler(IUserRepository users, ICallerContext caller)
    : IQueryHandler<ListMembersQuery, IReadOnlyList<Member>>
{
    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<Member>>> HandleAsync(
        ListMembersQuery query, CancellationToken cancellationToken)
    {
        var members = await users
            .ListMembersAsync(caller.Current.TenantId, cancellationToken).ConfigureAwait(false);

        return Result<IReadOnlyList<Member>>.Success(
            [.. members.Select(m => new Member(m.User, m.Membership.Role))]);
    }
}

/// <summary>Handles <see cref="IssueApiKeyCommand"/>.</summary>
public sealed class IssueApiKeyHandler(
    IApiKeyRepository apiKeys,
    IAuditLog audit,
    IUnitOfWork unitOfWork,
    ICallerContext caller,
    TimeProvider clock)
    : ICommandHandler<IssueApiKeyCommand, IssuedApiKey>
{
    /// <inheritdoc />
    public async Task<Result<IssuedApiKey>> HandleAsync(
        IssueApiKeyCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var current = caller.Current;

        // A key may never grant more than its issuer holds. Without this, a Reader who can
        // reach this endpoint mints themselves subject:admin and ADR-018 is decoration.
        var parsed = Scope.Parse(command.Scopes, out var requested);
        if (parsed.IsFailure)
        {
            return Result<IssuedApiKey>.Failure(parsed.Error!);
        }

        var exceeded = requested.Where(s => !current.Allows(s)).ToList();

        if (exceeded.Count > 0)
        {
            return Result<IssuedApiKey>.Failure(
                ConcordatCodes.InsufficientScope,
                $"You cannot issue a key granting {string.Join(", ", exceeded)}, because you " +
                "do not hold it. A key is a delegation of the issuer's own authority.");
        }

        var issued = ApiKey.Issue(
            current.TenantId,
            current.UserId,
            command.Label,
            command.Scopes,
            clock.GetUtcNow(),
            command.ExpiresAt);

        if (issued.IsFailure)
        {
            return issued;
        }

        apiKeys.Add(issued.Value.Key);

        audit.Append(AuditEntry.Record(
            null,
            AuditAction.ApiKeyIssued,
            current.Actor,
            issued.Value.Key.Label,
            clock.GetUtcNow(),
            $"{issued.Value.Key.KeyId}: {string.Join(", ", issued.Value.Key.Scopes)}"));

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return issued;
    }
}

/// <summary>Handles <see cref="RevokeApiKeyCommand"/>.</summary>
public sealed class RevokeApiKeyHandler(
    IApiKeyRepository apiKeys,
    IAuditLog audit,
    IUnitOfWork unitOfWork,
    ICallerContext caller,
    TimeProvider clock)
    : ICommandHandler<RevokeApiKeyCommand, ApiKey>
{
    /// <inheritdoc />
    public async Task<Result<ApiKey>> HandleAsync(
        RevokeApiKeyCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var key = await apiKeys
            .FindAsync(caller.Current.TenantId, command.Id, cancellationToken)
            .ConfigureAwait(false);

        if (key is null)
        {
            return Result<ApiKey>.Failure(
                ConcordatCodes.ApiKeyNotFound, "No such API key in this tenant.");
        }

        key.Revoke(clock.GetUtcNow());

        audit.Append(AuditEntry.Record(
            null,
            AuditAction.ApiKeyRevoked,
            caller.Current.Actor,
            key.Label,
            clock.GetUtcNow(),
            key.KeyId));

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result<ApiKey>.Success(key);
    }
}

/// <summary>Handles <see cref="ListApiKeysQuery"/>.</summary>
public sealed class ListApiKeysHandler(IApiKeyRepository apiKeys, ICallerContext caller)
    : IQueryHandler<ListApiKeysQuery, IReadOnlyList<ApiKey>>
{
    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<ApiKey>>> HandleAsync(
        ListApiKeysQuery query, CancellationToken cancellationToken)
    {
        var keys = await apiKeys
            .ListAsync(caller.Current.TenantId, cancellationToken).ConfigureAwait(false);

        return Result<IReadOnlyList<ApiKey>>.Success(keys);
    }
}
