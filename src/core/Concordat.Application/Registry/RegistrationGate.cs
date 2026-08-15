using Concordat.Application.Abstractions;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Environment = Concordat.Domain.Registry.Environment;

namespace Concordat.Application.Registry;

/// <summary>
/// Decides whether a caller may write schemas into an environment, creating the environment's
/// row if this is the first thing anyone has written to it (decisions 23 and the M7.1 policy).
/// </summary>
/// <remarks>
/// <para>
/// Shared by subject creation and version registration because they are the same question asked
/// twice, and the two answering differently is how a closed environment ends up full of empty
/// subjects.
/// </para>
/// <para>
/// <b>Auto-creating the row is not a migration; it is filling in a record that was always
/// implied.</b> <c>IEnvironmentResolver</c> derives an <c>EnvironmentId</c> by hashing the name,
/// so <c>POST /v1/environments/dev/subjects</c> has always worked whether or not anybody created
/// <c>dev</c> — right up until promotion and impact analysis, which need a real row to read a
/// compatibility policy from and answered <c>environment_not_found</c>. An estate could register
/// happily for months and then discover it could not promote, which reads as a bug rather than a
/// missing setup step.
/// </para>
/// <para>
/// The row is created <b>with the derived id</b>, which is what makes this safe: every subject
/// already points at that id, so the new row adopts them rather than orphaning them.
/// </para>
/// <para>
/// <b>A production-named environment refuses on that first write, and the row is not kept.</b>
/// <c>Environment.Create</c> defaults anything called <c>prod</c>, <c>production</c> or
/// <c>live</c> to <see cref="RegistrationPolicy.CiOnly"/>, so a non-CI caller is refused and the
/// handler returns before committing. That is the intended shape: it closes the gap where a
/// never-created environment had no policy and therefore admitted everyone.
/// </para>
/// </remarks>
internal static class RegistrationGate
{
    /// <summary>What the gate found.</summary>
    /// <param name="Environment">
    /// The environment, or null when the caller built a command by hand and supplied no name.
    /// A carrier record rather than a nullable type argument, because <c>Result&lt;T&gt;</c>
    /// constrains <c>T</c> to <c>notnull</c> — deliberately, so that a successful result never
    /// carries nothing.
    /// </param>
    public sealed record Admission(Environment? Environment);

    /// <summary>Admits or refuses a write, ensuring the environment exists.</summary>
    /// <param name="environments">The environment repository.</param>
    /// <param name="caller">Who is asking.</param>
    /// <param name="environmentId">The id the route resolved to.</param>
    /// <param name="environmentName">The name from the route, or null on a legacy call path.</param>
    /// <param name="clock">Time source.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>
    /// The environment, or the refusal to return to the caller. The environment comes back
    /// because the caller usually needs it for the next question too — the pre-release policy
    /// lives on it, and loading it twice would be two round trips for one row.
    /// </returns>
    public static async Task<Result<Admission>> AdmitAsync(
        IEnvironmentRepository environments,
        ICallerContext caller,
        EnvironmentId environmentId,
        string? environmentName,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var environment = await environments
            .FindAsync(environmentId, cancellationToken).ConfigureAwait(false);

        if (environment is null)
        {
            if (string.IsNullOrWhiteSpace(environmentName))
            {
                // No name to create from. Only a caller that built the command by hand can get
                // here; every route carries the name. Allowing it keeps that path behaving as it
                // did rather than refusing on a technicality the caller cannot act on.
                return Result<Admission>.Success(new Admission(null));
            }

            var created = Environment.Create(
                environmentName, clock.GetUtcNow(), id: environmentId);

            if (created.IsFailure)
            {
                return Result<Admission>.Failure(created.Error!);
            }

            environment = created.Value;

            // Staged, not committed. If the policy below refuses, the handler returns without
            // saving and the row goes with it — so a refused first write leaves nothing behind.
            environments.Add(environment);
        }

        var permitted = environment.MayRegister(caller.Current.Scopes);

        return permitted.IsFailure
            ? Result<Admission>.Failure(permitted.Error!)
            : Result<Admission>.Success(new Admission(environment));
    }
}
