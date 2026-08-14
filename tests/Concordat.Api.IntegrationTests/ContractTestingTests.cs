using System.Net;
using System.Net.Http.Json;
using Concordat.Application.Registry;
using Concordat.Contracts;
using Concordat.Contracts.Testing;

namespace Concordat.Api.IntegrationTests;

/// <summary>An order, as this test suite's "application" defines it.</summary>
/// <param name="OrderId">The order.</param>
/// <param name="Total">What it came to.</param>
/// <remarks>
/// Marked so the generator emits <c>[assembly: ConcordatGeneratedSchema(...)]</c> for it. That
/// attribute is the only schema <c>ConcordatAssert</c> will ever use — deriving one from the
/// runtime type would be a second implementation of the C#-to-JSON-Schema mapping, and the two
/// would drift.
/// </remarks>
[ConcordatContract("acme.testing.OrderCreated")]
public sealed record TestOrderCreated(string OrderId, decimal Total);

/// <summary>
/// `Concordat.Contracts.Testing` against the real registry (decision 13).
/// </summary>
/// <remarks>
/// <b>Driven through the package rather than through a hand-written request</b>, for the reason
/// the SDK's contract tests are: the failure most likely here and least likely to be caught by
/// either side alone is the two disagreeing about the compatibility endpoint's response shape.
/// A fake handler would have agreed with whatever the package expected.
/// </remarks>
[Collection(ApiCollection.Name)]
public class ContractTestingTests(ApiFactory factory)
{
    private const string Subject = "acme.testing.OrderCreated";

    private static string UniqueEnvironment() => $"env-{Guid.CreateVersion7():N}"[..24];

    private ConcordatTestOptions Options(string environment) => new()
    {
        BaseAddress = new Uri("http://localhost"),
        Environment = environment,

        // The in-process server's handler. This is what the option exists for beyond tests:
        // a corporate proxy or a self-signed registry certificate needs the same seam.
        Handler = factory.Server.CreateHandler(),
    };

