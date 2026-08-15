using System.CommandLine;
using Concordat.Cli;
using Concordat.Cli.Commands;
using Concordat.Formats.Json;

// Must be the first statement: .NET reads the process-wide regex match timeout exactly once,
// the first time anything touches Regex, so this is the one place that can reliably win that
// race. See RegexSafety's remarks for why nothing downstream depends on it having won -- the
// CLI's own `check`/`infer` commands run JSON Schema validation locally, over files an
// operator points it at, which is the same untrusted-pattern-and-payload shape as the server.
RegexSafety.ApplyProcessWideDefault();

// Global options. Every one of them also reads an environment variable, because a CI job
// should configure the registry once for the whole pipeline rather than repeating
// --registry on nine steps and getting one of them wrong.
// Recursive, so they work on every subcommand. Without it `concordat lint --json` is a parse
// error, which is both surprising and — see below — indistinguishable from a broken contract.
var registryOption = new Option<string>("--registry")
{
    Description = "Registry base address. Defaults to $CONCORDAT_REGISTRY.",
    DefaultValueFactory = _ =>
        Environment.GetEnvironmentVariable("CONCORDAT_REGISTRY") ?? "http://localhost:8080",
    Recursive = true,
};

var environmentOption = new Option<string>("--env", "-e")
{
    Description = "Environment name. Defaults to $CONCORDAT_ENV.",
    DefaultValueFactory = _ => Environment.GetEnvironmentVariable("CONCORDAT_ENV") ?? "default",
    Recursive = true,
};

var apiKeyOption = new Option<string?>("--api-key")
{
    Description = "API key. Prefer $CONCORDAT_API_KEY — an argument is visible in the process list.",
    DefaultValueFactory = _ => Environment.GetEnvironmentVariable("CONCORDAT_API_KEY"),
    Recursive = true,
};

var jsonOption = new Option<bool>("--json")
{
    Description = "Emit one JSON document on stdout and nothing else.",
    Recursive = true,
};

var directoryOption = new Option<string>("--dir", "-d")
{
    Description = "The contracts directory.",
    DefaultValueFactory = _ => ContractDirectory.DefaultPath,
};

var byOption = new Option<string>("--by")
{
    Description = "Who is making this change, for the audit trail.",
    DefaultValueFactory = _ =>
        Environment.GetEnvironmentVariable("CONCORDAT_ACTOR")
        ?? Environment.GetEnvironmentVariable("GITHUB_ACTOR")
        ?? "cli",
};

var ownerOption = new Option<string>("--owner")
{
    Description = "Owner recorded when a subject has to be created.",
    DefaultValueFactory = _ => Environment.GetEnvironmentVariable("CONCORDAT_OWNER") ?? "unknown",
};

var root = new RootCommand(
    "Concordat — schema registry and contract enforcement for RabbitMQ.\n\n" +
    "Exit codes: 0 success · 1 contract violation · 2 usage · 3 registry unavailable · " +
    "4 local file error · 70 internal.");

root.Add(registryOption);
root.Add(environmentOption);
root.Add(apiKeyOption);
root.Add(jsonOption);

// ---- check ----
var check = new Command("check", "Dry-run every contract against the registry. Exits 1 on breakage.");
check.Add(directoryOption);
check.SetAction((parse, token) => Run(parse, (api, output) =>
    CheckCommand.RunAsync(api, output, parse.GetValue(directoryOption)!, token)));

// ---- lint ----
var lint = new Command("lint", "Check the contracts directory itself. Needs no registry.");
lint.Add(directoryOption);
lint.SetAction((parse, _) =>
{
    var output = NewOutput(parse);
    return Task.FromResult(CheckCommand.Lint(output, parse.GetValue(directoryOption)!));
});

// ---- push ----
var dryRunOption = new Option<bool>("--dry-run")
{
    Description = "Report what would be registered, and change nothing.",
};

var push = new Command("push", "Register every contract in the directory.");
push.Add(directoryOption);
push.Add(ownerOption);
push.Add(byOption);
push.Add(dryRunOption);
push.SetAction((parse, token) => Run(parse, (api, output) =>
    PushCommand.RunAsync(
        api,
        output,
        parse.GetValue(directoryOption)!,
        parse.GetValue(ownerOption)!,
        parse.GetValue(byOption)!,
        parse.GetValue(dryRunOption),
        token)));

