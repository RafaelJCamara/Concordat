using System.Text.Json;
using System.Text.Json.Serialization;

namespace Concordat.Cli;

/// <summary>
/// Everything the CLI prints, in one of two shapes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Human output goes to stdout; diagnostics go to stderr.</b> Under <c>--json</c> stdout
/// carries exactly one JSON document and nothing else, so <c>concordat check --json | jq</c>
/// works without the caller having to strip a progress line off the front. A tool that mixes
/// the two is a tool nobody can script.
/// </para>
/// <para>
/// The JSON shape is a published interface under ADR-019, on the same footing as the REST
/// surface: a Go shop's pipeline parses it, and renaming a field breaks their build.
/// </para>
/// </remarks>
public sealed class Output(bool json, TextWriter stdout, TextWriter stderr)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Whether machine-readable output was asked for.</summary>
    public bool IsJson { get; } = json;

    /// <summary>A line of prose. Suppressed entirely under <c>--json</c>.</summary>
    /// <param name="line">The text.</param>
    public void Line(string line)
    {
        if (!IsJson)
        {
            stdout.WriteLine(line);
        }
    }

    /// <summary>
    /// A diagnostic. Goes to stderr in both modes, so it never contaminates a JSON document
    /// but is still visible when something goes wrong in a pipeline.
    /// </summary>
    /// <param name="line">The text.</param>
    public void Diagnostic(string line) => stderr.WriteLine(line);

    /// <summary>The single JSON document, written only under <c>--json</c>.</summary>
    /// <typeparam name="T">The payload type.</typeparam>
    /// <param name="payload">What to write.</param>
    public void Document<T>(T payload)
    {
        if (IsJson)
        {
            stdout.WriteLine(JsonSerializer.Serialize(payload, Options));
        }
    }

    /// <summary>Reports a failure in whichever shape the caller asked for, and returns its code.</summary>
    /// <param name="code">The exit code to return.</param>
    /// <param name="concordatCode">A stable token, so a script can branch on more than the exit code.</param>
    /// <param name="message">What went wrong.</param>
    /// <returns><paramref name="code"/>, so callers can <c>return output.Fail(...)</c>.</returns>
    public int Fail(int code, string concordatCode, string message)
    {
        if (IsJson)
        {
            Document(new { ok = false, concordatCode, message });
        }
        else
        {
            Diagnostic($"error: {message}");
        }

        return code;
    }
}
