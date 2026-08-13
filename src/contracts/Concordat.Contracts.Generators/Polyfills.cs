using System.ComponentModel;

namespace System.Runtime.CompilerServices;

/// <summary>
/// The marker the compiler needs for <c>init</c> accessors and records.
/// </summary>
/// <remarks>
/// Present in .NET 5 onwards and absent from netstandard2.0, which this project must target
/// because Roslyn loads analyzers into a compiler process that may still be .NET Framework
/// MSBuild. Declaring it here is the conventional shim; the compiler only needs the type to
/// exist.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
internal static class IsExternalInit;
