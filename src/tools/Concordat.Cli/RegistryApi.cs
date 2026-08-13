using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Concordat.Cli;

/// <summary>The registry refused, or could not be reached.</summary>
public sealed class RegistryException(int exitCode, string code, string message) : Exception(message)
{
    /// <summary>Which <see cref="ExitCodes"/> value this warrants.</summary>
    public int ExitCode { get; } = exitCode;

    /// <summary>A stable <c>concordatCode</c>.</summary>
    public string Code { get; } = code;
}

/// <summary>One divergence, as the registry reports it.</summary>
/// <param name="Path">A JSON Pointer into the schema document.</param>
/// <param name="Kind">A stable token.</param>
/// <param name="Direction"><c>BACKWARD</c> or <c>FORWARD</c>.</param>
/// <param name="Surface"><c>WIRE</c>, <c>WIRE_JSON</c> or <c>SOURCE</c>.</param>
/// <param name="Message">An actionable explanation.</param>
/// <param name="ConflictsWithVersion">The prior version compared against.</param>
public sealed record Divergence(
    string Path, string Kind, string Direction, string Surface, string Message, int ConflictsWithVersion);

/// <summary>The policy a check ran under.</summary>
/// <param name="Mode">Who must keep working.</param>
/// <param name="Surface">How much must keep working.</param>
public sealed record Policy(string? Mode, string? Surface);

/// <summary>A dry-run compatibility result.</summary>
/// <param name="Compatible">Whether the proposal satisfies the policy.</param>
/// <param name="SchemaId">The id the proposal would receive.</param>
/// <param name="Policy">The policy it was evaluated under.</param>
/// <param name="BreakingChanges">Divergences that violate the policy.</param>
/// <param name="AllDivergences">Every divergence, including tolerated ones.</param>
/// <param name="SuggestedSemver">The label this change warrants.</param>
public sealed record CompatibilityResult(
    bool Compatible,
    string SchemaId,
    Policy Policy,
    IReadOnlyList<Divergence> BreakingChanges,
    IReadOnlyList<Divergence> AllDivergences,
    string? SuggestedSemver);

/// <summary>The outcome of registering a version.</summary>
/// <param name="Subject">The subject.</param>
/// <param name="Ordinal">The allocated ordinal.</param>
/// <param name="SchemaId">The content-addressed id.</param>
/// <param name="Status">Active, or awaiting approval.</param>
/// <param name="Created">False when the schema was already at the tip.</param>
/// <param name="Divergences">Findings, including tolerated ones.</param>
public sealed record RegisterResult(
    string Subject,
    int Ordinal,
    string SchemaId,
    string Status,
    bool Created,
    IReadOnlyList<Divergence> Divergences);

/// <summary>A difference between two ordinals.</summary>
/// <param name="From">The earlier ordinal.</param>
/// <param name="To">The later ordinal.</param>
/// <param name="FromSchemaId">The earlier schema id.</param>
/// <param name="ToSchemaId">The later schema id.</param>
/// <param name="Identical">Whether both point at the same content.</param>
/// <param name="Policy">The policy the comparison ran under.</param>
/// <param name="Divergences">Every difference.</param>
public sealed record DiffResult(
    int From,
    int To,
    string FromSchemaId,
    string ToSchemaId,
    bool Identical,
    Policy Policy,
    IReadOnlyList<Divergence> Divergences);

/// <summary>A subject as the registry lists it.</summary>
/// <param name="Name">The subject name.</param>
/// <param name="Format">The schema language.</param>
/// <param name="Owner">Who owns it.</param>
/// <param name="Lifecycle">Active, deprecated or retired.</param>
/// <param name="Latest">
/// The gated latest pointer, or null when no version is active. Named to match the API's
/// <c>latest</c> field exactly — a mismatch here does not fail, it silently deserialises to
/// null, and every subject then looks like it has no approved version.
/// </param>
public sealed record SubjectSummary(
    string Name, string Format, string? Owner, string? Lifecycle, int? Latest);

/// <summary>A stored version. Carries the schema <em>id</em>, never the text.</summary>
/// <param name="Ordinal">The version ordinal.</param>
/// <param name="SchemaId">The content-addressed id.</param>
/// <param name="Status"><c>ACTIVE</c>, <c>AWAITING_APPROVAL</c> or <c>REJECTED</c>.</param>
/// <param name="SemanticVersion">Its optional label.</param>
public sealed record VersionDetail(
    int Ordinal, string SchemaId, string? Status, string? SemanticVersion);

/// <summary>A stored schema.</summary>
/// <param name="SchemaId">The content-addressed id.</param>
/// <param name="Format">The schema language.</param>
/// <param name="Schema">The canonical text.</param>
public sealed record SchemaDetail(string SchemaId, string Format, string Schema);

/// <summary>The tokens the registry uses for a version's status.</summary>
public static class VersionStatuses
{
    /// <summary>Live, and reachable through the latest pointer.</summary>
    public const string Active = "ACTIVE";

    /// <summary>Registered but gated: it does not move the latest pointer (ADR-017).</summary>
    public const string AwaitingApproval = "AWAITING_APPROVAL";

    /// <summary>Refused, and excluded from compatibility history.</summary>
    public const string Rejected = "REJECTED";
}

