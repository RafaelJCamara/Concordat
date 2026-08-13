using Concordat.Cli.Inference;
using Concordat.Domain.Registry;

namespace Concordat.Cli.Commands;

/// <summary>
/// <c>concordat infer</c> — drafts schemas from real messages (ADR-014).
/// </summary>
/// <remarks>
/// <b>Never registers anything.</b> It writes a draft and a report, and a human decides. A
/// schema inferred from samples is a well-informed guess, and auto-registering guesses would
/// fill the registry with wrong contracts that then gate production traffic.
/// </remarks>
public static class InferCommand
{
    /// <summary>Infers from a directory of sample payloads. The default and safe mode.</summary>
    /// <param name="output">Where results go.</param>
    /// <param name="samplesDirectory">A directory of <c>.json</c> payloads.</param>
    /// <param name="subject">The subject the draft is for.</param>
    /// <param name="outputDirectory">Where to write the draft.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>An <see cref="ExitCodes"/> value.</returns>
    public static async Task<int> FromFilesAsync(
        Output output,
        string samplesDirectory,
        string subject,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (!Directory.Exists(samplesDirectory))
        {
            return output.Fail(
                ExitCodes.LocalFileError, "samples_dir_missing", $"No such directory: {samplesDirectory}");
        }

        var files = Directory.EnumerateFiles(samplesDirectory, "*.json").Order(StringComparer.Ordinal).ToList();

        if (files.Count == 0)
        {
            return output.Fail(
                ExitCodes.LocalFileError,
                "no_samples",
                $"{samplesDirectory} contains no .json payloads to infer from.");
        }

        var samples = new List<string>(files.Count);

        foreach (var file in files)
        {
            samples.Add(await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false));
        }

        return await WriteAsync(output, subject, outputDirectory, samples, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Infers from a live queue, requeuing every message it reads.</summary>
    /// <param name="output">Where results go.</param>
    /// <param name="broker">AMQP URI.</param>
    /// <param name="queue">The queue to sample.</param>
    /// <param name="max">How many messages to inspect.</param>
    /// <param name="subject">The subject the draft is for, or null to take it from the messages.</param>
    /// <param name="outputDirectory">Where to write the draft.</param>
    /// <param name="acknowledgeReordering">Whether the caller has accepted the reordering risk.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>An <see cref="ExitCodes"/> value.</returns>
    public static async Task<int> FromQueueAsync(
        Output output,
        string broker,
        string queue,
        int max,
        string? subject,
        string outputDirectory,
        bool acknowledgeReordering,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);

        // An explicit opt-in, not a warning printed after the fact. ADR-014 records reordering
        // as a real side effect on production traffic; a flag makes it a decision somebody took
        // rather than something they discovered afterwards.
        if (!acknowledgeReordering)
        {
            return output.Fail(
                ExitCodes.UsageError,
                "queue_mode_not_acknowledged",
                "Queue mode reads live traffic. Every message is requeued, so nothing is lost, " +
                "but requeued messages return to the head of the queue rather than their " +
                "original position — on an order-sensitive queue that is a real side effect. " +
                "Pass --i-understand-this-reorders-the-queue to proceed, or use file mode.");
        }

        if (!Uri.TryCreate(broker, UriKind.Absolute, out var uri))
        {
            return output.Fail(ExitCodes.UsageError, "broker_uri_invalid", $"'{broker}' is not a URI.");
        }

        QueueSample sample;
        try
        {
            sample = await QueueSampler.DrainAsync(uri, queue, max, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return output.Fail(ExitCodes.RegistryUnavailable, "broker_unreachable", ex.Message);
        }

        output.Diagnostic(
            $"note: inspected {sample.Inspected} message(s) and requeued {sample.Requeued}.");

        if (sample.Subjects.Count > 1 && subject is null)
        {
            // One queue routinely carries several message types (ADR-011), so a single inferred
            // schema across all of them would be a union of unrelated shapes — worse than
            // nothing, because it would look plausible.
            var seen = string.Join(", ", sample.Subjects.OrderByDescending(s => s.Value)
                .Select(s => $"{s.Key} ({s.Value})"));

            return output.Fail(
                ExitCodes.UsageError,
                "mixed_message_types",
                $"'{queue}' carries more than one message type: {seen}. Inferring one schema " +
                "across all of them would produce a union of unrelated shapes. Re-run with " +
                "--subject to draft one of them.");
        }

        var payloads = sample.Payloads;
        var target = subject ?? sample.Subjects.Keys.FirstOrDefault();

        if (target is null)
        {
            return output.Fail(
                ExitCodes.UsageError,
                "subject_unknown",
                $"No message on '{queue}' set properties.type, so there is no subject to draft " +
                "for. Re-run with --subject.");
        }

        if (subject is not null && sample.Subjects.Count > 1)
        {
            // Filtering is best-effort: only messages that declared a type can be attributed.
            output.Diagnostic(
                $"note: the queue carries {sample.Subjects.Count} message types; all sampled " +
                "payloads were used. Review the draft carefully.");
        }

        return payloads.Count == 0
            ? output.Fail(
                ExitCodes.LocalFileError, "no_samples", $"No readable payload was found on '{queue}'.")
            : await WriteAsync(output, target, outputDirectory, payloads, cancellationToken)
                .ConfigureAwait(false);
    }

    private static async Task<int> WriteAsync(
        Output output,
        string subject,
        string outputDirectory,
        IReadOnlyCollection<string> samples,
        CancellationToken cancellationToken)
    {
        var name = SubjectName.Create(subject);

        if (name.IsFailure)
        {
            return output.Fail(ExitCodes.UsageError, name.Error!.Code, name.Error.Message);
        }

        InferenceResult result;
        try
        {
            result = JsonSchemaInferrer.Infer(samples);
        }
        catch (InvalidOperationException ex)
        {
            return output.Fail(ExitCodes.LocalFileError, "no_parsable_samples", ex.Message);
        }

        Directory.CreateDirectory(outputDirectory);
        var path = ContractDirectory.FileFor(outputDirectory, name.Value.Value, SchemaFormat.Json);
        await File.WriteAllTextAsync(path, result.Schema, cancellationToken).ConfigureAwait(false);

        output.Document(
            new InferReport(
                true,
                name.Value.Value,
                path,
                result.SampleCount,
                [.. result.Findings.Select(f => new FindingEntry(
                    f.Path, f.Confidence.ToString().ToUpperInvariant(), f.Kind, f.Message))]),
            CliJson.Default.InferReport);

        output.Line($"Draft written to {path}");
        output.Line(string.Empty);

        foreach (var line in JsonSchemaInferrer.Describe(result))
        {
            output.Line(line);
        }

        // Said on stderr so it survives `concordat infer > draft.txt`, and said last so it is
        // the thing still on screen.
        var low = result.LowConfidence.Count();
        output.Diagnostic(
            low > 0
                ? $"\nnote: this is a DRAFT, not a contract. {low} low-confidence inference(s) " +
                  "need review before you push it."
                : "\nnote: this is a DRAFT, not a contract. Review it before you push it.");

        return ExitCodes.Success;
    }
}
