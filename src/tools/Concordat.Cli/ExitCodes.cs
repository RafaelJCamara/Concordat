namespace Concordat.Cli;

/// <summary>
/// The CLI's contract with CI. These numbers are as public as the REST API.
/// </summary>
/// <remarks>
/// <para>
/// <b>The important split is 1 against 3.</b> "Your change breaks the contract" and "the
/// registry is unreachable" must never share an exit code. If they do, a pipeline cannot tell
/// a genuine violation from an outage — and the way that gets resolved in practice is that
/// somebody adds <c>|| true</c> to the step, which switches the gate off permanently and
/// silently.
/// </para>
/// <para>
/// So a violation is <see cref="ContractViolation"/> and only ever that. Everything that is
/// Concordat's own problem rather than the caller's gets a different number, and a pipeline
/// that wants to be lenient about outages can retry or ignore <see cref="RegistryUnavailable"/>
/// without ever weakening the check it actually cares about.
/// </para>
/// </remarks>
public static class ExitCodes
{
    /// <summary>Everything checked out.</summary>
    public const int Success = 0;

    /// <summary>
    /// A contract was broken. <b>The one CI gates on.</b>
    /// </summary>
    public const int ContractViolation = 1;

    /// <summary>The command line was wrong — unknown option, missing argument.</summary>
    public const int UsageError = 2;

    /// <summary>
    /// The registry could not be reached, or refused the request.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="ContractViolation"/>. A red build here means "go and look at
    /// the registry", not "go and look at your schema".
    /// </remarks>
    public const int RegistryUnavailable = 3;

    /// <summary>A local file was missing, unreadable, or not valid in its declared format.</summary>
    public const int LocalFileError = 4;

    /// <summary>Concordat itself failed in a way it did not anticipate.</summary>
    public const int InternalError = 70;
}
