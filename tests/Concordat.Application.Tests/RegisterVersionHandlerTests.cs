using Concordat.Application.Registry;
using Concordat.Application.Tests.TestSupport;
using Concordat.Domain.Identity;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Concordat.Formats.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Concordat.Application.Tests;

/// <summary>
/// The registration handler's refusals and its ordering, which the HTTP tests reach only
/// through happy paths.
/// </summary>
public class RegisterVersionHandlerTests
{
    private const string V1 = """{"type":"object","properties":{"id":{"type":"string"}}}""";

    private const string V1Required =
        """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}""";

    private const string V1PlusOptional =
        """{"type":"object","properties":{"id":{"type":"string"},"note":{"type":"string"}}}""";

    private readonly EnvironmentId _environment = EnvironmentId.New();
    private readonly FakeSubjects _subjects = new();
    private readonly FakeSchemas _schemas = new();
    private readonly RecordingUnitOfWork _unitOfWork = new();
    private readonly RecordingEvaluator _evaluator = new(new CompatibilityEvaluator(new JsonFormats()));
    private readonly FakeTimeProvider _clock = new(Build.At);

    private readonly RecordingAuditLog _audit = new();
    private readonly RecordingOutbox _outbox = new();
    private readonly SettableBillingGate _billing = new();
    private readonly FakeEnvironmentStore _environments = new();
    private readonly SettableCaller _caller = new();

    private RegisterVersionHandler Handler() =>
        new(_subjects, _schemas, _environments, _caller, _evaluator,
            _audit, _outbox, _billing, _unitOfWork, _clock);

    private RegisterVersionHandler Handler(ICompatibilityEvaluator evaluator) =>
        new(_subjects, _schemas, _environments, _caller, evaluator,
            _audit, _outbox, _billing, _unitOfWork, _clock);

    private Task<Result<RegisterVersionResult>> RegisterAsync(
        string? body,
        string subject = "acme.orders.OrderCreated",
        string? semver = null,
        string? changelog = null,
        string registeredBy = "alice") =>
        Handler().HandleAsync(
            new RegisterVersionCommand(_environment, subject, body, semver, changelog, registeredBy),
            CancellationToken.None);

    // ------------------------------------------------- the registration policy (M7.1)

