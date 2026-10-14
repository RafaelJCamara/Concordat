using System.Reflection;

namespace Concordat.Formats.Json;

/// <summary>
/// The JSON Schema draft 2020-12 meta-schema and its seven vocabularies, as embedded files
/// (ADR-039).
/// </summary>
/// <remarks>
/// <b>Built once, on first use, and read-only after.</b> Parsing eight small documents and
/// compiling two regular expressions is microseconds of work, but it is work every registration
/// would otherwise repeat; the validator holds no state a call can change, so one instance is
/// shared by every request.
/// </remarks>
internal static class Draft202012Metaschema
{
    /// <summary>The <c>$id</c> of the meta-schema validation starts from.</summary>
    public const string Uri = "https://json-schema.org/draft/2020-12/schema";

    private const string ResourcePrefix = "metaschema/draft-2020-12/";

    private static readonly Lazy<MetaschemaValidator> Instance = new(Load);

    /// <summary>The validator over the embedded documents.</summary>
    public static MetaschemaValidator Validator => Instance.Value;

    /// <summary>The embedded documents, by file name, for a test that pins their content.</summary>
    public static IReadOnlyDictionary<string, string> Documents() =>
        Names().ToDictionary(n => n[ResourcePrefix.Length..], ReadText, StringComparer.Ordinal);

    private static MetaschemaValidator Load() =>
        new(Names().Select(ReadText), Uri);

    private static IEnumerable<string> Names() =>
        typeof(Draft202012Metaschema).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);

    private static string ReadText(string name)
    {
        using var stream = typeof(Draft202012Metaschema).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The embedded resource '{name}' is missing.");
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }
}