    private async Task<string> NewEnvironmentAsync()
    {
        var http = factory.CreateClient();
        var environment = UniqueEnvironment();

        var created = await http.PostAsJsonAsync(
            "/v1/environments", new CreateEnvironmentRequest(environment), ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return environment;
    }

    /// <summary>Registers the generated schema, so the subject exists to compare against.</summary>
    private async Task RegisterGeneratedAsync(string environment)
    {
        var http = factory.CreateClient();

        var generated = typeof(TestOrderCreated).Assembly
            .GetCustomAttributes(typeof(ConcordatGeneratedSchemaAttribute), false)
            .Cast<ConcordatGeneratedSchemaAttribute>()
            .Single(a => a.Subject == Subject);

        var subject = await http.PostAsJsonAsync(
            $"/v1/environments/{environment}/subjects",
            new CreateSubjectRequest(Subject, "json", "alice"),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, subject.StatusCode);

        var version = await http.PostAsJsonAsync(
            $"/v1/environments/{environment}/subjects/{Subject}/versions",
            new RegisterVersionRequest(generated.Schema, RegisteredBy: "alice"),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, version.StatusCode);
    }

    [Fact]
    public void TheGeneratorEmittedASchemaForTheMarkedType()
    {
        // If this fails, every other test here would pass by asserting on nothing: the package
        // would throw "no generated schema" and the suite would be measuring its own wiring.
        var generated = typeof(TestOrderCreated).Assembly
            .GetCustomAttributes(typeof(ConcordatGeneratedSchemaAttribute), false)
            .Cast<ConcordatGeneratedSchemaAttribute>()
            .SingleOrDefault(a => a.Subject == Subject);

        Assert.NotNull(generated);
        Assert.Contains("orderId", generated.Schema, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnchangedContractIsCompatibleWithWhatTheRegistryHolds()
    {
        var environment = await NewEnvironmentAsync();
        await RegisterGeneratedAsync(environment);

        var verdict = await ConcordatAssert.CompatibleAsync<TestOrderCreated>(Options(environment));

        Assert.True(verdict.Compatible);
        Assert.Equal(Subject, verdict.Subject);
        Assert.NotNull(verdict.SchemaId);
    }

    [Fact]
    public async Task ASubjectTheRegistryHasNeverSeenIsCompatibleByDefault()
    {
        // A team adding a new contract type has not broken anything. A red test between writing
        // the type and CI first pushing it teaches them that this check cries wolf.
        var environment = await NewEnvironmentAsync();

        var verdict = await ConcordatAssert.CompatibleAsync<TestOrderCreated>(Options(environment));

        Assert.True(verdict.Compatible);
        Assert.Null(verdict.SchemaId);
    }

    [Fact]
    public async Task AnUnknownSubjectFailsWhenTheSuiteSaysEveryContractMustBeRegistered()
    {
        var environment = await NewEnvironmentAsync();
        var options = Options(environment);
        options.TreatUnknownSubjectAsCompatible = false;

        var failure = await Assert.ThrowsAsync<ConcordatContractException>(
            () => ConcordatAssert.CompatibleAsync<TestOrderCreated>(options));

        Assert.Contains(Subject, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABreakingChangeInTheRegistryFailsTheAssertion()
    {
        // THE CASE THE PACKAGE EXISTS FOR, and the one the build-time check cannot see.
        //
        // The type is unchanged and its file is unchanged, so `concordat check` and the M3.4
        // analyser are both green. What moved is the registry: somebody registered a version
        // this type can no longer satisfy. Only a call to a live registry catches that.
        var environment = await NewEnvironmentAsync();
        var http = factory.CreateClient();

        var subject = await http.PostAsJsonAsync(
            $"/v1/environments/{environment}/subjects",
            new CreateSubjectRequest(Subject, "json", "alice"),
            ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Created, subject.StatusCode);

        // A version demanding a field the C# type does not have. Registering the generated
        // schema after this would be dropping a required property, which is backward-breaking.
        const string Stricter = """
            {"type":"object",
             "properties":{"orderId":{"type":"string"},"customerId":{"type":"string"}},
             "required":["orderId","customerId"]}
            """;

        var version = await http.PostAsJsonAsync(
            $"/v1/environments/{environment}/subjects/{Subject}/versions",
            new RegisterVersionRequest(Stricter, RegisteredBy: "alice"),
            ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Created, version.StatusCode);

        var failure = await Assert.ThrowsAsync<ConcordatContractException>(
            () => ConcordatAssert.CompatibleAsync<TestOrderCreated>(Options(environment)));

        Assert.Contains("no longer compatible", failure.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(TestOrderCreated), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnreachableRegistryIsNotReportedAsAnIncompatibility()
    {
        // A test that fails identically whether the schema broke or the registry was down
        // trains a team to rerun it rather than read it.
        var options = new ConcordatTestOptions
        {
            BaseAddress = new Uri("http://127.0.0.1:1"),
            Environment = "prod",
            Timeout = TimeSpan.FromSeconds(2),
        };

        var failure = await Assert.ThrowsAsync<ConcordatContractException>(
            () => ConcordatAssert.CompatibleAsync<TestOrderCreated>(options));

        Assert.Contains("not a compatibility failure", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheWholeAssemblyCanBeSweptAtOnce()
    {
        var environment = await NewEnvironmentAsync();
        await RegisterGeneratedAsync(environment);

        var verdicts = await ConcordatAssert.AllCompatibleAsync(
            typeof(TestOrderCreated).Assembly, Options(environment));

        Assert.Contains(verdicts, v => v.Subject == Subject && v.Compatible);
    }

    [Fact]
    public async Task AMissingRegistryAddressSaysWhichVariableToSet()
    {
        var options = new ConcordatTestOptions { BaseAddress = null, Environment = "prod" };

        var failure = await Assert.ThrowsAsync<ConcordatContractException>(
            () => ConcordatAssert.CompatibleAsync<TestOrderCreated>(options));

        Assert.Contains("CONCORDAT_REGISTRY", failure.Message, StringComparison.Ordinal);
    }
}
