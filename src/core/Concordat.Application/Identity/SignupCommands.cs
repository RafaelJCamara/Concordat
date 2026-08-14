using Concordat.Application.Abstractions;
using Concordat.Domain.Billing;
using Concordat.Domain.Identity;
using Concordat.Domain.Results;

namespace Concordat.Application.Identity;

/// <summary>
/// Creates an organisation and its first owner (M9.2).
/// </summary>
/// <param name="OrganisationName">What the organisation calls itself.</param>
/// <param name="Slug">The URL-safe handle, or null to derive one from the name.</param>
/// <param name="Email">The owner's login.</param>
/// <param name="DisplayName">What to show.</param>
/// <param name="Password">The plaintext, checked against the length rule and then hashed.</param>
/// <remarks>
/// <b>Cloud's equivalent of first-run bootstrap, and deliberately a different command.</b>
/// Bootstrap claims a deployment and can only ever run once; signup creates one organisation
/// among many and runs forever. Sharing a handler would mean one code path whose safety
/// depends on a profile flag, which is exactly the shape DESIGN §10 rejects.
/// </remarks>
public sealed record SignUpCommand(
    string? OrganisationName,
    string? Slug,
    string? Email,
    string? DisplayName,
    string? Password) : ICommand<SignedUp>;

/// <summary>What signup produced.</summary>
/// <param name="Tenant">The new organisation.</param>
/// <param name="Owner">Its first owner.</param>
public sealed record SignedUp(Tenant Tenant, User Owner);

/// <summary>Handles <see cref="SignUpCommand"/>.</summary>
public sealed class SignUpHandler(
    ITenantRepository tenants,
    IUserRepository users,
    IBillingSubscriptionRepository subscriptions,
    IPasswordHasher passwords,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
    : ICommandHandler<SignUpCommand, SignedUp>
{
    /// <inheritdoc />
    public async Task<Result<SignedUp>> HandleAsync(
        SignUpCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var checkedPassword = User.CheckPassword(command.Password);
        if (checkedPassword.IsFailure)
        {
            return Result<SignedUp>.Failure(checkedPassword.Error!);
        }

        var address = EmailAddress.Create(command.Email);
        if (address.IsFailure)
        {
            return Result<SignedUp>.Failure(address.Error!);
        }

        var tenant = Tenant.Create(
            command.OrganisationName, command.Slug, clock.GetUtcNow());

        if (tenant.IsFailure)
        {
            return Result<SignedUp>.Failure(tenant.Error!);
        }

        // Both uniqueness checks before either insert. Creating the organisation and then
        // failing on the email would leave an empty organisation nobody can sign in to and
        // nobody can name, because the slug is now taken.
        var slugTaken = await tenants
            .FindBySlugAsync(tenant.Value.Slug, cancellationToken).ConfigureAwait(false);

        if (slugTaken is not null)
        {
            return Result<SignedUp>.Failure(
                ConcordatCodes.TenantAlreadyExists,
                $"The handle '{tenant.Value.Slug}' is taken.");
        }

        var emailTaken = await users.FindAsync(address.Value, cancellationToken)
            .ConfigureAwait(false);

        if (emailTaken is not null)
        {
            // Deliberately the same message an existing account would produce anywhere else.
            // A signup form that says "that address already has an account" is an account
            // enumeration oracle open to the entire internet.
            return Result<SignedUp>.Failure(
                ConcordatCodes.UserAlreadyExists,
                "That email address cannot be used to create an organisation.");
        }

        var owner = User.Create(
            command.Email,
            command.DisplayName,
            passwords.Hash(command.Password!),
            clock.GetUtcNow());

        if (owner.IsFailure)
        {
            return Result<SignedUp>.Failure(owner.Error!);
        }

        tenants.Add(tenant.Value);

        // In the same transaction as the organisation, deliberately. The billing gate treats a
        // missing subscription as unlimited — failing open rather than silently downgrading a
        // paying customer over an internal inconsistency — and that opening stays narrow only
        // because provisioning cannot half-succeed.
        subscriptions.Add(Subscription.Start(tenant.Value.Id, Tier.Free, clock.GetUtcNow()));

        users.Add(owner.Value);
        users.Add(Membership.Grant(
            tenant.Value.Id, owner.Value.Id, Role.Owner, clock.GetUtcNow()));

        // No audit entry, and that is a limitation rather than an omission. Audit rows are
        // stamped with the tenant in scope, and at signup nobody has authenticated yet — the
        // scope is whatever an anonymous caller resolves to, which is not the organisation
        // being created. A row in the wrong organisation's trail is worse than no row, so the
        // record of a signup is the tenant's own CreatedAt and its owner's membership, both
        // queryable. Recorded in decisions-pending: Cloud eventually wants this audited, and
        // that needs a way to write an entry for a tenant that is not the current one.
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result<SignedUp>.Success(new SignedUp(tenant.Value, owner.Value));
    }
}
