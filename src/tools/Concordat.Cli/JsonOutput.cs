using System.Text.Json.Serialization;

namespace Concordat.Cli;

/// <summary>An error, in <c>--json</c> form.</summary>
/// <param name="Ok">Always false.</param>
/// <param name="ConcordatCode">A stable token to branch on.</param>
/// <param name="Message">What went wrong.</param>
public sealed record ErrorReport(bool Ok, string ConcordatCode, string Message);

/// <summary>One unusable contract file.</summary>
/// <param name="Path">The file.</param>
/// <param name="Reason">What is wrong with it.</param>
public sealed record FileErrorEntry(string Path, string Reason);

/// <summary>A set of unusable contract files.</summary>
/// <param name="Ok">Always false.</param>
/// <param name="ConcordatCode">A stable token to branch on.</param>
/// <param name="Errors">The files.</param>
public sealed record FileErrorReport(bool Ok, string ConcordatCode, IReadOnlyList<FileErrorEntry> Errors);

/// <summary>The result of <c>concordat check</c>.</summary>
/// <param name="Ok">Whether every contract passed.</param>
/// <param name="CheckedCount">How many contracts were examined.</param>
/// <param name="BrokenCount">How many would break their policy.</param>
/// <param name="Subjects">One verdict per contract.</param>
public sealed record CheckReport(
    bool Ok, int CheckedCount, int BrokenCount, IReadOnlyList<Commands.SubjectVerdict> Subjects);

/// <summary>One well-formed contract, as <c>concordat lint</c> reports it.</summary>
/// <param name="Subject">The subject.</param>
/// <param name="SchemaId">The id it would receive, computed offline.</param>
public sealed record LintedSubject(string Subject, string SchemaId);

/// <summary>The result of <c>concordat lint</c>.</summary>
/// <param name="Ok">Always true; failures are reported as errors.</param>
/// <param name="LintedCount">How many contracts were checked.</param>
/// <param name="Subjects">Each contract and its id.</param>
public sealed record LintReport(bool Ok, int LintedCount, IReadOnlyList<LintedSubject> Subjects);

/// <summary>The result of <c>concordat push</c>.</summary>
/// <param name="Ok">Always true; a breaking change is recorded, not refused.</param>
/// <param name="DryRun">Whether anything was actually written.</param>
/// <param name="PushedCount">How many contracts were processed.</param>
/// <param name="Awaiting">How many need approval before they are live.</param>
/// <param name="Subjects">One outcome per contract.</param>
public sealed record PushReport(
    bool Ok, bool DryRun, int PushedCount, int Awaiting, IReadOnlyList<Commands.PushOutcome> Subjects);

/// <summary>The result of <c>concordat promote</c>.</summary>
/// <param name="Ok">Always true.</param>
/// <param name="Subject">The subject.</param>
/// <param name="SchemaId">The content-addressed id, identical in both environments.</param>
/// <param name="SourceOrdinal">The ordinal read from.</param>
/// <param name="TargetOrdinal">The ordinal written to.</param>
/// <param name="Status">Whether the target version is active or awaiting approval.</param>
/// <param name="Created">False when the target was already at this content.</param>
public sealed record PromoteReport(
    bool Ok,
    string Subject,
    string SchemaId,
    int SourceOrdinal,
    int TargetOrdinal,
    string Status,
    bool Created);

/// <summary>The result of <c>concordat impact</c>.</summary>
/// <param name="Ok">False when a consumer breaks, so a pipeline can gate on it.</param>
/// <param name="Subject">The subject analysed.</param>
/// <param name="SchemaId">The candidate's content-addressed id.</param>
/// <param name="BreakingCount">How many registered consumers break.</param>
/// <param name="Consumers">Every registered consumer, breaking ones first.</param>
/// <param name="Contracts">The contracts whose bindings carry this subject.</param>
public sealed record ImpactReport(
    bool Ok,
    string Subject,
    string? SchemaId,
    int BreakingCount,
    IReadOnlyList<ConsumerImpact> Consumers,
    IReadOnlyList<string> Contracts);

/// <summary>One contract written by <c>concordat export</c>.</summary>
/// <param name="Subject">The subject.</param>
/// <param name="Path">Where it was written.</param>
/// <param name="Ordinal">The version exported.</param>
/// <param name="SchemaId">Its content-addressed id.</param>
public sealed record ExportedSubject(string Subject, string Path, int Ordinal, string SchemaId);

/// <summary>One subject <c>concordat export</c> declined to write.</summary>
/// <param name="Subject">The subject.</param>
/// <param name="Reason">Why it was skipped.</param>
public sealed record SkippedSubject(string Subject, string Reason);

