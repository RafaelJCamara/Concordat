namespace Concordat.Domain.Results;

/// <summary>
/// A domain rule violation, carrying the stable wire code that identifies it.
/// </summary>
/// <param name="Code">
/// The stable <c>concordatCode</c> string. Values come from <see cref="ConcordatCodes"/>;
/// they are part of the published protocol and must not change once released.
/// </param>
/// <param name="Message">A human-readable explanation. Not part of the protocol contract.</param>
public sealed record DomainError(string Code, string Message);
