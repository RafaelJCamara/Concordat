using Concordat.Contracts;

namespace ContractDrift;

/// <summary>
/// The C# type is the contract. Change it and the build fails.
/// </summary>
/// <remarks>
/// Try it: remove the <c>?</c> from <see cref="Note"/> and build. The compiler reports CDT003
/// pointing at <c>#/properties/note/type</c>, showing <c>["string","null"]</c> against
/// <c>"string"</c> — which reads as "you removed a ?", not as a mystery.
/// </remarks>
/// <param name="Id">The order id.</param>
/// <param name="Reference">The customer-visible reference.</param>
/// <param name="Note">An optional note. Nullable here means optional in the schema.</param>
[ConcordatContract("acme.orders.OrderCreated")]
public record OrderCreated(int Id, string Reference, string? Note);
