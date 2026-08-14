using System.Globalization;
using Concordat.Domain.Registry;

namespace Concordat.Cli.Commands;

/// <summary>What happened to one subject during a push.</summary>
/// <param name="Subject">The subject.</param>
/// <param name="Status"><c>registered</c>, <c>unchanged</c>, <c>awaiting-approval</c>, or <c>skipped</c>.</param>
/// <param name="Ordinal">The allocated ordinal.</param>
/// <param name="SchemaId">The content-addressed id.</param>
public sealed record PushOutcome(string Subject, string Status, int? Ordinal = null, string? SchemaId = null);

/// <summary>
/// <c>concordat push</c> — registers every contract in a directory.
/// </summary>
/// <remarks>
/// <para>
/// <b>A breaking change is not a push failure.</b> Under ADR-017 it registers as
/// <c>AwaitingApproval</c> and does not move the <c>latest</c> pointer, which is a reviewable
/// artifact rather than an error — so push reports it and exits 0. Use <c>check</c> to gate;
/// <c>push</c> records.
/// </para>
/// <para>
/// That split matters in a pipeline: the merge build gates with <c>check</c>, and the deploy
/// build records with <c>push</c>. Making <c>push</c> also fail on breakage would mean a
/// deliberately-approved breaking change could never be recorded at all.
/// </para>
/// </remarks>
public static class PushCommand
{
    /// <summary>Registers a directory of contracts.</summary>
    /// <param name="api">The registry.</param>
    /// <param name="output">Where results go.</param>
    /// <param name="directory">The contracts directory.</param>
    /// <param name="owner">Owner recorded when a subject has to be created.</param>
    /// <param name="by">Who is registering.</param>
    /// <param name="dryRun">Report what would happen, and change nothing.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>An <see cref="ExitCodes"/> value.</returns>
    public static async Task<int> RunAsync(
        RegistryApi api,
        Output output,
        string directory,
        string owner,
        string by,
        bool dryRun,
        CancellationToken cancellationToken)
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
            foreach (var error in errors)
            {
                output.Diagnostic($"error: {error.Path}: {error.Reason}");
            }

            output.Document(
                new FileErrorReport(
                    false,
                    "contract_file_invalid",
                    [.. errors.Select(e => new FileErrorEntry(e.Path, e.Reason))]),
                CliJson.Default.FileErrorReport);

            return ExitCodes.LocalFileError;
        }

        var outcomes = new List<PushOutcome>();

        foreach (var contract in contracts)
        {
            if (contract.Format is not SchemaFormat.Json)
            {
                outcomes.Add(new PushOutcome(contract.Subject.Value, "skipped"));
                continue;
            }

            if (dryRun)
            {
                var preview = await api.CheckAsync(contract.Subject.Value, contract.Body, cancellationToken)
                    .ConfigureAwait(false);

                outcomes.Add(new PushOutcome(
                    contract.Subject.Value,
                    preview is null ? "would-create" : preview.Compatible ? "would-register" : "would-await-approval",
                    SchemaId: preview?.SchemaId));

                output.Line($"  {contract.Subject.Value}  {outcomes[^1].Status}");
                continue;
            }

            // A subject must exist before a version can be registered — M1.6 refuses implicit
            // creation. Creating it here keeps that server-side rule while sparing every user
            // a separate step.
            await api.CreateSubjectAsync(
                contract.Subject.Value, WireTokens.FormatJson, owner, cancellationToken)
                .ConfigureAwait(false);

            var result = await api.RegisterAsync(
                contract.Subject.Value, contract.Body, semver: null, by, cancellationToken)
                .ConfigureAwait(false);

            var status = !result.Created
                ? "unchanged"
                : result.Status.Equals(VersionStatuses.AwaitingApproval, StringComparison.OrdinalIgnoreCase)
                    ? "awaiting-approval"
                    : "registered";

            outcomes.Add(new PushOutcome(contract.Subject.Value, status, result.Ordinal, result.SchemaId));
            output.Line($"  {contract.Subject.Value}  v{result.Ordinal}  {status}");

            if (status == "awaiting-approval")
            {
                foreach (var change in result.Divergences)
                {
                    output.Line($"      {change.Path}  {change.Kind}  [{change.Direction}/{change.Surface}]");
                }
            }
        }

        var awaiting = outcomes.Count(o => o.Status == "awaiting-approval");

        output.Document(
            new PushReport(true, dryRun, outcomes.Count, awaiting, outcomes), CliJson.Default.PushReport);

        if (awaiting > 0)
        {
            // Not a failure, but it must not be missed either: nothing this run recorded is
            // live until somebody approves it.
            output.Diagnostic(
                $"note: {awaiting} version(s) are awaiting approval and are not yet the latest.");
        }

        return ExitCodes.Success;
    }

    /// <summary>
    /// <c>concordat promote</c> — copies a subject's version from one environment to another.
    /// </summary>
    /// <param name="source">The registry client, bound to the source environment.</param>
    /// <param name="output">Where results go.</param>
    /// <param name="subject">The subject.</param>
    /// <param name="targetEnvironment">The environment to promote into.</param>
    /// <param name="ordinal">The ordinal to promote, or <c>latest</c>.</param>
    /// <param name="by">Who is promoting.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>An <see cref="ExitCodes"/> value.</returns>
    /// <remarks>
    /// <para>
    /// <b>One server-side call as of M7.4</b>, where this used to be a four-call client-side
    /// composition. The composition was not merely chattier: it created the target subject with
    /// a hard-coded JSON format, so promoting an Avro or Protobuf subject into a fresh
    /// environment produced a subject of the wrong language which then rejected its own schema.
    /// It also had no transaction, so a failure between the create and the register left a
    /// subject with no versions.
    /// </para>
    /// <para>
    /// The registry re-checks the schema against the target's history under the target's policy
    /// and asserts that the content-addressed id (ADR-015) did not change — a message published
    /// before the promotion stays valid after it.
    /// </para>
    /// <para>
    /// It deliberately promotes <em>one</em> subject, not a whole environment. Bulk promotion
    /// reads as an atomic operation and is not one; a partial failure halfway through would
    /// leave the target in a state nobody chose.
    /// </para>
    /// </remarks>
    public static async Task<int> PromoteAsync(
        RegistryApi source,
        string targetEnvironment,
        Output output,
        string subject,
        string ordinal,
        string by,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(output);

        int? wanted = null;

        if (!string.Equals(ordinal, "latest", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(ordinal, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                return output.Fail(
                    ExitCodes.UsageError,
                    "version_selector_invalid",
                    $"'{ordinal}' is not a version. Expected an ordinal or 'latest'.");
            }

            wanted = parsed;
        }

        var result = await source.PromoteAsync(subject, targetEnvironment, wanted, by, cancellationToken)
            .ConfigureAwait(false);

        output.Document(
            new PromoteReport(
                true, result.Subject, result.SchemaId, result.SourceOrdinal, result.TargetOrdinal,
                result.Status, result.Created),
            CliJson.Default.PromoteReport);

        var created = result.SubjectCreated ? "  (subject created in target)" : string.Empty;

        output.Line(
            $"{result.Subject}  {result.FromEnvironment} v{result.SourceOrdinal} → " +
            $"{result.ToEnvironment} v{result.TargetOrdinal}  {result.SchemaId}  " +
            $"{result.Status}{created}");

        return ExitCodes.Success;
    }
}
