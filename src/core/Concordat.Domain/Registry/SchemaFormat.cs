namespace Concordat.Domain.Registry;

/// <summary>
/// The schema language a subject is expressed in (ADR-002).
/// </summary>
/// <remarks>
/// Numbered from 1 so that <c>default(SchemaFormat)</c> is a detectably invalid value rather
/// than silently meaning <see cref="Json"/>.
/// </remarks>
public enum SchemaFormat
{
    /// <summary>JSON Schema. The only format implemented in M1.</summary>
    Json = 1,

    /// <summary>Apache Avro. Lands in M5.</summary>
    Avro = 2,

    /// <summary>Protocol Buffers. Lands in M5.</summary>
    Protobuf = 3,
}
