using Concordat.Application.Abstractions;
using Concordat.Application.Registry;
using Concordat.Domain.Governance;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Concordat.Formats.Abstractions;
using Concordat.Formats.Json;

namespace Concordat.Application.Tests.TestSupport;

/// <summary>
/// An in-memory <see cref="ISubjectRepository"/> that records what the handler asked it.
/// </summary>
/// <remarks>
/// <see cref="Seed"/> puts a subject in place without going through <see cref="Add"/>, so
/// <see cref="Added"/> stays a record of what the handler under test wrote rather than of what
/// the arrangement did.
/// </remarks>
internal sealed class FakeSubjects : ISubjectRepository
{
    private readonly List<Subject> _subjects = [];

    /// <summary>How many times a handler looked a subject up.</summary>
    public int Finds { get; private set; }

    /// <summary>Subjects staged for insert by the handler.</summary>
    public List<Subject> Added { get; } = [];

    public Task<Subject?> FindAsync(
        EnvironmentId environmentId, SubjectName name, CancellationToken cancellationToken)
    {
        Finds++;

        return Task.FromResult(
            _subjects.Find(s => s.EnvironmentId == environmentId && s.Name == name));
    }

    public Task<IReadOnlyList<Subject>> ListAsync(
        EnvironmentId environmentId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Subject>>(
            [.. _subjects
                .Where(s => s.EnvironmentId == environmentId)
                .OrderBy(s => s.Name.Value, StringComparer.Ordinal)]);

    public void Add(Subject subject)
    {
        Added.Add(subject);
        _subjects.Add(subject);
    }

    /// <summary>Arranges an existing subject without counting as a write.</summary>
    public FakeSubjects Seed(Subject subject)
    {
        _subjects.Add(subject);
        return this;
    }
}

/// <summary>
/// An in-memory <see cref="ISchemaRepository"/>, with the reachability gate under test control.
/// </summary>
/// <remarks>
/// <see cref="Hide"/> models the case the global schema table cannot express structurally: a
/// schema that exists but belongs to another tenant. Without a way to arrange that, the
/// authorisation branch in <c>GetSchemaHandler</c> is unreachable from a unit test.
/// </remarks>
internal sealed class FakeSchemas : ISchemaRepository
{
    private readonly Dictionary<string, Schema> _stored = new(StringComparer.Ordinal);
    private readonly HashSet<string> _hidden = new(StringComparer.Ordinal);
    private readonly List<(SchemaId Id, SubjectName Subject, int Version)> _usages = [];

    /// <summary>How many schemas a handler staged for insert.</summary>
    public int Staged { get; private set; }

    /// <summary>How many times a handler read the store.</summary>
    public int Finds { get; private set; }

    public Task<Schema?> FindAsync(SchemaId id, CancellationToken cancellationToken)
    {
        Finds++;
        return Task.FromResult(_stored.GetValueOrDefault(id.Value));
    }

    public Task<bool> IsReachableByCurrentTenantAsync(
        SchemaId id, CancellationToken cancellationToken) =>
        Task.FromResult(_stored.ContainsKey(id.Value) && !_hidden.Contains(id.Value));

    public Task<Schema> AddIfMissingAsync(Schema schema, CancellationToken cancellationToken)
    {
        Staged++;

        if (_stored.TryGetValue(schema.Id.Value, out var existing))
        {
            return Task.FromResult(existing);
        }

        _stored[schema.Id.Value] = schema;
        return Task.FromResult(schema);
    }

    public Task<IReadOnlyList<(SubjectName Subject, int Version)>> FindUsagesAsync(
        SchemaId id, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<(SubjectName Subject, int Version)>>(
            [.. _usages.Where(u => u.Id == id).Select(u => (u.Subject, u.Version))]);

    /// <summary>Arranges a stored schema without counting as a write.</summary>
    public FakeSchemas Seed(Schema schema)
    {
        _stored[schema.Id.Value] = schema;
        return this;
    }

    /// <summary>Makes a stored schema unreachable, as another tenant's would be.</summary>
    public FakeSchemas Hide(SchemaId id)
    {
        _hidden.Add(id.Value);
        return this;
    }

    /// <summary>Records that a subject version points at a schema.</summary>
    public FakeSchemas Used(SchemaId id, SubjectName subject, int version)
    {
        _usages.Add((id, subject, version));
        return this;
    }
}

/// <summary>Counts commits, so a test can assert that a refusal wrote nothing.</summary>
internal sealed class RecordingUnitOfWork : IUnitOfWork
{
    /// <summary>How many times the handler committed.</summary>
    public int Saves { get; private set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        Saves++;
        return Task.FromResult(0);
    }
}

/// <summary>Collects audit entries, so a test can assert what a handler recorded (M7.4).</summary>
/// <remarks>
/// Entries are held rather than discarded because "what did this write to the trail" is a
/// behaviour worth asserting on: an audit log is the one output nobody notices is wrong until
/// somebody needs it.
/// </remarks>
internal sealed class RecordingAuditLog : IAuditLog
{
    private readonly List<AuditEntry> _entries = [];

