using System.Diagnostics;

namespace Concordat.Cli.Tests;

/// <summary>
/// The exit codes, exercised through the real executable.
/// </summary>
/// <remarks>
/// <para>
/// The one place the process boundary is worth crossing. Everything asserted here happens in
/// argument parsing and top-level plumbing, which an in-process call skips entirely — and
/// <see cref="ExitCodes.UsageError"/> in particular exists because the parser's own default
/// for a bad argument is <c>1</c>, which is <see cref="ExitCodes.ContractViolation"/>.
/// </para>
/// <para>
/// If that collision ever came back, every in-process test here would still pass while CI
/// reported schema violations that never happened.
/// </para>
/// </remarks>
public class ExitCodeTests
{
    private static string Executable
    {
        get
        {
            // Beside the test assembly, because Concordat.Cli is a project reference.
            var path = Path.Combine(
                AppContext.BaseDirectory,
                OperatingSystem.IsWindows() ? "concordat.exe" : "concordat");

            Assert.True(File.Exists(path), $"the CLI was not found at {path}.");
            return path;
        }
    }

    private static (int ExitCode, string Stdout, string Stderr) Run(params string[] args)
    {
        using var process = Process.Start(new ProcessStartInfo(Executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Arguments = string.Join(' ', args.Select(a => a.Contains(' ', StringComparison.Ordinal) ? $"\"{a}\"" : a)),
        })!;

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60_000), "the CLI did not exit.");

        return (process.ExitCode, stdout, stderr);
    }

    [Fact]
    public void AMistypedFlagIsAUsageErrorNotAContractViolation()
    {
        // The collision this whole exit-code scheme exists to prevent. System.CommandLine
        // returns 1 for a parse error; 1 means "your schema broke". A pipeline could not tell
        // a typo from a genuine violation, and the way that gets resolved in practice is
        // someone appending `|| true` and switching the gate off for good.
        var (code, _, stderr) = Run("lint", "--jsno");

        Assert.Equal(ExitCodes.UsageError, code);
        Assert.Contains("--jsno", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownCommandIsAUsageError()
    {
        Assert.Equal(ExitCodes.UsageError, Run("nosuchcommand").ExitCode);
    }

    [Fact]
    public void AnApiKeyOverANonLoopbackRegistryIsAUsageErrorRatherThanBeingSent()
    {
        // The credential rides on every request regardless of scheme, so this has to be
        // caught before the first request goes out, not discovered by inspecting traffic.
        var (code, _, stderr) = Run(
            "check", "--registry", "http://registry.example.com", "--api-key", "cdt_test_secret");

        Assert.Equal(ExitCodes.UsageError, code);
        Assert.Contains("http://", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void HelpSucceedsAndDocumentsTheExitCodes()
    {
        var (code, stdout, _) = Run("--help");

        Assert.Equal(ExitCodes.Success, code);

        // The codes are the contract with CI, so they belong in --help rather than only in a
        // documentation page nobody reads at 2am.
        Assert.Contains("Exit codes", stdout, StringComparison.Ordinal);
        Assert.Contains("1 contract violation", stdout, StringComparison.Ordinal);
        Assert.Contains("3 registry unavailable", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnreachableRegistryIsNotAContractViolation()
    {
        // Port 9 is the discard protocol: reliably present, reliably refusing.
        var directory = Path.Combine(Path.GetTempPath(), $"concordat-unreachable-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "acme.Order.json"), """{"type":"object"}""");

        try
        {
            var (code, _, stderr) = Run("check", "--dir", directory, "--registry", "http://127.0.0.1:9");

            Assert.Equal(ExitCodes.RegistryUnavailable, code);
            Assert.Contains("Could not reach the registry", stderr, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void GlobalOptionsReachSubcommands()
    {
        // --json is declared on the root. Without Recursive it is a parse error on every
        // subcommand, which is both surprising and, before the fix above, exit code 1.
        var directory = Path.Combine(Path.GetTempPath(), $"concordat-global-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "acme.Order.json"), """{"type":"object","properties":{}}""");

        try
        {
            var (code, stdout, _) = Run("lint", "--dir", directory, "--json");

            Assert.Equal(ExitCodes.Success, code);

            using var document = System.Text.Json.JsonDocument.Parse(stdout);
            Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LintNeedsNoRegistryAtAll()
    {
        // The check a pre-commit hook can afford, and the one that still works when the
        // registry is down. Pointed at a dead port to prove it never calls out.
        var directory = Path.Combine(Path.GetTempPath(), $"concordat-offline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "acme.Order.json"), """{"type":"object","properties":{}}""");

        try
        {
            var (code, stdout, _) = Run("lint", "--dir", directory, "--registry", "http://127.0.0.1:9");

            Assert.Equal(ExitCodes.Success, code);
            Assert.Contains("well-formed", stdout, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
