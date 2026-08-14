namespace Concordat.Contracts.Testing;

/// <summary>Where the registry is, and what to ask it about.</summary>
public sealed class ConcordatTestOptions
{
    /// <summary>The registry's base address.</summary>
    /// <remarks>
    /// Defaults from <c>CONCORDAT_REGISTRY</c> so a test project reads the same variable the CLI
    /// does. A team that had to configure the two separately would eventually point them at
    /// different registries and get a green build against the wrong one.
    /// </remarks>
    public Uri? BaseAddress { get; set; } =
        Uri.TryCreate(
            System.Environment.GetEnvironmentVariable("CONCORDAT_REGISTRY"),
            UriKind.Absolute,
            out var uri)
            ? uri
            : null;

    /// <summary>Which environment to check against.</summary>
    /// <remarks>
    /// <b>Point this at the environment you deploy to, not the one CI writes to.</b> Checking a
    /// contract against the environment your own pipeline just pushed to answers a question you
    /// already knew the answer to; the useful question is whether the type still fits what
    /// production is serving.
    /// </remarks>
    public string Environment { get; set; } =
        System.Environment.GetEnvironmentVariable("CONCORDAT_ENV") ?? "prod";

    /// <summary>The API key, if the registry requires one.</summary>
    /// <remarks>
    /// Defaults from <c>CONCORDAT_API_KEY</c>. Never pass this as a literal in a test file — the
    /// same rule the CLI states, for the same reason.
    /// </remarks>
    public string? ApiKey { get; set; } =
        System.Environment.GetEnvironmentVariable("CONCORDAT_API_KEY");

    /// <summary>
    /// Whether a subject the registry has never seen counts as compatible.
    /// </summary>
    /// <remarks>
    /// <b>On, and it is the right default.</b> A team adding a new contract type has not broken
    /// anything, and a red test between writing the type and CI first pushing it would teach
    /// them that this check cries wolf. Turn it off in a suite whose job is to prove every
    /// contract is registered.
    /// </remarks>
    public bool TreatUnknownSubjectAsCompatible { get; set; } = true;

    /// <summary>How long to wait for the registry.</summary>
    /// <remarks>
    /// Short. This runs in a test suite, and a contract check that hangs for the default
    /// hundred seconds will be deleted by whoever is waiting for the build.
    /// </remarks>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The transport, when the default one will not do.
    /// </summary>
    /// <remarks>
    /// For a corporate proxy, a self-signed registry certificate, or a test that stands the API
    /// up in-process. Left null the ordinary way, and <see cref="CreateClient"/> builds its own.
    /// <b>Not disposed here</b>: a caller supplying a handler usually owns it and is reusing it.
    /// </remarks>
    public HttpMessageHandler? Handler { get; set; }

    /// <summary>Validates the options, throwing on anything that cannot work.</summary>
    /// <exception cref="ConcordatContractException">A required value is missing.</exception>
    public void Validate()
    {
        if (BaseAddress is null)
        {
            throw new ConcordatContractException(
                $"No registry address. Set {nameof(BaseAddress)}, or the CONCORDAT_REGISTRY " +
                "environment variable.");
        }

        if (string.IsNullOrWhiteSpace(Environment))
        {
            throw new ConcordatContractException($"{nameof(Environment)} is required.");
        }
    }

    /// <summary>Builds the transport.</summary>
    /// <returns>A client bound to <see cref="BaseAddress"/>.</returns>
    /// <remarks>
    /// A new <see cref="HttpClient"/> per call, disposed by the caller. That is the pattern to
    /// avoid in a server and the right one here: a test process makes a handful of requests over
    /// a few seconds, and the socket-exhaustion problem <c>IHttpClientFactory</c> exists to solve
    /// needs sustained traffic to appear at all.
    /// </remarks>
    public HttpClient CreateClient()
    {
        // disposeHandler: false when the caller supplied one -- disposing somebody else's
        // handler is how a second call in the same test gets an ObjectDisposedException.
        var http = Handler is null
            ? new HttpClient()
            : new HttpClient(Handler, disposeHandler: false);

        http.BaseAddress = BaseAddress;
        http.Timeout = Timeout;

        if (!string.IsNullOrWhiteSpace(ApiKey))
        {
            http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {ApiKey}");
        }

        return http;
    }
}

/// <summary>What the registry said about one contract.</summary>
/// <param name="Compatible">Whether the registry would accept it.</param>
/// <param name="Subject">The subject it was checked against.</param>
/// <param name="SchemaId">The content-addressed id of the schema as checked.</param>
/// <param name="BreakingChanges">What would break, as <c>path: message</c>.</param>
/// <param name="SuggestedSemver">The label the registry would expect for this change.</param>
public sealed record CompatibilityVerdict(
    bool Compatible,
    string Subject,
    string? SchemaId,
    IReadOnlyList<string> BreakingChanges,
    string? SuggestedSemver);

/// <summary>
/// A contract check failed, or could not be made.
/// </summary>
/// <remarks>
/// Deliberately not derived from any test framework's assertion type. xunit, NUnit and MSTest
/// all report an unhandled exception as a failure, so this works in all three — where inheriting
/// from one of their base types would make the package unusable in the other two.
/// </remarks>
public sealed class ConcordatContractException : Exception
{
    /// <summary>Creates an exception.</summary>
    /// <param name="message">What went wrong.</param>
    public ConcordatContractException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a cause.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    public ConcordatContractException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates an exception with no message. Prefer the overloads that carry one.</summary>
    public ConcordatContractException()
        : base("A Concordat contract check failed.")
    {
    }
}