/// <summary>The result of <c>concordat export</c>.</summary>
/// <param name="Ok">Always true.</param>
/// <param name="WrittenCount">How many files were written.</param>
/// <param name="Written">Each file.</param>
/// <param name="Skipped">Each subject that was not written, and why.</param>
public sealed record ExportReport(
    bool Ok,
    int WrittenCount,
    IReadOnlyList<ExportedSubject> Written,
    IReadOnlyList<SkippedSubject> Skipped);

/// <summary>One inference finding, in <c>--json</c> form.</summary>
/// <param name="Path">A JSON Pointer into the inferred schema.</param>
/// <param name="Confidence"><c>HIGH</c>, <c>MEDIUM</c> or <c>LOW</c>.</param>
/// <param name="Kind">A stable token.</param>
/// <param name="Message">What was assumed.</param>
public sealed record FindingEntry(string Path, string Confidence, string Kind, string Message);

/// <summary>The result of <c>concordat infer</c>.</summary>
/// <param name="Ok">Always true.</param>
/// <param name="Subject">The subject drafted for.</param>
/// <param name="Path">Where the draft was written.</param>
/// <param name="SampleCount">How many payloads were read.</param>
/// <param name="Findings">Everything worth reviewing, worst first.</param>
public sealed record InferReport(
    bool Ok, string Subject, string Path, int SampleCount, IReadOnlyList<FindingEntry> Findings);

/// <summary>Request body for a dry-run compatibility check.</summary>
/// <param name="Schema">The proposed schema document.</param>
public sealed record CheckRequest(string Schema);

/// <summary>Request body for registering a version.</summary>
/// <param name="Schema">The schema document.</param>
/// <param name="SemanticVersion">An optional intent label.</param>
/// <param name="RegisteredBy">Who is registering.</param>
public sealed record RegisterRequest(string Schema, string? SemanticVersion, string RegisteredBy);

/// <summary>Request body for creating a subject.</summary>
/// <param name="Name">The subject name.</param>
/// <param name="Format">The schema language token.</param>
/// <param name="Owner">Who owns it.</param>
public sealed record CreateSubjectRequest(string Name, string Format, string Owner);

/// <summary>
/// Every type the CLI serialises or deserialises, ahead of time.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because of NativeAOT, and it improved the design anyway.</b> The CLI's
/// original output used anonymous types with reflection-based serialisation, which AOT cannot
/// see through — it produced a dozen IL2026/IL3050 warnings and would have silently emitted
/// empty objects in a published binary.
/// </para>
/// <para>
/// The forced fix was to give every <c>--json</c> shape a real type. That is what the shape
/// deserved regardless: it is a published interface under ADR-019 that other languages'
/// pipelines parse, and it was previously defined only by whichever anonymous object happened
/// to be at the call site.
/// </para>
/// <para>
/// The AOT warnings were <em>not</em> from <c>RabbitMQ.Client</c>, which M3.2 flagged as the
/// likely blocker. Every one came from this project's own JSON usage.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(ErrorReport))]
[JsonSerializable(typeof(FileErrorReport))]
[JsonSerializable(typeof(CheckReport))]
[JsonSerializable(typeof(LintReport))]
[JsonSerializable(typeof(PushReport))]
[JsonSerializable(typeof(PromoteReport))]
[JsonSerializable(typeof(ImpactReport))]
[JsonSerializable(typeof(ExportReport))]
[JsonSerializable(typeof(InferReport))]
[JsonSerializable(typeof(DiffResult))]
public sealed partial class CliJson : JsonSerializerContext;

/// <summary>Every type exchanged with the registry, ahead of time.</summary>
/// <remarks>
/// Separate from <see cref="CliJson"/> because the wire contract with the registry is not
/// indented and does not share the output's formatting.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CheckRequest))]
[JsonSerializable(typeof(RegisterRequest))]
[JsonSerializable(typeof(CreateSubjectRequest))]
[JsonSerializable(typeof(CompatibilityResult))]
[JsonSerializable(typeof(RegisterResult))]
[JsonSerializable(typeof(DiffResult))]
[JsonSerializable(typeof(VersionDetail))]
[JsonSerializable(typeof(SchemaDetail))]
[JsonSerializable(typeof(List<SubjectSummary>))]
[JsonSerializable(typeof(PromoteRequest))]
[JsonSerializable(typeof(PromoteResult))]
[JsonSerializable(typeof(ImpactRequest))]
[JsonSerializable(typeof(ImpactResult))]
public sealed partial class RegistryJson : JsonSerializerContext;
