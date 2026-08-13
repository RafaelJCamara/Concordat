using Concordat.Formats.Abstractions;
using Concordat.Formats.Json;

namespace Concordat.Cli.Commands;

/// <summary>One subject's verdict, in <c>--json</c> form.</summary>
/// <param name="Subject">The subject.</param>
/// <param name="Status"><c>ok</c>, <c>new</c>, <c>broken</c>, or <c>skipped</c>.</param>
/// <param name="SchemaId">The id the proposal would receive.</param>
/// <param name="SuggestedSemver">The label this change warrants.</param>
/// <param name="BreakingChanges">Divergences that violate the policy.</param>
public sealed record SubjectVerdict(
    string Subject,
    string Status,
    string? SchemaId = null,
    string? SuggestedSemver = null,
    IReadOnlyList<Divergence>? BreakingChanges = null);

/// <summary>
/// <c>concordat check</c> — the CI gate, and the reason this CLI exists.
/// </summary>
/// <remarks>
/// Reads every contract in a directory, asks the registry what would happen if each were
/// registered, and exits 1 if any of them would break its policy. Nothing is written.
/// </remarks>
public static class CheckCommand
{
    /// <summary>Runs the check.</summary>
    /// <param name="api">The registry.</param>
    /// <param name="output">Where results go.</param>
    /// <param name="directory">The contracts directory.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>An <see cref="ExitCodes"/> value.</returns>
    public static async Task<int> RunAsync(
        RegistryApi api, Output output, string directory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(output);

        if (!ContractDirectory.TryRead(directory, out var contracts, out var errors))
        {
            return output.Fail(
                ExitCodes.LocalFileError, "contracts_dir_missing", $"No such directory: {directory}");
        }

        if (errors.Count > 0)
        {
            return ReportFileErrors(output, errors);
        }

        if (contracts.Count == 0)
        {
            // Loudly, and as a failure. A gate that silently passes because it found nothing to
            // check is worse than no gate: the pipeline is green and nothing was verified.
            return output.Fail(
                ExitCodes.LocalFileError,
                "contracts_dir_empty",
                $"{directory} contains no contract files. Nothing was checked.");
        }

        var verdicts = new List<SubjectVerdict>();

        foreach (var contract in contracts)
        {
            if (contract.Format is not Concordat.Domain.Registry.SchemaFormat.Json)
            {
                verdicts.Add(new SubjectVerdict(contract.Subject.Value, "skipped"));
                output.Line($"- {contract.Subject.Value}  (skipped: {contract.Format} lands in M5)");
                continue;
            }

            var result = await api.CheckAsync(contract.Subject.Value, contract.Body, cancellationToken)
                .ConfigureAwait(false);

            if (result is null)
            {
                // A subject that does not exist yet cannot break anything. Reported as new
                // rather than as an error, so adding a contract does not fail the build that
                // introduces it.
                verdicts.Add(new SubjectVerdict(contract.Subject.Value, "new"));
                output.Line($"+ {contract.Subject.Value}  (new subject)");
                continue;
            }

            verdicts.Add(new SubjectVerdict(
                contract.Subject.Value,
                result.Compatible ? "ok" : "broken",
                result.SchemaId,
                result.SuggestedSemver,
                result.BreakingChanges));

            Report(output, contract.Subject.Value, result);
        }

        var broken = verdicts.Count(v => v.Status == "broken");

        output.Document(
            new CheckReport(broken == 0, verdicts.Count, broken, verdicts), CliJson.Default.CheckReport);

        if (broken == 0)
        {
            output.Line($"\n{verdicts.Count} contract(s) checked, all compatible.");
            return ExitCodes.Success;
        }

        output.Line($"\n{broken} of {verdicts.Count} contract(s) would break their policy.");
        return ExitCodes.ContractViolation;
    }

    /// <summary>
    /// <c>concordat lint</c> — checks the contracts directory itself, with no registry.
    /// </summary>
    /// <param name="output">Where results go.</param>
    /// <param name="directory">The contracts directory.</param>
    /// <returns>An <see cref="ExitCodes"/> value.</returns>
    /// <remarks>
    /// Separate from <see cref="RunAsync"/> because it needs nothing but the filesystem. That
    /// makes it the check a pre-commit hook can afford to run, and the one that still works
    /// when the registry is down.
    /// </remarks>
    public static int Lint(Output output, string directory)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (!ContractDirectory.TryRead(directory, out var contracts, out var errors))
        {
            return output.Fail(
                ExitCodes.LocalFileError, "contracts_dir_missing", $"No such directory: {directory}");
        }

        var canonicalizer = new JsonSchemaCanonicalizer();
        var problems = errors.ToList();
        var linted = new List<LintedSubject>();

        foreach (var contract in contracts)
        {
            if (contract.Format is not Concordat.Domain.Registry.SchemaFormat.Json)
            {
                continue;
            }

            var canonical = canonicalizer.Canonicalize(contract.Body);

            if (canonical.IsFailure)
            {
                problems.Add(new ContractFileError(contract.Path, canonical.Error!.Message));
                continue;
            }

            // The id is reported because it is reproducible offline in every SDK (ADR-015).
            // Seeing it in a diff is how you tell a reformat from a real change — and computing
            // it locally, with no registry, is the proof that content addressing works offline.
            var schemaId = SchemaIdComputer.Compute(contract.Format, canonical.Value).Value;

            linted.Add(new LintedSubject(contract.Subject.Value, schemaId));
            output.Line($"  {contract.Subject.Value}  {schemaId}");
        }

        if (problems.Count > 0)
        {
            return ReportFileErrors(output, problems);
        }

        output.Document(new LintReport(true, linted.Count, linted), CliJson.Default.LintReport);
        output.Line($"\n{linted.Count} contract(s) are well-formed.");
        return ExitCodes.Success;
    }

    private static void Report(Output output, string subject, CompatibilityResult result)
    {
        if (result.Compatible)
        {
            var suffix = result.SuggestedSemver is null ? string.Empty : $"  → {result.SuggestedSemver}";
            output.Line($"✓ {subject}{suffix}");
            return;
        }

        output.Line($"✗ {subject}");

        foreach (var change in result.BreakingChanges)
        {
            // The JSON Pointer leads. DESIGN §7 singles this out as where Confluent is weakest:
            // being told a change is incompatible without being told where is the difference
            // between a fixable build failure and a guessing game.
            output.Line($"    {change.Path}");
            output.Line(
                $"      {change.Kind}  [{change.Direction}/{change.Surface}]  vs v{change.ConflictsWithVersion}");
            output.Line($"      {change.Message}");
        }
    }

    private static int ReportFileErrors(Output output, IReadOnlyList<ContractFileError> errors)
    {
        output.Document(
            new FileErrorReport(
                false,
                "contract_file_invalid",
                [.. errors.Select(e => new FileErrorEntry(e.Path, e.Reason))]),
            CliJson.Default.FileErrorReport);

        foreach (var error in errors)
        {
            output.Diagnostic($"error: {error.Path}: {error.Reason}");
        }

        return ExitCodes.LocalFileError;
    }
}
