using System.Globalization;

namespace Concordat.Cli.Commands;

/// <summary>
/// <c>concordat impact</c> — who breaks if this subject changes (M7.4).
/// </summary>
/// <remarks>
/// <para>
/// Deferred out of M3.1 because it needs registered consumers, which did not exist until M7.4.
/// It is the capability DESIGN §4 calls out as the one Confluent most conspicuously lacks, and
/// it is only as good as the service registrations behind it: a service that never declares
/// itself is invisible here, which is why the report says how many consumers it looked at
/// rather than only how many break.
/// </para>
/// <para>
/// <b>It gates by default.</b> A breaking consumer is exit 1, so a pipeline that runs this
/// before <c>push</c> stops rather than warning into a log nobody reads. <c>--warn-only</c> is
/// there for the brownfield case where the answer is known and being worked through.
/// </para>
/// </remarks>
public static class ImpactCommand
{
    /// <summary>Runs the command.</summary>
    /// <param name="api">The registry client.</param>
    /// <param name="output">Where to write.</param>
    /// <param name="subject">The subject to analyse.</param>
    /// <param name="file">A candidate schema file, or null to analyse a stored version.</param>
    /// <param name="ordinal">Which stored version, when no file is given.</param>
    /// <param name="warnOnly">Report breakage without failing.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>An <see cref="ExitCodes"/> value.</returns>
    public static async Task<int> RunAsync(
        RegistryApi api,
        Output output,
        string subject,
        string? file,
        int? ordinal,
        bool warnOnly,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(output);

        string? candidate = null;

        if (!string.IsNullOrWhiteSpace(file))
        {
            if (!File.Exists(file))
            {
                return output.Fail(
                    ExitCodes.LocalFileError, "file_not_found", $"No such file: {file}");
            }

            candidate = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
        }

        var result = await api.ImpactAsync(subject, candidate, ordinal, cancellationToken)
            .ConfigureAwait(false);

        output.Document(
            new ImpactReport(
                result.BreakingCount is 0,
                result.Subject,
                result.CandidateSchemaId,
                result.BreakingCount,
                result.Consumers,
                result.Contracts),
            CliJson.Default.ImpactReport);

        Render(output, result);

        return result.BreakingCount is 0 || warnOnly
            ? ExitCodes.Success
            : ExitCodes.ContractViolation;
    }

    private static void Render(Output output, ImpactResult result)
    {
        output.Line($"{result.Subject}  {result.CandidateSchemaId}");

        if (result.Consumers.Count is 0)
        {
            // Not the same as "nothing breaks", and saying so matters: an estate where nobody
            // has registered looks identical to one where nobody is affected.
            output.Line(
                "  no registered consumers — nothing to analyse. Services declare themselves " +
                "at startup or from CI; until they do, this command cannot answer.");
        }

        foreach (var consumer in result.Consumers)
        {
            var mark = consumer.Breaks ? "BREAKS " : "ok     ";
            var stale = consumer.Stale ? "  (stale)" : string.Empty;

            output.Line($"  {mark} {consumer.Service} @{consumer.Selector}{stale}");

            if (consumer.Certainty is "FOLLOWS_LATEST")
            {
                output.Line(
                    "           tracks latest, so this cannot be answered — it follows the " +
                    "pointer wherever it goes.");
            }

            foreach (var reason in consumer.Reasons)
            {
                output.Line($"           {reason}");
            }
        }

        foreach (var contract in result.Contracts)
        {
            output.Line($"  contract {contract} carries this subject");
        }

        output.Line(
            result.BreakingCount is 0
                ? $"{result.Consumers.Count.ToString(CultureInfo.InvariantCulture)} consumer(s), none broken."
                : $"{result.BreakingCount.ToString(CultureInfo.InvariantCulture)} of " +
                  $"{result.Consumers.Count.ToString(CultureInfo.InvariantCulture)} consumer(s) break.");
    }
}