// ---- promote ----
var subjectArgument = new Argument<string>("subject") { Description = "The subject to promote." };
var fromEnvOption = new Option<string>("--from") { Description = "Source environment.", Required = true };
var toEnvOption = new Option<string>("--to") { Description = "Target environment.", Required = true };
var ordinalOption = new Option<string>("--version")
{
    Description = "Ordinal to promote, or 'latest'.",
    DefaultValueFactory = _ => "latest",
};

var promote = new Command("promote", "Copy one subject's version from one environment to another.");
promote.Add(subjectArgument);
promote.Add(fromEnvOption);
promote.Add(toEnvOption);
promote.Add(ordinalOption);
promote.Add(ownerOption);
promote.Add(byOption);
promote.SetAction(async (parse, token) =>
{
    var output = NewOutput(parse);

    try
    {
        using var sourceHttp = NewHttpClient(parse);

        return await PushCommand.PromoteAsync(
            new RegistryApi(sourceHttp, parse.GetValue(fromEnvOption)!),
            parse.GetValue(toEnvOption)!,
            output,
            parse.GetValue(subjectArgument)!,
            parse.GetValue(ordinalOption)!,
            parse.GetValue(byOption)!,
            token).ConfigureAwait(false);
    }
    catch (RegistryException ex)
    {
        return output.Fail(ex.ExitCode, ex.Code, ex.Message);
    }
});

// ---- diff ----
var fromOrdinal = new Argument<int>("from") { Description = "The earlier ordinal." };
var toOrdinal = new Argument<int>("to") { Description = "The later ordinal." };

var diff = new Command("diff", "Show what changed between two versions of a subject.");
diff.Add(subjectArgument);
diff.Add(fromOrdinal);
diff.Add(toOrdinal);
diff.SetAction((parse, token) => Run(parse, (api, output) =>
    InspectCommands.DiffAsync(
        api, output, parse.GetValue(subjectArgument)!,
        parse.GetValue(fromOrdinal), parse.GetValue(toOrdinal), token)));

// ---- impact ----
var impactFileOption = new Option<string?>("--file")
{
    Description = "A candidate schema to analyse before registering it.",
};

var impactVersionOption = new Option<int?>("--version")
{
    Description = "A registered ordinal to analyse. Defaults to the subject's latest.",
};

var warnOnlyOption = new Option<bool>("--warn-only")
{
    Description = "Report breakage without failing. For brownfield estates being worked through.",
};

var impact = new Command("impact", "Show which registered consumers a change would break.");
impact.Add(subjectArgument);
impact.Add(impactFileOption);
impact.Add(impactVersionOption);
impact.Add(warnOnlyOption);
impact.SetAction((parse, token) => Run(parse, (api, output) =>
    ImpactCommand.RunAsync(
        api,
        output,
        parse.GetValue(subjectArgument)!,
        parse.GetValue(impactFileOption),
        parse.GetValue(impactVersionOption),
        parse.GetValue(warnOnlyOption),
        token)));

// ---- export ----
var export = new Command("export", "Write an environment's contracts to disk.");
export.Add(directoryOption);
export.SetAction((parse, token) => Run(parse, (api, output) =>
    InspectCommands.ExportAsync(api, output, parse.GetValue(directoryOption)!, token)));

// ---- infer ----
var samplesOption = new Option<string?>("--samples")
{
    Description = "A directory of sample .json payloads. File mode is the default and is safe.",
};

var queueOption = new Option<string?>("--queue")
{
    Description = "Sample a live queue instead. Reads and requeues; see --i-understand-this-reorders-the-queue.",
};

var brokerOption = new Option<string>("--broker")
{
    Description = "AMQP URI for queue mode. Defaults to $CONCORDAT_BROKER.",
    DefaultValueFactory = _ =>
        Environment.GetEnvironmentVariable("CONCORDAT_BROKER") ?? "amqp://guest:guest@localhost:5672/",
};

var maxOption = new Option<int>("--max")
{
    Description = "How many messages to inspect in queue mode.",
    DefaultValueFactory = _ => 500,
};

var subjectOption = new Option<string?>("--subject")
{
    Description = "The subject to draft for. Required in file mode.",
};

var outOption = new Option<string>("--out", "-o")
{
    Description = "Where to write the draft.",
    DefaultValueFactory = _ => ContractDirectory.DefaultPath,
};