    /// <summary>Everything appended, in the order it was appended.</summary>
    public IReadOnlyList<AuditEntry> Entries => _entries;

    /// <summary>The actions appended, for terse assertions.</summary>
    public IReadOnlyList<AuditAction> Actions => [.. _entries.Select(e => e.Action)];

    public void Append(AuditEntry entry) => _entries.Add(entry);

    public Task<IReadOnlyList<AuditEntry>> QueryAsync(
        AuditFilter filter, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AuditEntry>>(_entries);
}

/// <summary>Mints one stable <see cref="EnvironmentId"/> per environment name.</summary>
/// <remarks>
/// Reference resolution goes name → id, so a test that seeds a subject has to be able to ask
/// for the same id the handler will.
/// </remarks>
internal sealed class FakeEnvironments : IEnvironmentResolver
{
    private readonly Dictionary<string, EnvironmentId> _byName = new(StringComparer.Ordinal);

    public EnvironmentId Resolve(string name)
    {
        if (!_byName.TryGetValue(name, out var id))
        {
            id = EnvironmentId.New();
            _byName[name] = id;
        }

        return id;
    }
}

/// <summary>The real JSON implementations, and nothing else registered.</summary>
/// <remarks>
/// Refusing any other format mirrors <c>SchemaFormatRegistry</c>: falling back to JSON for an
/// Avro subject would produce a confidently wrong verdict rather than an obvious failure.
/// </remarks>
internal sealed class JsonFormats : ISchemaFormatRegistry
{
    private static readonly JsonSchemaCanonicalizer JsonCanonicalizer = new();
    private static readonly JsonSchemaCompatibilityChecker JsonChecker = new();
    private static readonly JsonSchemaReferenceExtractor JsonExtractor = new();
    private static readonly JsonSchemaPortabilityChecker JsonPortability = new();

    public ISchemaCanonicalizer Canonicalizer(SchemaFormat format) =>
        Only(format, JsonCanonicalizer);

    public ICompatibilityChecker Checker(SchemaFormat format) => Only(format, JsonChecker);

    public ISchemaReferenceExtractor ReferenceExtractor(SchemaFormat format) =>
        Only(format, JsonExtractor);

    public ISchemaPortabilityChecker? PortabilityChecker(SchemaFormat format) =>
        format is SchemaFormat.Json ? JsonPortability : null;

    private static T Only<T>(SchemaFormat format, T json) =>
        format is SchemaFormat.Json
            ? json
            : throw new NotSupportedException($"Only JSON is registered; asked for {format}.");
}

/// <summary>The real JSON bundler, and nothing else registered.</summary>
internal sealed class JsonBundlers : ISchemaBundlerRegistry
{
    private static readonly JsonSchemaBundler JsonBundler = new();

    public ISchemaBundler Bundler(SchemaFormat format) =>
        format is SchemaFormat.Json
            ? JsonBundler
            : throw new NotSupportedException($"Only JSON is registered; asked for {format}.");
}

/// <summary>
/// The real evaluator, wrapped so a test can see exactly what the handler handed it.
/// </summary>
/// <remarks>
/// The arguments matter as much as the answer. A handler that passes the environment default
/// instead of the subject's policy, or the caller's format instead of the subject's, still
/// produces a plausible verdict — one computed against the wrong rules.
/// </remarks>
internal sealed class RecordingEvaluator(ICompatibilityEvaluator inner) : ICompatibilityEvaluator
{
    public int Calls { get; private set; }

    public SchemaFormat Format { get; private set; }

    public IReadOnlyList<PriorSchema> Priors { get; private set; } = [];

    public CompatibilityPolicy Policy { get; private set; }

    public ContentModel ContentModel { get; private set; }

    public Result<EvaluatedProposal> Evaluate(
        SchemaFormat format,
        string? body,
        IReadOnlyList<PriorSchema> priorVersions,
        CompatibilityPolicy policy,
        ContentModel contentModel)
    {
        Calls++;
        Format = format;
        Priors = priorVersions;
        Policy = policy;
        ContentModel = contentModel;

        return inner.Evaluate(format, body, priorVersions, policy, contentModel);
    }
}

/// <summary>
/// An evaluator that answers however the test says, without consulting any engine.
/// </summary>
/// <remarks>
/// For the aggregate refusals a handler can only reach with a verdict the real evaluator would
/// never produce — a verdict stamped with the wrong policy, or a schema in the wrong format.
/// Those branches are unreachable end to end, which is exactly why nothing else covers them.
/// </remarks>
internal sealed class StubEvaluator(EvaluatedProposal proposal) : ICompatibilityEvaluator
{
    public int Calls { get; private set; }

    public SchemaFormat Format { get; private set; }

    public Result<EvaluatedProposal> Evaluate(
        SchemaFormat format,
        string? body,
        IReadOnlyList<PriorSchema> priorVersions,
        CompatibilityPolicy policy,
        ContentModel contentModel)
    {
        Calls++;
        Format = format;

        return Result<EvaluatedProposal>.Success(proposal);
    }
}