    [Fact]
    public async Task ACiOnlyEnvironment_RefusesAProducerAndCanonicalisesNothing()
    {
        _environments.Seed(_environment, "prod", RegistrationPolicy.CiOnly);
        _subjects.Seed(Build.Subject(_environment));
        _caller.Holding(Scope.SubjectWrite);

        var result = await RegisterAsync(V1);

        Assert.Equal(ConcordatCodes.RegistrationPolicyForbids, result.Error!.Code);

        // Refused before anything is spent: no meter call, no canonicalisation, no write.
        Assert.Equal(0, _billing.Calls);
        Assert.Equal(0, _evaluator.Calls);
        Assert.Equal(0, _schemas.Staged);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task ACiOnlyEnvironment_AdmitsAPipeline()
    {
        _environments.Seed(_environment, "prod", RegistrationPolicy.CiOnly);
        _subjects.Seed(Build.Subject(_environment));
        _caller.Holding(Scope.SubjectWrite, Scope.Ci);

        var result = await RegisterAsync(V1);

        Assert.True(result.IsSuccess, result.Error?.Message);
    }

    [Fact]
    public async Task AClosedEnvironment_RefusesEvenCi()
    {
        _environments.Seed(_environment, "vault", RegistrationPolicy.Closed);
        _subjects.Seed(Build.Subject(_environment));
        _caller.Holding(Scope.SubjectWrite, Scope.Ci);

        var result = await RegisterAsync(V1);

        Assert.Equal(ConcordatCodes.RegistrationPolicyForbids, result.Error!.Code);
    }

    [Fact]
    public async Task AnEnvironmentWithNoRow_HasNoPolicyAndIsAllowed()
    {
        // Routes accept an environment name before an Environment aggregate exists — the id is
        // derived from the name. Refusing there would refuse every registration on a registry
        // nobody had explicitly configured, which is every quickstart.
        _subjects.Seed(Build.Subject(_environment));
        _caller.Holding(Scope.SubjectWrite);

        var result = await RegisterAsync(V1);

        Assert.True(result.IsSuccess, result.Error?.Message);
    }

    [Fact]
    public async Task AMonthlyVersionLimit_RefusesBeforeTheSchemaIsEvenCanonicalised()
    {
        // Canonicalising and hashing is the expensive part of a registration. A tenant at their
        // monthly limit should not pay for work that is going to be thrown away.
        _subjects.Seed(Build.Subject(_environment));
        _billing.Allowed = false;

        var result = await RegisterAsync(V1);

        Assert.Equal(ConcordatCodes.PlanLimitReached, result.Error!.Code);
        Assert.Equal(0, _evaluator.Calls);
        Assert.Equal(0, _schemas.Staged);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task AnInvalidSubjectName_IsRefusedBeforeTheRepositoryIsTouched()
    {
        var result = await RegisterAsync(V1, subject: "acme.orders.Order Created");

        Assert.Equal(ConcordatCodes.SubjectNameInvalid, result.Error!.Code);

        // Validating the name after the lookup would be harmless here, but it is the pattern
        // that lets an unvalidated value reach a query. Nothing is asked of the store.
        Assert.Equal(0, _subjects.Finds);
        Assert.Equal(0, _evaluator.Calls);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task AnEmptyRegisteredBy_IsRefusedWithActorIdInvalid()
    {
        // The actor is the only attribution the audit trail gets. Recording a blank one is
        // worse than refusing, because it looks like a record and answers nothing.
        var result = await RegisterAsync(V1, registeredBy: "   ");

        Assert.Equal(ConcordatCodes.ActorIdInvalid, result.Error!.Code);
        Assert.Equal(0, _subjects.Finds);
    }

    [Fact]
    public async Task AnUnknownSubject_IsSubjectNotFoundAndSaysToCreateItFirst()
    {
        var result = await RegisterAsync(V1);

        Assert.Equal(ConcordatCodes.SubjectNotFound, result.Error!.Code);

        // Registration does not auto-create. Confluent's does, which is how a typo in a
        // producer's subject name becomes a permanent empty subject nobody notices.
        Assert.Contains("Create it first", result.Error.Message, StringComparison.Ordinal);
        Assert.Empty(_subjects.Added);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task AMalformedSemverLabel_IsRefusedBeforeAnythingIsEvaluatedOrRead()
    {
        _subjects.Seed(Build.Subject(_environment));

        var result = await RegisterAsync(V1, semver: "1.0");

        Assert.Equal(ConcordatCodes.SemverInvalid, result.Error!.Code);

        // Canonicalising and hashing a schema costs real work, and prior loading is one read
        // per version. A label typo must not pay for either.
        Assert.Equal(0, _evaluator.Calls);
        Assert.Equal(0, _schemas.Finds);
        Assert.Equal(0, _schemas.Staged);
    }

    [Fact]
    public async Task APrereleaseSemverLabel_CarriesItsOwnCodeRatherThanTheGenericOne()
    {
        // A client branches on this to tell the author "drop the -rc1", which is actionable,
        // from "that is not a version at all", which is not the same fix.
        _subjects.Seed(Build.Subject(_environment));

        var result = await RegisterAsync(V1, semver: "1.0.0-rc1");

        Assert.Equal(ConcordatCodes.SemverPrereleaseUnsupported, result.Error!.Code);
    }

    [Fact]
    public async Task AMalformedBody_PropagatesTheCanonicalisersCodeAndStagesNothing()
    {
        _subjects.Seed(Build.Subject(_environment));

        var result = await RegisterAsync("not json");

        Assert.Equal(ConcordatCodes.SchemaMalformed, result.Error!.Code);
        Assert.Equal(0, _schemas.Staged);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task AnEmptyBody_IsSchemaBodyEmptyRatherThanMalformed()
    {
        _subjects.Seed(Build.Subject(_environment));

        var result = await RegisterAsync("   ");

        Assert.Equal(ConcordatCodes.SchemaBodyEmpty, result.Error!.Code);
    }

    [Fact]
    public async Task AnUnimplementedDialect_IsRefusedAsDialectUnsupportedNotMalformed()
    {
        // The document is well-formed; the registry is declining to interpret it under rules it
        // was not written against. 'items' changed meaning between drafts, so a schema judged
        // under the wrong one gets a confidently wrong verdict.
        _subjects.Seed(Build.Subject(_environment));

        var result = await RegisterAsync(
            """{"$schema":"http://json-schema.org/draft-07/schema#","type":"object"}""");

        Assert.Equal(ConcordatCodes.SchemaDialectUnsupported, result.Error!.Code);
        Assert.Equal(0, _schemas.Staged);
    }

    [Fact]
    public async Task ARetiredSubject_AcceptsNoFurtherVersions()
    {
        var subject = Build.Subject(_environment);
        Assert.True(subject.Retire().IsSuccess);
        _subjects.Seed(subject);

        var result = await RegisterAsync(V1);

        Assert.Equal(ConcordatCodes.SubjectRetired, result.Error!.Code);
        Assert.Empty(subject.Versions);
    }

    [Fact]
    public async Task ARefusedRegistrationCommitsNothingEvenThoughTheSchemaWasAlreadyStaged()
    {
        // The handler stages the schema before the aggregate's rules run, so the only thing
        // standing between a refused registration and a stored schema is that nothing commits.
        // If a future change moves SaveChangesAsync earlier, or adds one inside the repository,
        // a refused proposal starts leaving a row behind.
        var subject = Build.Subject(_environment);
        Assert.True(subject.Retire().IsSuccess);
        _subjects.Seed(subject);

        var result = await RegisterAsync(V1);

        Assert.True(result.IsFailure);
        Assert.Equal(1, _schemas.Staged);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task ABreakingChangeLabelledMinor_IsRefusedAndLeavesTheHistoryAlone()
    {
        var subject = Build.Subject(_environment);
        subject.Register(_schemas, V1, semver: "1.0.0");
        _subjects.Seed(subject);

        var result = await RegisterAsync(V1Required, semver: "1.1.0");

        // ADR-004: over-claiming is allowed, under-claiming is not. A breaking change wearing a
        // MINOR label is precisely the lie the registry exists to prevent.
        Assert.Equal(ConcordatCodes.SemverLabelUnderstatesBreakage, result.Error!.Code);
        Assert.Single(subject.Versions);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task ALabelThatDoesNotIncreaseIsRefusedWithItsOwnCode()
    {
        var subject = Build.Subject(_environment);
        subject.Register(_schemas, V1, semver: "2.0.0");
        _subjects.Seed(subject);

        var result = await RegisterAsync(V1PlusOptional, semver: "1.9.0");

        Assert.Equal(ConcordatCodes.SemverNotIncreasing, result.Error!.Code);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task AChangelogOverTheLimit_IsRefusedRatherThanTruncated()
    {
        // Truncating would silently discard the part the author cared about, and the record
        // would still look complete to whoever reads it later.
        _subjects.Seed(Build.Subject(_environment));

        var result = await RegisterAsync(
            V1, changelog: new string('x', Subject.MaxChangelogLength + 1));

        Assert.Equal(ConcordatCodes.ChangelogTooLong, result.Error!.Code);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task RejectedVersionsAreNotPartOfTheHistoryANewVersionMustMatch()
    {
        // They were declined precisely so nothing would depend on them. Leaving a rejected
        // proposal in the comparison set means a change is judged against a schema the registry
        // has already refused to serve — and under the default non-transitive mode it is the
        // highest ordinal that gets compared, so the rejected one would be the ONLY comparand.
        var subject = Build.Subject(_environment);
        var first = subject.Register(_schemas, V1);
        subject.Register(_schemas, V1Required, breaking: true);
        subject.RejectVersion(2);
        _subjects.Seed(subject);

        var result = await RegisterAsync(V1PlusOptional);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal([1], _evaluator.Priors.Select(p => p.Ordinal));
        Assert.Equal(first.Body, Assert.Single(_evaluator.Priors).CanonicalBody);
    }

    [Fact]
    public async Task AVersionAwaitingApprovalIsNotPartOfTheHistory()
    {
        // History is what was approved. A proposal awaiting review has not been accepted, so
        // letting it into the comparison set lets a proposal justify itself — which is exactly
        // how the approval gate came to be defeatable by submitting the same schema twice.
        // ResubmittingAPendingBreakingSchemaBypassesTheApprovalGate is the other half of this.
        var subject = Build.Subject(_environment);
        subject.Register(_schemas, V1);
        subject.Register(_schemas, V1Required, breaking: true);
        _subjects.Seed(subject);

        await RegisterAsync(V1PlusOptional);

        Assert.Equal([1], _evaluator.Priors.Select(p => p.Ordinal));
    }

    [Fact]
    public async Task EveryApprovedVersionIsPartOfTheHistory()
    {
        // The filter is on status, not on the latest pointer, so an approved version below the
        // tip still counts. Under a transitive policy it is compared directly; under the
        // default it is what the tip was itself judged against.
        var subject = Build.Subject(_environment);
        subject.Register(_schemas, V1);
        subject.Register(_schemas, V1PlusOptional);
        _subjects.Seed(subject);

        await RegisterAsync(V1Required);

        Assert.Equal([1, 2], _evaluator.Priors.Select(p => p.Ordinal));
    }

    [Fact]
    public async Task ResubmittingAPendingBreakingSchemaBypassesTheApprovalGate()
    {
        var subject = Build.Subject(_environment);
        _subjects.Seed(subject);

        Assert.True((await RegisterAsync(V1)).IsSuccess);

        var pending = await RegisterAsync(V1Required);
        Assert.Equal(VersionStatus.AwaitingApproval, pending.Value.Status);
        Assert.Equal(1, subject.Latest!.Ordinal);

        // The same breaking schema, submitted again. Nobody has approved anything.
        var again = await RegisterAsync(V1Required);

        Assert.Equal(VersionStatus.AwaitingApproval, again.Value.Status);
        Assert.Equal(1, subject.Latest.Ordinal);
    }

    [Fact]
    public async Task AVersionWhoseSchemaIsNotStoredIsSkippedRatherThanFailingTheRegistration()
    {
        // A dangling version reference must not wedge the subject forever. It does mean the
        // proposal is judged against less history than the subject appears to have, so the
        // permissive direction is the one to know about.
        var subject = Build.Subject(_environment);
        var orphan = subject.RegisterVersion(
            Build.JsonSchema(V1), CompatibilityVerdict.Compatible(CompatibilityPolicy.Default),
            null, null, Build.Actor(), Build.At);
        Assert.True(orphan.IsSuccess, orphan.Error?.Message);
        _subjects.Seed(subject);

        var result = await RegisterAsync(V1Required);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Empty(_evaluator.Priors);

        // With nothing to compare against the engine reports compatible, so this registers
        // ACTIVE — a change that would otherwise have awaited approval.
        Assert.Equal(VersionStatus.Active, result.Value.Status);
    }

    [Fact]
    public async Task TheEvaluatorIsAskedForTheSubjectsEffectivePolicyNotTheEnvironmentDefault()
    {
        // The aggregate refuses a verdict evaluated under a policy that is not the subject's,
        // so passing the environment default here would make every registration on a subject
        // with an explicit policy fail with verdict_policy_mismatch.
        var policy = new CompatibilityPolicy(CompatibilityMode.Full, CompatibilitySurface.Source);
        _subjects.Seed(Build.Subject(_environment, policy: policy));

        await RegisterAsync(V1);

        Assert.Equal(policy, _evaluator.Policy);
    }

    [Fact]
    public async Task TheEvaluatorIsAskedForTheSubjectsContentModel()
    {
        // The content model is explicit subject configuration, never inferred from the document
        // (DESIGN §7). Defaulting it here would reintroduce exactly the per-version drift the
        // field exists to prevent.
        _subjects.Seed(Build.Subject(_environment, contentModel: ContentModel.Closed));

        await RegisterAsync(V1);

        Assert.Equal(ContentModel.Closed, _evaluator.ContentModel);
    }

    [Fact]
    public async Task TheEvaluatorIsAskedForTheSubjectsFormatNotTheCallers()
    {
        // A subject keeps one format for its whole life, and the command carries no format at
        // all. Reading it from anywhere but the subject would canonicalise an Avro document
        // with the JSON canonicaliser and mint an id nothing can reproduce.
        var stub = Compatible(Build.AvroSchema());
        _subjects.Seed(Build.Subject(_environment, format: SchemaFormat.Avro));

        var result = await Handler(stub).HandleAsync(
            new RegisterVersionCommand(_environment, "acme.orders.OrderCreated", V1, null, null, "alice"),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(SchemaFormat.Avro, stub.Format);
    }

    [Fact]
    public async Task ASchemaInTheWrongFormatIsRefusedRatherThanSwallowed()
    {
        // Unreachable through the real evaluator, which is handed the subject's format — which
        // is why nothing else covers it. The handler must still surface the aggregate's code if
        // an evaluator ever answers in a format other than the one it was asked for.
        _subjects.Seed(Build.Subject(_environment, format: SchemaFormat.Json));

        var result = await Handler(Compatible(Build.AvroSchema())).HandleAsync(
            new RegisterVersionCommand(_environment, "acme.orders.OrderCreated", V1, null, null, "alice"),
            CancellationToken.None);

        Assert.Equal(ConcordatCodes.FormatMismatch, result.Error!.Code);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task AVerdictEvaluatedUnderTheWrongPolicyIsRefusedRatherThanSwallowed()
    {
        // The one disagreement the aggregate can detect on its own, and the handler must not
        // absorb it: a verdict computed under a laxer policy than the subject's would wave a
        // breaking change through as ACTIVE.
        var policy = new CompatibilityPolicy(CompatibilityMode.Full, CompatibilitySurface.Source);
        var subject = Build.Subject(_environment, policy: policy);
        subject.Register(_schemas, V1);
        _subjects.Seed(subject);

        var stub = Compatible(Build.JsonSchema(V1PlusOptional), CompatibilityPolicy.Default);

        var result = await Handler(stub).HandleAsync(
            new RegisterVersionCommand(_environment, "acme.orders.OrderCreated", V1PlusOptional, null, null, "alice"),
            CancellationToken.None);

        Assert.Equal(ConcordatCodes.VerdictPolicyMismatch, result.Error!.Code);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task TheRegistrationTimestampComesFromTheInjectedClock()
    {
        // Not DateTimeOffset.UtcNow inside the aggregate: the audit trail has to be arrangeable,
        // and a handler that reads the wall clock cannot be tested for ordering at all.
        var moment = new DateTimeOffset(2031, 2, 3, 4, 5, 6, TimeSpan.FromHours(2));
        _clock.SetUtcNow(moment);

        var subject = Build.Subject(_environment);
        _subjects.Seed(subject);

        await RegisterAsync(V1);

        Assert.Equal(moment.ToUniversalTime(), Assert.Single(subject.Versions).RegisteredAt);
    }

    [Fact]
    public async Task TheResultCarriesDivergencesThePolicyToleratedAsWellAsViolations()
    {
        // Widening 'integer' to 'number' leaves every document valid but changes the generated
        // type, so it is a Source-surface finding that the default WireJson policy permits.
        // Registering it silently would hide the one fact a consumer generating code needs.
        var subject = Build.Subject(_environment);
        subject.Register(_schemas, """{"type":"object","properties":{"n":{"type":"integer"}}}""");
        _subjects.Seed(subject);

        var result = await RegisterAsync("""{"type":"object","properties":{"n":{"type":"number"}}}""");

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(VersionStatus.Active, result.Value.Status);
        Assert.Empty(result.Value.Report.BreakingChanges);

        var divergence = Assert.Single(result.Value.Report.AllDivergences);
        Assert.Equal(BreakingChangeKinds.IntegerWidenedToNumber, divergence.Kind);
        Assert.Equal(SemverBump.Minor, result.Value.Report.SuggestedBump);
    }

    [Fact]
    public async Task PortabilityWarningsAreReturnedWithASuccessfulRegistration()
    {
        // Warnings, not refusals: the schema is legal and the author probably meant it. The
        // moment they are most likely to act on it is the moment they registered it.
        _subjects.Seed(Build.Subject(_environment));

        var result = await RegisterAsync(
            """{"type":"object","properties":{"a":{"oneOf":[{"type":"string"},{"type":"integer"}]}}}""");

        Assert.True(result.IsSuccess, result.Error?.Message);

        var finding = Assert.Single(result.Value.Portability);
        Assert.Equal(PortabilityKinds.KeywordNotCompared, finding.Kind);
        Assert.Equal(PortabilitySeverity.Warning, finding.Severity);
    }

    private static StubEvaluator Compatible(Schema schema, CompatibilityPolicy? policy = null)
    {
        var under = policy ?? CompatibilityPolicy.Default;

        return new StubEvaluator(new EvaluatedProposal(
            schema,
            new CompatibilityReport(true, [], [], SemverBump.None),
            CompatibilityVerdict.Compatible(under),
            []));
    }
}
