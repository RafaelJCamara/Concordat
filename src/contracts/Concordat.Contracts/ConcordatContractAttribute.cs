using System;

namespace Concordat.Contracts;

/// <summary>
/// Marks a type as the source of truth for a Concordat subject.
/// </summary>
/// <remarks>
/// <para>
/// <b>The C# type is the contract, and breaking it breaks the build.</b> A schema generated
/// from the type is compared against the checked-in <c>contracts/</c> file at compile time, so
/// a developer who renames a property finds out in their own editor rather than from a
/// quarantined message in production a week later.
/// </para>
/// <para>
/// This is the .NET-flavoured path, and it is deliberately optional. The registry never sees a
/// C# type (ADR-019) — it sees the same <c>&lt;subject&gt;.json</c> file a Go or Python shop
/// writes by hand. Deleting this package changes nothing about the contract.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class ConcordatContractAttribute : Attribute
{
    /// <summary>Declares the subject this type defines.</summary>
    /// <param name="subject">
    /// The subject name, for example <c>acme.orders.OrderCreated</c>. Written out rather than
    /// derived from the CLR type name: the derivation would have to be reproduced identically
    /// by four other SDKs, and a namespace refactor must not silently rename a live subject.
    /// </param>
    public ConcordatContractAttribute(string subject) => Subject = subject;

    /// <summary>The subject this type defines.</summary>
    public string Subject { get; }

    /// <summary>
    /// The description recorded in the generated schema, if any.
    /// </summary>
    public string? Description { get; set; }
}

/// <summary>
/// The schema generated for one contract type, attached to the assembly at compile time.
/// </summary>
/// <remarks>
/// <para>
/// Emitted by the generator, never written by hand. It exists so there is exactly
/// <b>one</b> schema generator in the product: the compile-time one. A second, reflection-based
/// generator for test-time use would be a separate implementation of the same mapping, and the
/// two would drift — which is the failure this project spends most of its effort preventing.
/// </para>
/// <para>
/// An assembly-level attribute rather than a generated static class, so consumers discover
/// contracts by asking the assembly rather than by knowing a magic type name.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class ConcordatGeneratedSchemaAttribute : Attribute
{
    /// <summary>Records a generated schema.</summary>
    /// <param name="subject">The subject.</param>
    /// <param name="clrType">The type it was generated from, for diagnostics.</param>
    /// <param name="schema">The schema document.</param>
    public ConcordatGeneratedSchemaAttribute(string subject, string clrType, string schema)
    {
        Subject = subject;
        ClrType = clrType;
        Schema = schema;
    }

    /// <summary>The subject.</summary>
    public string Subject { get; }

    /// <summary>The fully-qualified CLR type the schema came from.</summary>
    public string ClrType { get; }

    /// <summary>The schema document.</summary>
    public string Schema { get; }
}
