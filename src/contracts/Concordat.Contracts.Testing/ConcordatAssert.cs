using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Concordat.Contracts.Testing;

/// <summary>
/// Asserts that a contract type still fits what a registry is actually serving (M3.4).
/// </summary>
/// <remarks>
/// <para>
/// <b>This answers a different question from the build-time check, and a team needs both.</b>
/// <c>concordat check</c> and the M3.4 analyser compare a type against the schema file beside
/// it — they catch drift from the <em>file</em>, in the pull request that caused it, and neither
/// knows what is deployed. This calls a live registry and catches drift from what is running in
/// <c>prod</c>, which is the failure that actually pages somebody.
/// </para>
/// <para>
/// <b>It reads the schema the generator emitted; it never produces one.</b> The obvious
/// implementation reflects over the runtime type to build a schema, and that would be a second
/// implementation of the C#-to-JSON-Schema mapping living beside the compile-time one. The two
/// would drift — and a drift detector that drifts is worse than none, because it reports
/// failures nobody can reproduce and, far worse, passes while the real mapping has changed. The
/// only source here is <c>ConcordatGeneratedSchemaAttribute</c>, attached to the assembly at
/// build time.
/// </para>
/// <para>
/// <b>Nothing here is bound to a test framework.</b> It throws
/// <see cref="ConcordatContractException"/>, which xunit, NUnit and MSTest all report as a
/// failure. Depending on one of them would make the package unusable to most of its audience.
/// </para>
/// </remarks>
public static class ConcordatAssert
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Fails unless the registry would accept this type's schema as a new version of its subject.
    /// </summary>
    /// <typeparam name="TContract">A type carrying <c>[ConcordatContract]</c>.</typeparam>
    /// <param name="options">Where the registry is, and which environment to ask.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What the registry said, for a test that wants to assert on the detail.</returns>
    /// <exception cref="ConcordatContractException">
    /// The type carries no generated schema, the registry refused the question, or the schema is
    /// not compatible.
    /// </exception>
    public static Task<CompatibilityVerdict> CompatibleAsync<TContract>(
        ConcordatTestOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        return CheckAsync(FindGenerated(typeof(TContract)), options, cancellationToken);
    }

    /// <summary>
    /// Fails unless every contract type in an assembly is still compatible.
    /// </summary>
    /// <param name="assembly">The assembly to sweep, usually the one under test.</param>
    /// <param name="options">Where the registry is.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>One verdict per contract, in subject order.</returns>
    /// <exception cref="ConcordatContractException">Any of them is incompatible.</exception>
    /// <remarks>
    /// <b>Every contract is checked before anything is thrown.</b> Failing on the first would
    /// tell a team about one broken subject per test run, turning a single afternoon's migration
    /// into a week of rediscovery.
    /// </remarks>
    public static async Task<IReadOnlyList<CompatibilityVerdict>> AllCompatibleAsync(
        Assembly assembly,
        ConcordatTestOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(options);

        var generated = assembly
            .GetCustomAttributes<ConcordatGeneratedSchemaAttribute>()
            .OrderBy(a => a.Subject, StringComparer.Ordinal)
            .ToList();

        if (generated.Count is 0)
        {
            throw new ConcordatContractException(
                $"'{assembly.GetName().Name}' carries no generated schemas. Reference " +
                "Concordat.Contracts and mark a type with [ConcordatContract], or point this at " +
                "the assembly that does.");
        }

        var verdicts = new List<CompatibilityVerdict>(generated.Count);
        var failures = new List<string>();

        foreach (var contract in generated)
        {
            try
            {
                verdicts.Add(await CheckAsync(contract, options, cancellationToken)
                    .ConfigureAwait(false));
            }
            catch (ConcordatContractException ex)
            {
                failures.Add(ex.Message);
            }
        }

        return failures.Count is 0
            ? verdicts
            : throw new ConcordatContractException(
                $"{failures.Count} of {generated.Count} contracts are incompatible with " +
                $"'{options.Environment}':" + Environment.NewLine +
                string.Join(Environment.NewLine, failures));
    }

    private static async Task<CompatibilityVerdict> CheckAsync(
        ConcordatGeneratedSchemaAttribute contract,
        ConcordatTestOptions options,
        CancellationToken cancellationToken)
    {
        options.Validate();

        using var http = options.CreateClient();

        var subject = Uri.EscapeDataString(contract.Subject);
        var environment = Uri.EscapeDataString(options.Environment);

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync(
                $"/v1/environments/{environment}/subjects/{subject}/compatibility",
                new CheckRequest(contract.Schema),
                Json,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Distinguished from an incompatibility on purpose. A test that fails identically
            // whether the schema broke or the registry was unreachable trains a team to rerun it
            // rather than read it.
            throw new ConcordatContractException(
                $"The registry at {options.BaseAddress} could not be reached, so " +
                $"'{contract.Subject}' was not checked. This is not a compatibility failure.",
                ex);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.NotFound)
            {
                // A subject that does not exist yet is not a broken contract. A team adding a
                // new type should not have a red test until CI has pushed it for the first time.
                if (options.TreatUnknownSubjectAsCompatible)
                {
                    return new CompatibilityVerdict(true, contract.Subject, null, [], null);
                }

                throw new ConcordatContractException(
                    $"The registry has no subject '{contract.Subject}' in " +
                    $"'{options.Environment}'. Register it, or set " +
                    $"{nameof(ConcordatTestOptions.TreatUnknownSubjectAsCompatible)}.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new ConcordatContractException(
                    $"The registry refused to check '{contract.Subject}' with " +
                    $"{(int)response.StatusCode}: " +
                    await Describe(response, cancellationToken).ConfigureAwait(false));
            }

            var body = await response.Content
                .ReadFromJsonAsync<CheckResponse>(Json, cancellationToken).ConfigureAwait(false)
                ?? throw new ConcordatContractException(
                    $"The registry returned no answer for '{contract.Subject}'.");

            var verdict = new CompatibilityVerdict(
                body.Compatible,
                contract.Subject,
                body.SchemaId,
                [.. (body.BreakingChanges ?? []).Select(b => $"{b.Path}: {b.Message}")],
                body.SuggestedSemver);

            if (verdict.Compatible)
            {
                return verdict;
            }

            throw new ConcordatContractException(
                $"'{contract.ClrType}' is no longer compatible with what " +
                $"'{options.Environment}' holds for subject '{contract.Subject}':" +
                Environment.NewLine + "  " +
                string.Join(Environment.NewLine + "  ", verdict.BreakingChanges));
        }
    }

    /// <summary>The generated schema for a type, or an explanation of why there is none.</summary>
    private static ConcordatGeneratedSchemaAttribute FindGenerated(Type contract)
    {
        var generated = contract.Assembly
            .GetCustomAttributes<ConcordatGeneratedSchemaAttribute>()
            .FirstOrDefault(a => string.Equals(
                a.ClrType, contract.FullName, StringComparison.Ordinal));

        return generated ?? throw new ConcordatContractException(
            $"No generated schema for '{contract.FullName}'. Mark it with [ConcordatContract] " +
            "and make sure its project references Concordat.Contracts, which carries the " +
            "generator. This package never derives a schema from a runtime type: that would be " +
            "a second implementation of the mapping, and the two would drift.");
    }

    private static async Task<string> Describe(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var problem = await response.Content
                .ReadFromJsonAsync<Problem>(Json, cancellationToken).ConfigureAwait(false);

            return problem?.Detail ?? problem?.ConcordatCode ?? "no problem details";
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException)
        {
            // Already handling a failure; replacing a legible registry problem with an illegible
            // client one helps nobody.
            return "an unreadable error body";
        }
    }

    private sealed record CheckRequest(string Schema);

    private sealed record CheckResponse(
        bool Compatible,
        string? SchemaId,
        IReadOnlyList<Divergence>? BreakingChanges,
        string? SuggestedSemver);

    private sealed record Divergence(string? Path, string? Message);

    private sealed record Problem(
        string? Detail,
        [property: JsonPropertyName("concordatCode")] string? ConcordatCode);
}
