namespace Concordat.Formats.Json;

/// <summary>
/// Bounds the regex matching NJsonSchema does on this process's behalf (a ReDoS defence).
/// </summary>
/// <remarks>
/// <para>
/// <b>NJsonSchema compiles a schema's <c>pattern</c> and <c>patternProperties</c> keywords to
/// a <see cref="System.Text.RegularExpressions.Regex"/> with no match timeout of its own.</b>
/// Both the pattern and the string it is matched against are attacker-controlled under this
/// engine's threat model — a schema is registered by an API caller, and the payload it
/// validates arrives over RabbitMQ on the publish and consume path. A pattern with
/// catastrophic backtracking, such as <c>^(a+)+$</c>, pins a thread at 100% CPU for as long as
/// the process runs: this is a ReDoS denial of service, not a crash, so nothing catches it and
/// no retry recovers from it.
/// </para>
/// <para>
/// <b><see cref="ApplyProcessWideDefault"/> is not reliable on its own, and this is not an
/// oversight.</b> .NET's mechanism for this — <c>AppContext.SetData("REGEX_DEFAULT_MATCH_TIMEOUT",
/// …)</c> — is read exactly once, by <see cref="System.Text.RegularExpressions.Regex"/>'s own
/// static constructor, the first time anything in the process touches that type. Whichever call
/// happens first wins, permanently, for the rest of the process — a library has no way to
/// guarantee it runs before something else in the host (ASP.NET Core's own startup, a test
/// host, a future dependency) does. Measured directly: a module initializer in this assembly
/// was tried first and did not reliably win that race even in this repository's own test host.
/// The one place that <em>can</em> win it is a host's true entry point, which is why this method
/// exists to be called explicitly as the first statement of <c>Main</c> — see
/// <c>Concordat.Api/Program.cs</c> and <c>Concordat.Cli/Program.cs</c> — rather than running
/// itself. <see cref="NJsonSchemaPayloadValidator"/> does not depend on it having won: it wraps
/// the match in its own hard timeout regardless, which is the guaranteed backstop.
/// </para>
/// </remarks>
public static class RegexSafety
{
    /// <summary>
    /// How long an unbounded regex match may run before .NET aborts it with a
    /// <see cref="System.Text.RegularExpressions.RegexMatchTimeoutException"/>, when this
    /// process-wide default has taken effect.
    /// </summary>
    public static readonly TimeSpan DefaultMatchTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Sets the process-wide default regex match timeout. Call this as the first statement of
    /// <c>Main</c>, before anything else in the process can touch
    /// <see cref="System.Text.RegularExpressions.Regex"/> — see the type remarks for why the
    /// position matters and why nothing downstream relies on this alone.
    /// </summary>
    public static void ApplyProcessWideDefault() =>
        AppContext.SetData("REGEX_DEFAULT_MATCH_TIMEOUT", DefaultMatchTimeout);
}
