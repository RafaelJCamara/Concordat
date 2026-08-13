using Concordat.Domain.Registry;

namespace Concordat.Cli;

/// <summary>One schema file on disk, and the subject it belongs to.</summary>
/// <param name="Subject">The subject, taken from the file name.</param>
/// <param name="Format">The schema language, taken from the extension.</param>
/// <param name="Path">The file.</param>
/// <param name="Body">Its contents.</param>
public sealed record ContractFile(SubjectName Subject, SchemaFormat Format, string Path, string Body);

/// <summary>A file that could not be read as a contract, and why.</summary>
/// <param name="Path">The file.</param>
/// <param name="Reason">What is wrong with it.</param>
public sealed record ContractFileError(string Path, string Reason);

/// <summary>
/// The <c>./contracts</c> layout: <c>&lt;subject&gt;.&lt;ext&gt;</c>, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>The file name is the subject and the extension is the format.</b> No manifest, no
/// front-matter, no side-car metadata. A manifest would be a second source of truth that can
/// disagree with the directory, and front-matter is impossible in JSON anyway.
/// </para>
/// <para>
/// It also has to work for people who will never run .NET (ADR-019). A Go or Python shop
/// producing <c>contracts/acme.orders.OrderCreated.json</c> from their own tooling needs no
/// Concordat-specific file format to do it, and <c>ls</c> tells them what is registered.
/// </para>
/// <para>
/// The cost is that a subject name must be a legal file name. The subject grammar — letters,
/// digits, underscores and dots — is a subset of what every filesystem allows, so this holds
/// by construction rather than by luck.
/// </para>
/// </remarks>
public static class ContractDirectory
{
    /// <summary>The conventional directory name.</summary>
    public const string DefaultPath = "contracts";

    /// <summary>Maps an extension to the format it declares.</summary>
    /// <remarks>
    /// <c>.avsc</c> and <c>.proto</c> are the conventional extensions in their own ecosystems,
    /// not names Concordat invented. Avro and Protobuf are not registrable until M5; the
    /// extensions are recognised now so that a file for one of them is reported as
    /// "not supported yet" rather than "not a contract".
    /// </remarks>
    public static IReadOnlyDictionary<string, SchemaFormat> Extensions { get; } =
        new Dictionary<string, SchemaFormat>(StringComparer.OrdinalIgnoreCase)
        {
            [".json"] = SchemaFormat.Json,
            [".avsc"] = SchemaFormat.Avro,
            [".proto"] = SchemaFormat.Protobuf,
        };

    /// <summary>Reads every contract in a directory.</summary>
    /// <param name="path">The directory.</param>
    /// <param name="files">What was read, ordered by subject.</param>
    /// <param name="errors">
    /// Files that look like contracts but are not usable. Returned rather than thrown: a
    /// directory with one bad file should report that file, not fail to list the other forty.
    /// </param>
    /// <returns>Whether the directory exists.</returns>
    public static bool TryRead(
        string path, out IReadOnlyList<ContractFile> files, out IReadOnlyList<ContractFileError> errors)
    {
        var found = new List<ContractFile>();
        var problems = new List<ContractFileError>();
        files = found;
        errors = problems;

        if (!Directory.Exists(path))
        {
            return false;
        }

        foreach (var file in Directory.EnumerateFiles(path).Order(StringComparer.Ordinal))
        {
            var extension = System.IO.Path.GetExtension(file);

            if (!Extensions.TryGetValue(extension, out var format))
            {
                // Silently ignored, not reported. A contracts directory routinely holds a
                // README, a .gitkeep or an editor's scratch file, and complaining about those
                // trains people to ignore the output.
                continue;
            }

            var name = System.IO.Path.GetFileNameWithoutExtension(file);
            var subject = SubjectName.Create(name);

            if (subject.IsFailure)
            {
                problems.Add(new ContractFileError(
                    file, $"'{name}' is not a valid subject name: {subject.Error!.Message}"));
                continue;
            }

            string body;
            try
            {
                body = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add(new ContractFileError(file, ex.Message));
                continue;
            }

            found.Add(new ContractFile(subject.Value, format, file, body));
        }

        // Two files for one subject — say, acme.Order.json and acme.Order.avsc — would race to
        // define it, and which one won would depend on enumeration order.
        foreach (var duplicate in found.GroupBy(f => f.Subject.Value, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1))
        {
            problems.Add(new ContractFileError(
                string.Join(", ", duplicate.Select(d => System.IO.Path.GetFileName(d.Path))),
                $"subject '{duplicate.Key}' is defined by more than one file."));
        }

        found.Sort((a, b) => string.CompareOrdinal(a.Subject.Value, b.Subject.Value));
        return true;
    }

    /// <summary>The file a subject would be written to.</summary>
    /// <param name="directory">The contracts directory.</param>
    /// <param name="subject">The subject.</param>
    /// <param name="format">The schema language.</param>
    /// <returns>The path.</returns>
    public static string FileFor(string directory, string subject, SchemaFormat format) =>
        System.IO.Path.Combine(
            directory,
            subject + Extensions.First(e => e.Value == format).Key);
}
