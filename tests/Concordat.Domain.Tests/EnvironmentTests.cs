using Concordat.Domain.Identity;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Environment = Concordat.Domain.Registry.Environment;

namespace Concordat.Domain.Tests;

/// <summary>The M7.1 environment and broker invariants.</summary>
public class EnvironmentTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private static Environment New(string name = "dev") =>
        Environment.Create(name, Now).Value;

    private static BrokerConnection Broker(
        string label = "local", string uri = "amqp://localhost:5672", string? vhost = null) =>
        BrokerConnection.Create(label, uri, vhost).Value;

    // -------------------------------------------------------------------- naming

    [Theory]
    [InlineData("dev")]
    [InlineData("prod")]
    [InlineData("eu-west")]
    [InlineData("staging2")]
    public void ValidNamesAreAccepted(string name) =>
        Assert.True(EnvironmentName.Create(name).IsSuccess);

    [Fact]
    public void NamesAreFoldedToLowercase() =>
        // The opposite of SubjectName, which preserves case. An environment name is typed into
        // a pipeline variable by a human, and PROD meaning something other than prod is a trap
        // with no upside.
        Assert.Equal("prod", EnvironmentName.Create("PROD").Value.Value);

    [Fact]
    public void SurroundingWhitespaceIsTrimmed() =>
        Assert.Equal("dev", EnvironmentName.Create("  dev  ").Value.Value);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("has space")]
    [InlineData("under_score")]
    [InlineData("dot.ted")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("double--hyphen")]
    [InlineData("slash/es")]
    public void InvalidNamesAreRefused(string? name)
    {
        var result = EnvironmentName.Create(name);

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.EnvironmentNameInvalid, result.Error!.Code);
    }

    [Fact]
    public void ANameTooLongIsRefused() =>
        Assert.True(EnvironmentName.Create(new string('a', EnvironmentName.MaxLength + 1)).IsFailure);

    [Fact]
    public void TheRefusalShowsAValidExample() =>
        // A grammar error that does not show the shape it wanted makes the reader guess.
        Assert.Contains("eu-west", EnvironmentName.Create("Not Valid!").Error!.Message, StringComparison.Ordinal);

    // ------------------------------------------------------------------ defaults

    [Fact]
    public void ANewEnvironmentInheritsTheDefaultPolicy() =>
        Assert.Equal(CompatibilityPolicy.Default, New().DefaultCompatibilityPolicy);

    [Theory]
    [InlineData("prod")]
    [InlineData("production")]
    [InlineData("live")]
    public void AnEnvironmentNamedLikeProductionDefaultsToCiOnly(string name) =>
        // The asymmetry that justifies guessing from a name: an over-permissive prod is
        // polluted silently and permanently, while an over-strict scratch environment produces
        // one clear error and a config change.
        Assert.Equal(RegistrationPolicy.CiOnly, New(name).RegistrationPolicy);

    [Theory]
    [InlineData("dev")]
    [InlineData("staging")]
    [InlineData("eu-west")]
    public void EverythingElseDefaultsToOpen(string name) =>
        Assert.Equal(RegistrationPolicy.Open, New(name).RegistrationPolicy);

    [Fact]
    public void AnExplicitRegistrationPolicyBeatsTheNameGuess() =>
        Assert.Equal(
            RegistrationPolicy.Open,
            Environment.Create("prod", Now, registrationPolicy: RegistrationPolicy.Open).Value
                .RegistrationPolicy);

    [Fact]
    public void AnExplicitIdIsHonoured()
    {
        // The migration path: environments that existed only as a hash of their name keep the
        // id every subject already references.
        var id = EnvironmentId.New();

        Assert.Equal(id, Environment.Create("dev", Now, id: id).Value.Id);
    }

    // ------------------------------------------------------------------- brokers

    [Fact]
    public void ABrokerCanBeAdded()
    {
        var environment = New();

        Assert.True(environment.AddBroker(Broker()).IsSuccess);
        Assert.Single(environment.Brokers);
    }

    [Fact]
    public void TheSameHostOnADifferentVirtualHostIsAllowed()
    {
        // DESIGN §4's own example registers eu-1 twice under different virtual hosts. A
        // virtual host is a separate topology, so the identity is the pair.
        var environment = New();
        environment.AddBroker(Broker("eu-1", "amqps://rabbit-eu:5671", "/orders"));

        var second = environment.AddBroker(Broker("eu-1-billing", "amqps://rabbit-eu:5671", "/billing"));

        Assert.True(second.IsSuccess, second.Error?.Message);
        Assert.Equal(2, environment.Brokers.Count);
    }

    [Fact]
    public void TheSameEndpointAndVirtualHostTwiceIsRefused()
    {
        var environment = New();
        environment.AddBroker(Broker("first", "amqps://rabbit-eu:5671", "/orders"));

        var duplicate = environment.AddBroker(Broker("second", "amqps://rabbit-eu:5671", "/orders"));

        Assert.True(duplicate.IsFailure);
        Assert.Equal(ConcordatCodes.BrokerAlreadyExists, duplicate.Error!.Code);
    }

    [Fact]
    public void ADuplicateDisplayNameIsRefused()
    {
        // Names are how an operator tells two brokers apart in a list; two called 'eu-1' makes
        // the list useless even though the endpoints differ.
        var environment = New();
        environment.AddBroker(Broker("eu-1", "amqps://rabbit-eu:5671"));

        var duplicate = environment.AddBroker(Broker("EU-1", "amqps://rabbit-us:5671"));

        Assert.True(duplicate.IsFailure);
        Assert.Equal(ConcordatCodes.BrokerAlreadyExists, duplicate.Error!.Code);
    }

    [Fact]
    public void ABrokerCanBeRemoved()
    {
        var environment = New();
        var broker = Broker();
        environment.AddBroker(broker);

        Assert.True(environment.RemoveBroker(broker.Id).IsSuccess);
        Assert.Empty(environment.Brokers);
    }

    [Fact]
    public void RemovingAnUnknownBrokerIsRefused() =>
        Assert.Equal(
            ConcordatCodes.BrokerNotFound,
            New().RemoveBroker(Guid.CreateVersion7()).Error!.Code);

    [Fact]
    public void TheBrokerListCannotBeMutatedThroughTheProperty()
    {
        // An IReadOnlyList backed by a List can be cast back to ICollection and added to,
        // which would bypass every invariant above.
        var environment = New();

        Assert.False(environment.Brokers is ICollection<BrokerConnection> { IsReadOnly: false });
    }

    // ----------------------------------------------------------------- broker URIs

    [Theory]
    [InlineData("amqp://localhost:5672")]
    [InlineData("amqps://rabbit-eu:5671")]
    public void AmqpSchemesAreAccepted(string uri) =>
        Assert.True(BrokerConnection.Create("b", uri).IsSuccess);

    [Theory]
    [InlineData("http://localhost")]
    [InlineData("mqtt://localhost")]
    [InlineData("amqp+ssl://localhost")]
    public void OtherSchemesAreRefused(string uri)
    {
        // ADR-013 scopes v1 to AMQP 0-9-1. Registering an endpoint nothing can use would be a
        // configuration that looks complete and silently never works.
        var result = BrokerConnection.Create("b", uri);

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.BrokerUriInvalid, result.Error!.Code);
    }

    [Theory]
    [InlineData("not a uri")]
    [InlineData("")]
    [InlineData(null)]
    public void AMalformedUriIsRefused(string? uri) =>
        Assert.True(BrokerConnection.Create("b", uri).IsFailure);

    [Fact]
    public void ABrokerWithoutADisplayNameIsRefused() =>
        Assert.True(BrokerConnection.Create("  ", "amqp://localhost").IsFailure);

    [Fact]
    public void TlsIsDerivedFromTheScheme()
    {
        // One source for the fact. 'amqps:// with TLS disabled' is a configuration nobody
        // means, so there is no separate flag to contradict the scheme.
        Assert.True(Broker("s", "amqps://host:5671").UseTls);
        Assert.False(Broker("p", "amqp://host:5672").UseTls);
    }

    [Fact]
    public void TheVirtualHostDefaultsToRoot() =>
        Assert.Equal("/", Broker().VirtualHost);

    // ------------------------------------------------------------- health checks

    [Fact]
    public void ABrokerStartsUnknownRatherThanAssumedHealthy() =>
        Assert.Equal(BrokerStatus.Unknown, Broker().Status);

    [Fact]
    public void ASuccessfulCheckClearsThePreviousError()
    {
        // A stale failure beside a healthy status is the kind of dashboard that teaches people
        // to ignore dashboards.
        var broker = Broker();
        broker.RecordCheck(reachable: false, Now, "connection refused");
        broker.RecordCheck(reachable: true, Now.AddMinutes(1));

        Assert.Equal(BrokerStatus.Reachable, broker.Status);
        Assert.Null(broker.LastError);
        Assert.Equal(Now.AddMinutes(1), broker.LastCheckedAt);
    }

    [Fact]
    public void AFailedCheckRecordsWhy()
    {
        var broker = Broker();
        broker.RecordCheck(reachable: false, Now, "connection refused");

        Assert.Equal(BrokerStatus.Unreachable, broker.Status);
        Assert.Equal("connection refused", broker.LastError);
    }

    // ----------------------------------------------------------------- mutation

    [Fact]
    public void ThePolicyDefaultsCanBeChanged()
    {
        var environment = New();
        var strict = new CompatibilityPolicy(
            CompatibilityMode.FullTransitive, CompatibilitySurface.Source);

        environment.SetDefaultCompatibilityPolicy(strict);
        environment.SetRegistrationPolicy(RegistrationPolicy.Closed);

        Assert.Equal(strict, environment.DefaultCompatibilityPolicy);
        Assert.Equal(RegistrationPolicy.Closed, environment.RegistrationPolicy);
    }

    [Fact]
    public void AnEmptyDescriptionIsStoredAsNull()
    {
        // Null and empty-string mean the same thing to a reader, and carrying both means every
        // consumer has to check for two.
        var environment = New();
        environment.Describe("   ");

        Assert.Null(environment.Description);
    }

    [Fact]
    public void AnOverlongDescriptionIsRefused() =>
        Assert.True(Environment.Create("dev", Now, description: new string('x', 513)).IsFailure);

    // ------------------------------------------------- the registration policy (M7.1)

    private static Environment With(RegistrationPolicy policy, string name = "dev") =>
        Environment.Create(name, Now, null, null, policy).Value;

    [Fact]
    public void OpenAdmitsAnybody() =>
        Assert.True(With(RegistrationPolicy.Open).MayRegister(ScopeSet.None).IsSuccess);

    [Fact]
    public void CiOnlyAdmitsACallerCarryingTheCiScope() =>
        Assert.True(With(RegistrationPolicy.CiOnly)
            .MayRegister(ScopeSet.Of([Scope.SubjectWrite, Scope.Ci])).IsSuccess);

    [Fact]
    public void CiOnlyRefusesAProducerHoldingOnlyWriteAccess()
    {
        // The case the policy exists for. A producer and a pipeline both arrive with an API key
        // and subject:write; nothing else in the system tells them apart.
        var refused = With(RegistrationPolicy.CiOnly).MayRegister(ScopeSet.Of([Scope.SubjectWrite]));

        Assert.Equal(ConcordatCodes.RegistrationPolicyForbids, refused.Error!.Code);
    }

    [Fact]
    public void CiOnlyRefusesAnOrganisationAdministrator()
    {
        // org:admin does not imply ci, deliberately. Being an administrator does not make you a
        // build pipeline, and the opposite would let the most privileged human in the
        // organisation walk straight through the control that keeps production clean.
        var refused = With(RegistrationPolicy.CiOnly)
            .MayRegister(ScopeSet.Of([Scope.OrgAdmin, Scope.SubjectAdmin]));

        Assert.Equal(ConcordatCodes.RegistrationPolicyForbids, refused.Error!.Code);
    }

    [Fact]
    public void ClosedRefusesEvenCi()
    {
        // Closed means promotion only. A CI pipeline is still direct registration.
        var refused = With(RegistrationPolicy.Closed)
            .MayRegister(ScopeSet.Of([Scope.SubjectWrite, Scope.Ci]));

        Assert.Equal(ConcordatCodes.RegistrationPolicyForbids, refused.Error!.Code);
        Assert.Contains("promotion", refused.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusalNamesTheEnvironmentAndWhatIsMissing()
    {
        // Whoever hits this is usually a pipeline author who does not know the environment has a
        // policy at all. "Forbidden" alone sends them to audit their key's scopes, which are fine.
        var refused = With(RegistrationPolicy.CiOnly, "prod").MayRegister(ScopeSet.None);

        Assert.Contains("'prod'", refused.Error!.Message, StringComparison.Ordinal);
        Assert.Contains("'ci'", refused.Error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("prod")]
    [InlineData("production")]
    [InlineData("live")]
    public void AProductionNameDefaultsToCiOnlyAndThatIsNowEnforced(string name)
    {
        // The default existed and did nothing: no handler read RegistrationPolicy. It refuses now.
        var refused = New(name).MayRegister(ScopeSet.Of([Scope.SubjectWrite]));

        Assert.Equal(ConcordatCodes.RegistrationPolicyForbids, refused.Error!.Code);
    }

    [Fact]
    public void AnOrdinaryNameDefaultsToOpen() =>
        Assert.True(New("staging").MayRegister(ScopeSet.None).IsSuccess);
}
