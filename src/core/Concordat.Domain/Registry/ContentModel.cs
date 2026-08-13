namespace Concordat.Domain.Registry;

/// <summary>
/// Whether documents may carry properties the schema does not describe.
/// </summary>
/// <remarks>
/// <para>
/// <b>Explicit subject configuration, never inferred from a schema document</b> (DESIGN §7).
/// Inferring it per-version lets it flip silently between v1 and v2, which changes the meaning
/// of every subsequent compatibility verdict without anyone deciding to.
/// </para>
/// <para>
/// This is the setting Confluent gets wrong. Because it treats open and closed content models
/// under mutually exclusive rules inferred per schema, adding an optional field is not
/// backward compatible under its defaults — so teams set compatibility to <c>NONE</c> and the
/// registry's central value proposition is switched off.
/// </para>
/// </remarks>
public enum ContentModel
{
    /// <summary>
    /// Unknown properties are permitted. Matches JSON Schema's own default
    /// (<c>additionalProperties: true</c>) and is the default for a new subject.
    /// </summary>
    Open = 1,

    /// <summary>
    /// Only described properties are permitted. Adding a property then affects consumers, so
    /// the engine reports it.
    /// </summary>
    Closed = 2,
}
