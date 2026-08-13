using Concordat.Domain.Results;

namespace Concordat.Domain.Registry;

/// <summary>
/// A named dependency on another subject's version.
/// </summary>
/// <param name="Name">
/// The name the referring schema uses — a JSON Schema <c>$ref</c> target, a Protobuf
/// <c>import</c> filename, or an Avro named type.
/// </param>
/// <param name="Subject">The referenced subject.</param>
/// <param name="Version">The referenced version ordinal.</param>
public sealed record Reference(string Name, SubjectName Subject, int Version)
{
    /// <summary>Creates and validates a reference.</summary>
    /// <param name="name">The name the referring schema uses.</param>
    /// <param name="subject">The referenced subject.</param>
    /// <param name="version">The referenced version ordinal, which must be at least 1.</param>
    /// <returns>
    /// The reference, or a failure carrying <see cref="ConcordatCodes.ReferenceInvalid"/>.
    /// </returns>
    public static Result<Reference> Create(string? name, SubjectName subject, int version)
    {
        var trimmed = name?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return Result<Reference>.Failure(
                ConcordatCodes.ReferenceInvalid, "A reference name is required.");
        }

        return version < 1
            ? Result<Reference>.Failure(
                ConcordatCodes.ReferenceInvalid,
                $"A reference version must be at least 1; got {version}.")
            : Result<Reference>.Success(new Reference(trimmed, subject, version));
    }
}

/// <summary>
/// An immutable, content-addressed schema (ADR-015).
/// </summary>
/// <remarks>
/// <para>
/// Schemas are never deleted and never modified. Two schemas with the same body but different
/// reference sets are different schemas, because the identity hash covers the references too.
/// </para>
/// <para>
/// M1.1 does not compute the identity or enforce the body size ceiling; both are M1.2's
/// contract. What M1.1 guarantees is that the reference set is name-unique and stored in a
/// canonical order, so the hash M1.2 computes is stable.
/// </para>
/// </remarks>
public sealed class Schema
{
    private readonly List<Reference> _references;

    private Schema(SchemaId id, SchemaFormat format, string body, List<Reference> references)
    {
        Id = id;
        Format = format;
        Body = body;
        _references = references;
    }

    /// <summary>The content-addressed identity.</summary>
    public SchemaId Id { get; }

    /// <summary>The schema language.</summary>
    public SchemaFormat Format { get; }

    /// <summary>The canonical text of the schema.</summary>
    public string Body { get; }

    /// <summary>
    /// Dependencies on other subjects, ordered by name so the identity hash is stable.
    /// </summary>
    /// <remarks>
    /// Wrapped rather than returned directly: an <see cref="IReadOnlyList{T}"/> backed by a
    /// <see cref="List{T}"/> can be cast back to <see cref="ICollection{T}"/> and mutated. On
    /// an immutable, content-addressed type that would let the content drift from the id.
    /// </remarks>
    public IReadOnlyList<Reference> References => _references.AsReadOnly();

    /// <summary>Creates a schema.</summary>
    /// <param name="id">The content-addressed identity, computed by M1.2.</param>
    /// <param name="format">The schema language.</param>
    /// <param name="body">The canonical text. Must not be empty or whitespace.</param>
    /// <param name="references">
    /// Dependencies on other subjects. Names must be unique; order is not significant on input
    /// and is normalised.
    /// </param>
    /// <returns>
    /// The schema, or a failure carrying <see cref="ConcordatCodes.SchemaBodyEmpty"/> or
    /// <see cref="ConcordatCodes.DuplicateReferenceName"/>.
    /// </returns>
    public static Result<Schema> Create(
        SchemaId id,
        SchemaFormat format,
        string? body,
        IEnumerable<Reference>? references = null)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return Result<Schema>.Failure(
                ConcordatCodes.SchemaBodyEmpty, "A schema body is required.");
        }

        // Copied before validation: an IEnumerable supplied by a caller could yield different
        // items on a second enumeration, so validating one sequence and storing another would
        // be a hole.
        var ordered = (references ?? []).ToList();

        var duplicate = ordered
            .GroupBy(r => r.Name, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicate is not null)
        {
            return Result<Schema>.Failure(
                ConcordatCodes.DuplicateReferenceName,
                $"Reference name '{duplicate.Key}' appears more than once.");
        }

        ordered.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));

        return Result<Schema>.Success(new Schema(id, format, body, ordered));
    }
}
