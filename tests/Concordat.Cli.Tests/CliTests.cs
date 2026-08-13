using Concordat.Api.IntegrationTests;
using Concordat.Cli.Commands;

namespace Concordat.Cli.Tests;

/// <summary>Shares one API host and database across the CLI tests.</summary>
/// <remarks>
/// The <see cref="ApiFactory"/> type is reused from the API integration tests, but the
/// collection definition has to be declared here: xunit resolves collection definitions
/// per-assembly and will not see one in a referenced project.
/// </remarks>
[CollectionDefinition(Name)]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "'Collection' is xunit's own term for a shared-fixture group.")]
public sealed class CliApiCollection : ICollectionFixture<ApiFactory>
{
    /// <summary>The collection name.</summary>
    public const string Name = "cli-api";
}

/// <summary>Captures what a command wrote, so assertions can be made about the output itself.</summary>
internal sealed class Capture : IDisposable
{
    private readonly StringWriter _stdout = new();
    private readonly StringWriter _stderr = new();

    public Output Output(bool json = false) => new(json, _stdout, _stderr);

    public string Stdout => _stdout.ToString();

    public string Stderr => _stderr.ToString();

    public void Dispose()
    {
        _stdout.Dispose();
        _stderr.Dispose();
    }
}

/// <summary>
/// The CLI against the real API, over a real database.
/// </summary>
/// <remarks>
/// The commands are exercised as functions rather than by launching the executable. The
/// process boundary is worth testing once — argument parsing and exit-code mapping, covered by
/// <see cref="ExitCodeTests"/> — but re-crossing it for every scenario would buy nothing and
/// cost a process launch each time.
/// </remarks>
[Collection(CliApiCollection.Name)]
public class CliTests(ApiFactory api) : IDisposable
{
    private const string Compatible = """
        {"type":"object","properties":{"id":{"type":"integer"},"note":{"type":"string"}},"required":["id"]}
        """;

    private const string Original = """
        {"type":"object","properties":{"id":{"type":"integer"}},"required":["id"]}
        """;

    private const string Breaking = """
        {"type":"object","properties":{"id":{"type":"integer"},"email":{"type":"string"}},"required":["id","email"]}
        """;

    private readonly List<string> _directories = [];

    private RegistryApi Api(string environment = "cli-test") =>
        new(api.CreateClient(), environment);