var acknowledgeOption = new Option<bool>("--i-understand-this-reorders-the-queue")
{
    Description =
        "Required for queue mode. Requeued messages return to the head of the queue, not their " +
        "original position.",
};

var infer = new Command("infer", "Draft a schema from real messages. Never registers anything.");
infer.Add(samplesOption);
infer.Add(queueOption);
infer.Add(brokerOption);
infer.Add(maxOption);
infer.Add(subjectOption);
infer.Add(outOption);
infer.Add(acknowledgeOption);
infer.SetAction(async (parse, token) =>
{
    var output = NewOutput(parse);
    var samples = parse.GetValue(samplesOption);
    var queue = parse.GetValue(queueOption);

    if (samples is null && queue is null)
    {
        return output.Fail(
            ExitCodes.UsageError,
            "no_source",
            "Give --samples <dir> (file mode, the safe default) or --queue <name> (reads live traffic).");
    }

    if (samples is not null && queue is not null)
    {
        return output.Fail(
            ExitCodes.UsageError, "two_sources", "--samples and --queue are alternatives.");
    }

    if (samples is not null)
    {
        var subject = parse.GetValue(subjectOption);

        return subject is null
            ? output.Fail(
                ExitCodes.UsageError,
                "subject_required",
                "File mode cannot know which subject the payloads belong to. Pass --subject.")
            : await InferCommand.FromFilesAsync(
                output, samples, subject, parse.GetValue(outOption)!, token).ConfigureAwait(false);
    }

    return await InferCommand.FromQueueAsync(
        output,
        parse.GetValue(brokerOption)!,
        queue!,
        parse.GetValue(maxOption),
        parse.GetValue(subjectOption),
        parse.GetValue(outOption)!,
        parse.GetValue(acknowledgeOption),
        token).ConfigureAwait(false);
});

root.Add(check);
root.Add(lint);
root.Add(infer);
root.Add(push);
root.Add(promote);
root.Add(impact);
root.Add(diff);
root.Add(export);

var parsed = root.Parse(args);

// System.CommandLine returns 1 for a parse error, which is ExitCodes.ContractViolation. That
// collision is the exact failure ExitCodes exists to prevent: a mistyped flag would tell CI
// that a schema broke, and the pipeline would report a contract violation that never happened.
// Parse errors are intercepted here and mapped to 2 before anything is invoked.
if (parsed.Errors.Count > 0)
{
    foreach (var error in parsed.Errors)
    {
        Console.Error.WriteLine($"error: {error.Message}");
    }

    Console.Error.WriteLine("\nRun 'concordat --help' for usage.");
    return ExitCodes.UsageError;
}

return await parsed.InvokeAsync().ConfigureAwait(false);

Output NewOutput(ParseResult parse) =>
    new(parse.GetValue(jsonOption), Console.Out, Console.Error);

HttpClient NewHttpClient(ParseResult parse)
{
    var registry = new Uri(parse.GetValue(registryOption)!);
    var client = new HttpClient { BaseAddress = registry };
    var key = parse.GetValue(apiKeyOption);

    if (!string.IsNullOrWhiteSpace(key))
    {
        // The credential rides on every request regardless of scheme, so a non-loopback
        // http:// registry -- a typo'd --registry, or the http:// default left unchanged
        // against a real deployment -- would send it on the wire in the clear.
        if (registry.Scheme is "http" && !registry.IsLoopback)
        {
            throw new RegistryException(
                ExitCodes.UsageError,
                "api_key_over_http",
                $"--registry is '{registry}' and an API key is set. Sending it over a " +
                "non-loopback http:// connection puts the credential on the wire in the " +
                "clear. Use https://, or point --registry at localhost/127.0.0.1 if the " +
                "registry really is local.");
        }

        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {key}");
    }

    return client;
}

// One place where a registry failure becomes an exit code, so no command can accidentally
// report an outage as a contract violation.
async Task<int> Run(ParseResult parse, Func<RegistryApi, Output, Task<int>> body)
{
    var output = NewOutput(parse);

    try
    {
        using var http = NewHttpClient(parse);
        return await body(new RegistryApi(http, parse.GetValue(environmentOption)!), output)
            .ConfigureAwait(false);
    }
    catch (RegistryException ex)
    {
        return output.Fail(ex.ExitCode, ex.Code, ex.Message);
    }
    catch (UriFormatException ex)
    {
        return output.Fail(ExitCodes.UsageError, "registry_uri_invalid", ex.Message);
    }
}
