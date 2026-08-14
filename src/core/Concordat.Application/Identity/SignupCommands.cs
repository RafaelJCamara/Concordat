using Concordat.Application.Abstractions;
using Concordat.Domain.Billing;
using Concordat.Domain.Governance;
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
    IDeploymentLog deployments,
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

        // Recorded on the DEPLOYMENT trail, not the tenant's own (decision 29). Audit rows are
        // stamped with the tenant in scope, and at signup nobody has authenticated — the scope
        // is whatever an anonymous caller resolves to, which is not the organisation being
        // created, so a row there would land in someone else's trail.
        //
        // The fix is not a cross-tenant audit write. Creating an organisation is something the
        // operator's deployment did, not something this organisation did to itself, and it wants
        // a different retention and a different reader. Staged here so it commits in the same
        // transaction: an organisation that exists with nothing recording its creation is the
        // hole this closes, and a second transaction could reintroduce it.
        deployments.Append(DeploymentEvent.Record(
            DeploymentAction.OrganisationCreated,
            address.Value.Value,
            tenant.Value.Id,
            $"Organisation '{tenant.Value.Name}' created with handle '{tenant.Value.Slug}'.",
            clock.GetUtcNow()));

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result<SignedUp>.Success(new SignedUp(tenant.Value, owner.Value));
    }
}