    private string NewDirectory(params (string Subject, string Body)[] contracts)
    {
        var path = Path.Combine(Path.GetTempPath(), $"concordat-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _directories.Add(path);

        foreach (var (subject, body) in contracts)
        {
            File.WriteAllText(Path.Combine(path, $"{subject}.json"), body);
        }

        return path;
    }

    public void Dispose()
    {
        foreach (var directory in _directories.Where(Directory.Exists))
        {
            Directory.Delete(directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ANewSubjectPassesTheGate()
    {
        // Adding a contract must not fail the build that introduces it. A first version cannot
        // break anything, so there is nothing to gate on.
        using var capture = new Capture();
        var directory = NewDirectory(($"cli.New{Guid.NewGuid():N}", Original));

        var code = await CheckCommand.RunAsync(Api(), capture.Output(), directory, default);

        Assert.Equal(ExitCodes.Success, code);
        Assert.Contains("new subject", capture.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABreakingChangeFailsTheGateAndNamesThePath()
    {
        // The whole product, end to end. DESIGN §7 singles out the JSON Pointer as where
        // Confluent is weakest: being told a change is incompatible without being told where
        // turns a fixable build failure into a guessing game.
        var subject = $"cli.Breaking{Guid.NewGuid():N}";
        var environment = $"cli-break-{Guid.NewGuid():N}";

        using var pushCapture = new Capture();
        var initial = NewDirectory((subject, Original));
        var pushed = await PushCommand.RunAsync(
            Api(environment), pushCapture.Output(), initial, "team", "test", dryRun: false, default);
        Assert.Equal(ExitCodes.Success, pushed);

        using var capture = new Capture();
        var changed = NewDirectory((subject, Breaking));

        var code = await CheckCommand.RunAsync(Api(environment), capture.Output(), changed, default);

        Assert.Equal(ExitCodes.ContractViolation, code);
        Assert.Contains($"✗ {subject}", capture.Stdout, StringComparison.Ordinal);

        // The path, not just the verdict. It points at /required rather than at the added
        // member, because the change is to the required array — the member name is carried in
        // the message. Asserting both is what keeps that pairing honest: a path with no name,
        // or a name with no path, would each leave the developer guessing.
        Assert.Contains("/required", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("required_field_added", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("'email' is now required", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("would break their policy", capture.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACompatibleChangePassesTheGate()
    {
        // The other half, and the one Confluent's JSON Schema rules get wrong: adding an
        // optional property must be fully compatible or the product is unusable (DESIGN §7).
        var subject = $"cli.Additive{Guid.NewGuid():N}";
        var environment = $"cli-additive-{Guid.NewGuid():N}";

        using var pushCapture = new Capture();
        await PushCommand.RunAsync(
            Api(environment), pushCapture.Output(), NewDirectory((subject, Original)),
            "team", "test", dryRun: false, default);

        using var capture = new Capture();
        var code = await CheckCommand.RunAsync(
            Api(environment), capture.Output(), NewDirectory((subject, Compatible)), default);

        Assert.Equal(ExitCodes.Success, code);
        Assert.Contains("all compatible", capture.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PushIsIdempotentAtTheTip()
    {
        var subject = $"cli.Idempotent{Guid.NewGuid():N}";
        var environment = $"cli-idem-{Guid.NewGuid():N}";
        var directory = NewDirectory((subject, Original));

        using var first = new Capture();
        await PushCommand.RunAsync(
            Api(environment), first.Output(), directory, "team", "test", dryRun: false, default);
        Assert.Contains("registered", first.Stdout, StringComparison.Ordinal);

        using var second = new Capture();
        var code = await PushCommand.RunAsync(
            Api(environment), second.Output(), directory, "team", "test", dryRun: false, default);

        Assert.Equal(ExitCodes.Success, code);
        Assert.Contains("unchanged", second.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PushRecordsABreakingChangeAsAwaitingApproval()
    {
        // push records, check gates. Making push also fail on breakage would mean a
        // deliberately-approved breaking change could never be recorded at all (ADR-017).
        var subject = $"cli.Await{Guid.NewGuid():N}";
        var environment = $"cli-await-{Guid.NewGuid():N}";

        using var first = new Capture();
        await PushCommand.RunAsync(
            Api(environment), first.Output(), NewDirectory((subject, Original)),
            "team", "test", dryRun: false, default);

        using var second = new Capture();
        var code = await PushCommand.RunAsync(
            Api(environment), second.Output(), NewDirectory((subject, Breaking)),
            "team", "test", dryRun: false, default);

        Assert.Equal(ExitCodes.Success, code);
        Assert.Contains("awaiting-approval", second.Stdout, StringComparison.Ordinal);
        Assert.Contains("awaiting approval", second.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DryRunChangesNothing()
    {
        var subject = $"cli.Dry{Guid.NewGuid():N}";
        var environment = $"cli-dry-{Guid.NewGuid():N}";
        var directory = NewDirectory((subject, Original));

        using var capture = new Capture();
        await PushCommand.RunAsync(
            Api(environment), capture.Output(), directory, "team", "test", dryRun: true, default);

        Assert.Contains("would-create", capture.Stdout, StringComparison.Ordinal);

        // Still absent afterwards.
        Assert.Null(await Api(environment).GetVersionAsync(subject, "latest", default));
    }

    [Fact]
    public async Task ExportThenCheckIsACleanRoundTrip()
    {
        // What makes export usable for adopting an existing registry into source control: the
        // files it writes are the files check reads, and re-checking them reports no change.
        var subject = $"cli.Round{Guid.NewGuid():N}";
        var environment = $"cli-round-{Guid.NewGuid():N}";

        using var pushCapture = new Capture();
        await PushCommand.RunAsync(
            Api(environment), pushCapture.Output(), NewDirectory((subject, Original)),
            "team", "test", dryRun: false, default);

        var exported = Path.Combine(Path.GetTempPath(), $"concordat-export-{Guid.NewGuid():N}");
        _directories.Add(exported);

        using var exportCapture = new Capture();
        var exportCode = await InspectCommands.ExportAsync(
            Api(environment), exportCapture.Output(), exported, default);

        Assert.Equal(ExitCodes.Success, exportCode);
        Assert.True(File.Exists(Path.Combine(exported, $"{subject}.json")));

        using var checkCapture = new Capture();
        var checkCode = await CheckCommand.RunAsync(
            Api(environment), checkCapture.Output(), exported, default);

        Assert.Equal(ExitCodes.Success, checkCode);
    }

    [Fact]
    public async Task PromotionPreservesTheSchemaId()
    {
        // The property that makes promotion safe (ADR-015): a message published in staging and
        // still in flight stays valid after its subject is promoted to prod, because the id it
        // was pinned to is the same id there.
        var subject = $"cli.Promote{Guid.NewGuid():N}";
        var staging = $"cli-staging-{Guid.NewGuid():N}";
        var production = $"cli-prod-{Guid.NewGuid():N}";

        using var pushCapture = new Capture();
        await PushCommand.RunAsync(
            Api(staging), pushCapture.Output(), NewDirectory((subject, Original)),
            "team", "test", dryRun: false, default);

        using var capture = new Capture();
        var code = await PushCommand.PromoteAsync(
            Api(staging), Api(production), capture.Output(), subject, "latest", "team", "test", default);

        Assert.Equal(ExitCodes.Success, code);

        var source = await Api(staging).GetVersionAsync(subject, "latest", default);
        var target = await Api(production).GetVersionAsync(subject, "latest", default);

        Assert.NotNull(source);
        Assert.NotNull(target);
        Assert.Equal(source.SchemaId, target.SchemaId);
    }

    [Fact]
    public async Task PromotingAMissingSubjectIsAViolationNotAnOutage()
    {
        using var capture = new Capture();

        var code = await PushCommand.PromoteAsync(
            Api($"cli-a-{Guid.NewGuid():N}"),
            Api($"cli-b-{Guid.NewGuid():N}"),
            capture.Output(),
            $"cli.Missing{Guid.NewGuid():N}",
            "latest",
            "team",
            "test",
            default);

        Assert.Equal(ExitCodes.ContractViolation, code);
        Assert.DoesNotContain("registry", capture.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DiffNamesTheDivergenceAndItsPath()
    {
        var subject = $"cli.Diff{Guid.NewGuid():N}";
        var environment = $"cli-diff-{Guid.NewGuid():N}";

        using var firstPush = new Capture();
        await PushCommand.RunAsync(
            Api(environment), firstPush.Output(), NewDirectory((subject, Original)),
            "team", "test", dryRun: false, default);

        using var secondPush = new Capture();
        await PushCommand.RunAsync(
            Api(environment), secondPush.Output(), NewDirectory((subject, Breaking)),
            "team", "test", dryRun: false, default);

        using var capture = new Capture();
        var code = await InspectCommands.DiffAsync(Api(environment), capture.Output(), subject, 1, 2, default);

        Assert.Equal(ExitCodes.Success, code);
        Assert.Contains("/required", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("required_field_added", capture.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiffIsBlindToAnAddedPropertyUnderAnOpenContentModel()
    {
        // Pins a real limitation rather than hiding it. The compatibility engine records a
        // divergence only where one could affect compatibility, and under an open content
        // model — the default — adding a property cannot. So `diff` reports the ids changed
        // and nothing else, for the single most common schema change there is.
        //
        // The output says so explicitly. Calling it "a formatting change", which is what the
        // first version of this command did, would have been plainly wrong.
        var subject = $"cli.Blind{Guid.NewGuid():N}";
        var environment = $"cli-blind-{Guid.NewGuid():N}";

        using var firstPush = new Capture();
        await PushCommand.RunAsync(
            Api(environment), firstPush.Output(), NewDirectory((subject, Original)),
            "team", "test", dryRun: false, default);

        using var secondPush = new Capture();
        await PushCommand.RunAsync(
            Api(environment), secondPush.Output(), NewDirectory((subject, Compatible)),
            "team", "test", dryRun: false, default);

        using var capture = new Capture();
        await InspectCommands.DiffAsync(Api(environment), capture.Output(), subject, 1, 2, default);

        Assert.DoesNotContain("/properties/note", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("open content model", capture.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JsonModeEmitsExactlyOneDocumentAndNoProse()
    {
        // `concordat check --json | jq` has to work without the caller stripping a progress
        // line off the front.
        using var capture = new Capture();
        var directory = NewDirectory(($"cli.Json{Guid.NewGuid():N}", Original));

        await CheckCommand.RunAsync(Api(), capture.Output(json: true), directory, default);

        using var document = System.Text.Json.JsonDocument.Parse(capture.Stdout);
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(1, document.RootElement.GetProperty("checkedCount").GetInt32());
    }

    [Fact]
    public async Task AnEmptyContractsDirectoryFailsRatherThanPassingVacuously()
    {
        // A gate that silently passes because it found nothing is worse than no gate: the
        // pipeline is green and nothing was verified.
        using var capture = new Capture();
        var empty = NewDirectory();

        var code = await CheckCommand.RunAsync(Api(), capture.Output(), empty, default);

        Assert.Equal(ExitCodes.LocalFileError, code);
        Assert.Contains("Nothing was checked", capture.Stderr, StringComparison.Ordinal);
    }
}
