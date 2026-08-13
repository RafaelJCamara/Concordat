using Concordat.Domain.Registry;

namespace Concordat.Cli.Commands;

/// <summary><c>concordat diff</c> and <c>concordat export</c>.</summary>
public static class InspectCommands
{
    /// <summary>Shows what changed between two versions of a subject.</summary>
    /// <param name="api">The registry.</param>
    /// <param name="output">Where results go.</param>
    /// <param name="subject">The subject.</param>
    /// <param name="from">The earlier ordinal.</param>
    /// <param name="to">The later ordinal.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>An <see cref="ExitCodes"/> value.</returns>
    /// <remarks>
    /// Exits 0 whatever it finds. <c>diff</c> answers a question; it does not pass judgement,
    /// and a pipeline that wants a verdict wants <c>check</c>.
    /// </remarks>
    public static async Task<int> DiffAsync(
        RegistryApi api, Output output, string subject, int from, int to, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(output);

        var diff = await api.DiffAsync(subject, from, to, cancellationToken).ConfigureAwait(false);

        output.Document(diff);

        if (diff.Identical)
        {
            // Possible even across different ordinals: two registrations of identical content
            // share one content-addressed id.
            output.Line($"{subject}  v{from} and v{to} are identical ({diff.FromSchemaId}).");
            return ExitCodes.Success;
        }

        output.Line($"{subject}  v{from} → v{to}");
        output.Line($"  {diff.FromSchemaId} → {diff.ToSchemaId}");
        output.Line(string.Empty);

        foreach (var change in diff.Divergences)
        {
            output.Line($"  {change.Path}");
            output.Line($"    {change.Kind}  [{change.Direction}/{change.Surface}]");
            output.Line($"    {change.Message}");
        }

        if (diff.Divergences.Count == 0)
        {
            // Do NOT read this as "only formatting changed". The compatibility engine records a
            // divergence only where one could affect compatibility, so under an open content
            // model — the default — adding or removing a property produces no finding at all.
            // Saying "a formatting change" here would be actively wrong for the single most
            // common schema change there is.
            output.Line(
                "  The content differs, but nothing here can affect compatibility under this " +
                "subject's policy.");
            output.Line(
                "  Note: under an open content model, added and removed properties are not " +
                "reported. Compare the schema text to see them.");
        }

        return ExitCodes.Success;
    }

    /// <summary>Writes an environment's contracts to disk.</summary>
    /// <param name="api">The registry.</param>
    /// <param name="output">Where results go.</param>
    /// <param name="directory">Where to write.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>An <see cref="ExitCodes"/> value.</returns>
    /// <remarks>
    /// <para>
    /// Writes the same <c>&lt;subject&gt;.&lt;ext&gt;</c> layout <c>check</c> and <c>push</c>
    /// read, so <c>export</c> then <c>check</c> is a round trip and reports no changes. That
    /// is what makes it usable for adopting an existing registry into source control.
    /// </para>
    /// <para>
    /// It exports the <b>canonical</b> text, not what was originally authored — the registry
    /// does not keep the original. So the first export of a hand-written schema will show a
    /// formatting diff, and that is expected rather than a fault.
    /// </para>
    /// </remarks>
    public static async Task<int> ExportAsync(
        RegistryApi api, Output output, string directory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(output);

        var subjects = await api.ListSubjectsAsync(cancellationToken).ConfigureAwait(false);

        Directory.CreateDirectory(directory);

        var written = new List<object>();
        var skipped = new List<object>();

        foreach (var subject in subjects)
        {
            if (subject.Latest is null)
            {
                // A subject whose only versions are awaiting approval has no latest pointer
                // (ADR-017). Exporting a pending version would put an unapproved contract into
                // source control as though it were live.
                skipped.Add(new { subject = subject.Name, reason = "no approved version" });
                output.Line($"- {subject.Name}  (no approved version)");
                continue;
            }

            var version = await api.GetVersionAsync(subject.Name, "latest", cancellationToken)
                .ConfigureAwait(false);

            if (version is null)
            {
                skipped.Add(new { subject = subject.Name, reason = "latest version unavailable" });
                continue;
            }

            // A version carries its schema id, not its text.
            var schema = await api.GetSchemaTextAsync(version.SchemaId, cancellationToken)
                .ConfigureAwait(false);

            if (schema is null)
            {
                skipped.Add(new { subject = subject.Name, reason = $"schema {version.SchemaId} unavailable" });
                continue;
            }

            var format = subject.Format switch
            {
                WireTokens.FormatAvro => SchemaFormat.Avro,
                WireTokens.FormatProtobuf => SchemaFormat.Protobuf,
                _ => SchemaFormat.Json,
            };

            var path = ContractDirectory.FileFor(directory, subject.Name, format);
            await File.WriteAllTextAsync(path, schema, cancellationToken).ConfigureAwait(false);

            written.Add(new
            {
                subject = subject.Name,
                path,
                ordinal = version.Ordinal,
                schemaId = version.SchemaId,
            });

            output.Line($"  {path}  v{version.Ordinal}  {version.SchemaId}");
        }

        output.Document(new { ok = true, writtenCount = written.Count, written, skipped });
        output.Line($"\n{written.Count} contract(s) written to {directory}.");

        return ExitCodes.Success;
    }
}
