namespace Concordat.Formats.Avro;

/// <summary>
/// Unwinds a recursive walk over a malformed Avro document.
/// </summary>
/// <remarks>
/// Never crosses the public API: every entry point catches it and converts it to a
/// <c>schema_malformed</c> failure, because the ports in
/// <c>Concordat.Formats.Abstractions</c> report malformed input as a <c>Result</c>, not as a
/// throw.
/// </remarks>
/// <param name="message">What is wrong with the document.</param>
internal sealed class MalformedAvroSchemaException(string message) : Exception(message);