/// <summary>
/// The registry's admin surface, over plain HTTP.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately not <c>Concordat.Client</c>.</b> That client caches aggressively — schemas
/// forever, the latest pointer for 30 seconds — which is exactly right on the delivery path
/// and exactly wrong for a CI gate. A build that passed because the CLI answered from a stale
/// cache is worse than no gate at all, so every call here goes to the registry.
/// </para>
/// <para>
/// It is also a different surface: registering, checking and diffing are operations the
/// runtime client has no business exposing.
/// </para>
/// </remarks>
public sealed class RegistryApi(HttpClient http, string environment)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private string Root => $"/v1/environments/{Uri.EscapeDataString(environment)}";

    /// <summary>Checks a proposal without registering it.</summary>
    /// <param name="subject">The subject.</param>
    /// <param name="schema">The proposed schema document.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The verdict, or null when the subject does not exist yet.</returns>
    public async Task<CompatibilityResult?> CheckAsync(
        string subject, string schema, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"{Root}/subjects/{Uri.EscapeDataString(subject)}/compatibility",
            new { schema },
            cancellationToken).ConfigureAwait(false);

        // A subject nobody has created is not an error here. A first version cannot break
        // anything, so `check` reports it as new rather than as a missing resource.
        return response.StatusCode is HttpStatusCode.NotFound
            ? null
            : await ReadAsync<CompatibilityResult>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Registers a version.</summary>
    /// <param name="subject">The subject.</param>
    /// <param name="schema">The schema document.</param>
    /// <param name="semver">An optional intent label.</param>
    /// <param name="by">Who is registering.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    public async Task<RegisterResult> RegisterAsync(
        string subject, string schema, string? semver, string by, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"{Root}/subjects/{Uri.EscapeDataString(subject)}/versions",
            new { schema, semanticVersion = semver, registeredBy = by },
            cancellationToken).ConfigureAwait(false);

        return await ReadAsync<RegisterResult>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates a subject.</summary>
    /// <param name="subject">The subject name.</param>
    /// <param name="format">The schema language token.</param>
    /// <param name="owner">Who owns it.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether it was created, as opposed to already existing.</returns>
    public async Task<bool> CreateSubjectAsync(
        string subject, string format, string owner, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"{Root}/subjects",
            new { name = subject, format, owner },
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Conflict)
        {
            return false;
        }

        await EnsureAsync(response, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Lists subjects in the environment.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The subjects.</returns>
    public async Task<IReadOnlyList<SubjectSummary>> ListSubjectsAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, $"{Root}/subjects", null, cancellationToken)
            .ConfigureAwait(false);

        return await ReadAsync<List<SubjectSummary>>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Fetches one version.</summary>
    /// <param name="subject">The subject.</param>
    /// <param name="ordinal">The ordinal, or <c>latest</c>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The version, or null when absent.</returns>
    public async Task<VersionDetail?> GetVersionAsync(
        string subject, string ordinal, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"{Root}/subjects/{Uri.EscapeDataString(subject)}/versions/{Uri.EscapeDataString(ordinal)}",
            null,
            cancellationToken).ConfigureAwait(false);

        return response.StatusCode is HttpStatusCode.NotFound
            ? null
            : await ReadAsync<VersionDetail>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Fetches a schema's canonical text by id.</summary>
    /// <param name="schemaId">The content-addressed id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The canonical text, or null when the registry does not know the id.</returns>
    /// <remarks>
    /// A separate call because a version carries its schema <em>id</em>, not its text. That is
    /// the right split — the schema table is global and content-addressed (ADR-015), while a
    /// version is per-environment — but it does mean anything that needs the document, like
    /// <c>export</c> and <c>promote</c>, makes two calls rather than one.
    /// </remarks>
    public async Task<string?> GetSchemaTextAsync(string schemaId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Get, $"/v1/schemas/{Uri.EscapeDataString(schemaId)}", null, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NotFound)
        {
            return null;
        }

        var schema = await ReadAsync<SchemaDetail>(response, cancellationToken).ConfigureAwait(false);
        return schema.Schema;
    }

    /// <summary>Diffs two ordinals.</summary>
    /// <param name="subject">The subject.</param>
    /// <param name="from">The earlier ordinal.</param>
    /// <param name="to">The later ordinal.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The differences.</returns>
    public async Task<DiffResult> DiffAsync(
        string subject, int from, int to, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"{Root}/subjects/{Uri.EscapeDataString(subject)}/versions/{from}/diff/{to}",
            null,
            cancellationToken).ConfigureAwait(false);

        return await ReadAsync<DiffResult>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        try
        {
            return await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new RegistryException(
                ExitCodes.RegistryUnavailable,
                "registry_unreachable",
                $"Could not reach the registry at {http.BaseAddress}: {ex.Message}");
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await EnsureAsync(response, cancellationToken).ConfigureAwait(false);

        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken).ConfigureAwait(false)
            ?? throw new RegistryException(
                ExitCodes.RegistryUnavailable, "registry_response_empty", "The registry returned no body.");
    }

    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        // The registry's own concordatCode is far more useful than the status, so it is lifted
        // out of the Problem Details body and carried through to the CLI's output.
        string? code = null;
        string? detail = null;

        try
        {
            using var problem = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

            code = problem.RootElement.TryGetProperty("concordatCode", out var c) ? c.GetString() : null;
            detail = problem.RootElement.TryGetProperty("detail", out var d) ? d.GetString() : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // A proxy returning HTML. Fall through to the status code.
        }

        throw new RegistryException(
            ExitCodes.RegistryUnavailable,
            code ?? "registry_refused",
            detail ?? $"The registry answered {(int)response.StatusCode} {response.ReasonPhrase}.");
    }
}
