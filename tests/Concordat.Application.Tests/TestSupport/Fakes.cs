using Concordat.Application.Abstractions;
using Concordat.Application.Registry;
using Concordat.Domain.Billing;
using Concordat.Domain.Governance;
using Concordat.Domain.Identity;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Concordat.Formats.Abstractions;
using Concordat.Formats.Json;
using RegistryEnvironment = Concordat.Domain.Registry.Environment;

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

    /// <summary>How many times a handler read the store one id at a time.</summary>
    public int Finds { get; private set; }

    /// <summary>
    /// How many times a handler batch-read the store — kept separate from <see cref="Finds"/>
    /// so a test can assert the N+1 fix directly: a multi-version history costs one of these,
    /// not one <see cref="Finds"/> per version.
    /// </summary>
    public int BatchFinds { get; private set; }

    public Task<Schema?> FindAsync(SchemaId id, CancellationToken cancellationToken)
    {
        Finds++;
        return Task.FromResult(_stored.GetValueOrDefault(id.Value));
    }

    public Task<IReadOnlyDictionary<SchemaId, Schema>> FindManyAsync(
        IReadOnlyCollection<SchemaId> ids, CancellationToken cancellationToken)
    {
        BatchFinds++;

        var result = new Dictionary<SchemaId, Schema>();
        foreach (var id in ids)
        {
            if (_stored.TryGetValue(id.Value, out var schema))
            {
                result[id] = schema;
            }
        }

        return Task.FromResult<IReadOnlyDictionary<SchemaId, Schema>>(result);
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

/// <summary>Collects staged notifications, so a test can assert what would be sent (M7.5).</summary>
internal sealed class RecordingOutbox : IOutbox
{
    private readonly List<OutboxMessage> _staged = [];

    /// <summary>
    /// The tenant every staged message is claimed under. Defaults to the single self-hosted
    /// tenant; a multi-tenant test overrides it per message via <see cref="StageFor"/>.
    /// </summary>
    private readonly Dictionary<OutboxMessage, TenantId> _tenants = [];

    /// <summary>Everything staged, in order.</summary>
    public IReadOnlyList<OutboxMessage> Staged => _staged;

    /// <summary>The events staged, for terse assertions.</summary>
    public IReadOnlyList<NotificationEvent> Events => [.. _staged.Select(m => m.Event)];

    public void Stage(OutboxMessage message) => StageFor(TenantId.SelfHosted, message);

    /// <summary>Stages a message as belonging to a specific tenant, for cross-tenant tests.</summary>
    /// <param name="tenantId">The staging tenant.</param>
    /// <param name="message">The message.</param>
    public void StageFor(TenantId tenantId, OutboxMessage message)
    {
        _staged.Add(message);
        _tenants[message] = tenantId;
    }

    public Task<IReadOnlyList<(TenantId TenantId, OutboxMessage Message)>>
        ClaimDueAcrossTenantsAsync(
            DateTimeOffset now, int batchSize, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<(TenantId, OutboxMessage)>>(
            [.. _staged.Where(m => m.DeliveredAt is null && !m.Parked && m.NextAttemptAt <= now)
                .Take(batchSize)
                .Select(m => (_tenants[m], m))]);

    public Task<(int Pending, int Parked)> DepthAsync(CancellationToken cancellationToken) =>
        Task.FromResult((
            _staged.Count(m => m.DeliveredAt is null && !m.Parked),
            _staged.Count(m => m.Parked)));
}

/// <summary>An in-memory <see cref="IWebhookSigningKeyStore"/>, so a test can assert what a
/// webhook was signed with without touching Data Protection.</summary>
internal sealed class FakeWebhookSigningKeyStore : IWebhookSigningKeyStore
{
    private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);
    private int _next;

    public Task<(string Reference, string Secret)> GenerateAsync(CancellationToken cancellationToken)
    {
        var reference = $"signing-key-{++_next}";
        var secret = $"secret-for-{reference}";
        _secrets[reference] = secret;
        return Task.FromResult((reference, secret));
    }

    public Task<string?> ResolveAsync(string reference, CancellationToken cancellationToken) =>
        Task.FromResult(_secrets.GetValueOrDefault(reference));

    public Task RemoveAsync(string reference, CancellationToken cancellationToken)
    {
        _secrets.Remove(reference);
        return Task.CompletedTask;
    }
}

/// <summary>A billing gate a test can close, to prove a limit is enforced (M9.3).</summary>
/// <remarks>
/// Open by default, because a handler test is about the handler. The interesting case is the
/// one where it is shut: the check has to happen before anything is staged, or a refused
/// creation still leaves a row behind.
/// </remarks>
internal sealed class SettableBillingGate : IBillingGate
{
    /// <summary>Whether to allow creation. True unless a test says otherwise.</summary>
    public bool Allowed { get; set; } = true;

    /// <summary>How many times the gate was consulted.</summary>
    public int Calls { get; private set; }

    public Task<BillingVerdict> MayCreateAsync(Meter meter, CancellationToken cancellationToken)
    {
        Calls++;

        return Task.FromResult(
            Allowed ? BillingVerdict.Yes : new BillingVerdict(false, Limit: 1, Tier: Tier.Free));
    }
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

/// <summary>
/// An in-memory <see cref="IEnvironmentRepository"/>, empty until a test seeds it.
/// </summary>
/// <remarks>
/// Empty is the interesting default. A registry accepts an environment name in a route before
/// any <see cref="Environment"/> row exists — <c>DerivedEnvironmentResolver</c> hashes the name
/// to a stable id — so "no environment, therefore no registration policy, therefore allowed" is
/// the ordinary path and not a corner case.
/// </remarks>
internal sealed class FakeEnvironmentStore : IEnvironmentRepository
{
    private readonly List<RegistryEnvironment> _all = [];

    /// <summary>Seeds an environment carrying a registration policy.</summary>
    public void Seed(EnvironmentId id, string name, RegistrationPolicy policy) =>
        _all.Add(RegistryEnvironment.Create(name, Build.At, null, null, policy, id).Value);

    public Task<RegistryEnvironment?> FindAsync(EnvironmentName name, CancellationToken cancellationToken) =>
        Task.FromResult(_all.FirstOrDefault(e => e.Name == name));

    public Task<RegistryEnvironment?> FindAsync(EnvironmentId id, CancellationToken cancellationToken) =>
        Task.FromResult(_all.FirstOrDefault(e => e.Id == id));

    public Task<RegistryEnvironment?> FindAcrossTenantsAsync(
        TenantId tenantId, EnvironmentId id, CancellationToken cancellationToken) =>
        Task.FromResult(_all.FirstOrDefault(e => e.Id == id));

    public Task<IReadOnlyList<RegistryEnvironment>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RegistryEnvironment>>(_all);

    public void Add(RegistryEnvironment environment) => _all.Add(environment);
}

/// <summary>A caller a test can rewrite between calls.</summary>
internal sealed class SettableCaller : ICallerContext
{
    /// <summary>
    /// Every scope by default, so a test that is not about authorisation does not have to
    /// enumerate them — and so a test that <em>is</em> about authorisation has to say so.
    /// </summary>
    public Caller Current { get; set; } = new(
        CredentialKind.ApiKey,
        TenantId.SelfHosted,
        ScopeSet.Of(Scope.All),
        ActorId.Create("tests").Value);

    /// <summary>Narrows the caller to exactly these scopes.</summary>
    public void Holding(params string[] scopes) =>
        Current = Current with { Scopes = ScopeSet.Of(scopes) };
}
