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
    /// <param name="source">The environment to read from.</param>
    /// <param name="target">The environment to write to.</param>
    /// <param name="output">Where results go.</param>
    /// <param name="subject">The subject.</param>
    /// <param name="ordinal">The ordinal to promote, or <c>latest</c>.</param>
    /// <param name="owner">Owner recorded if the subject is new in the target.</param>
    /// <param name="by">Who is promoting.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>An <see cref="ExitCodes"/> value.</returns>
    /// <remarks>
    /// <para>
    /// Promotion is a composition of existing operations rather than a new endpoint, and it
    /// works because schema ids are content-addressed (ADR-015): the id in <c>prod</c> is the
    /// same id as in <c>staging</c>, so a message published before promotion stays valid after
    /// it. The command asserts that rather than assuming it.
    /// </para>
    /// <para>
    /// It deliberately promotes <em>one</em> subject, not a whole environment. Bulk promotion
    /// reads as an atomic operation and is not one; a partial failure halfway through would
    /// leave the target in a state nobody chose.
    /// </para>
    /// </remarks>
    public static async Task<int> PromoteAsync(
        RegistryApi source,
        RegistryApi target,
        Output output,
        string subject,
        string ordinal,
        string owner,
        string by,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(output);

        var version = await source.GetVersionAsync(subject, ordinal, cancellationToken).ConfigureAwait(false);

        if (version is null)
        {
            return output.Fail(
                ExitCodes.ContractViolation,
                "version_not_found",
                $"'{subject}' has no version '{ordinal}' in the source environment.");
        }

        // A version carries its schema id, not its text, so the document is a second call.
        var schema = await source.GetSchemaTextAsync(version.SchemaId, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(schema))
        {
            return output.Fail(
                ExitCodes.RegistryUnavailable,
                "schema_unresolvable",
                $"The source knows v{version.Ordinal} of '{subject}' but not schema {version.SchemaId}.");
        }

        await target.CreateSubjectAsync(subject, WireTokens.FormatJson, owner, cancellationToken)
            .ConfigureAwait(false);

        var result = await target.RegisterAsync(
            subject, schema, version.SemanticVersion, by, cancellationToken).ConfigureAwait(false);

        // Content addressing is what makes promotion safe. If the ids ever differed, a message
        // in flight during a promotion would be pinned to an id the target had never heard of,
        // so this is asserted rather than trusted.
        if (!string.Equals(result.SchemaId, version.SchemaId, StringComparison.Ordinal))
        {
            return output.Fail(
                ExitCodes.InternalError,
                "schema_id_mismatch",
                $"Promotion changed the schema id, from {version.SchemaId} to {result.SchemaId}. " +
                "Identical content must produce an identical id (ADR-015); this indicates the two " +
                "environments are canonicalising differently.");
        }

        output.Document(
            new PromoteReport(
                true, subject, result.SchemaId, version.Ordinal, result.Ordinal,
                result.Status, result.Created),
            CliJson.Default.PromoteReport);

        output.Line(
            $"{subject}  v{version.Ordinal} → v{result.Ordinal}  {result.SchemaId}  {result.Status}");

        return ExitCodes.Success;
    }
}
