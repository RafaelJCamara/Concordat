using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Concordat.Api;

/// <summary>
/// Declares the credential every <see cref="RequireScopeFilter"/>-gated route accepts, so the
/// OpenAPI document -- artifact 1 of ADR-019's five -- says so itself rather than leaving an
/// implementer to find <see cref="RequireScopeFilter"/> in this repository's C#.
/// </summary>
public sealed class BearerSecuritySchemeTransformer : IOpenApiDocumentTransformer
{
    /// <summary>The scheme id every protected operation references.</summary>
    public const string SchemeId = "Bearer";

    /// <inheritdoc />
    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();

        document.Components.SecuritySchemes[SchemeId] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "cdt_...",
            Description =
                "An API key or session secret, issued by POST /v1/auth/bootstrap, " +
                "/v1/auth/signup or /v1/auth/signin, or minted at /v1/api-keys. Send it as " +
                "'Authorization: Bearer cdt_...'. An unclaimed instance (ADR-008) answers every " +
                "request as an owner regardless of this header; once claimed, every route that " +
                "declares a required scope below answers 401 'unauthenticated' without it " +
                "(ADR-027).",
        };

        return Task.CompletedTask;
    }
}

/// <summary>
/// Attaches the <see cref="BearerSecuritySchemeTransformer"/> credential to every operation
/// whose route called <see cref="ScopeRequirements.RequireScope"/>, reading the same
/// <see cref="RequiredScopes"/> endpoint metadata <c>EveryMutatingRouteDeclaresAScope</c>
/// enumerates -- so this can never say less than that test already guarantees.
/// </summary>
/// <remarks>
/// The scope names themselves do not go in the security requirement's scope array: that array
/// is spec-reserved for OAuth2 and OpenID Connect schemes, and an <c>http</c> scheme like
/// Concordat's must leave it empty. They go in the operation description instead, which is
/// where a reader actually looks for "which credential, holding what."
/// </remarks>
public sealed class ScopeSecurityOperationTransformer : IOpenApiOperationTransformer
{
    /// <inheritdoc />
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);

        var required = context.Description.ActionDescriptor.EndpointMetadata
            .OfType<RequiredScopes>()
            .FirstOrDefault();

        if (required is null)
        {
            return Task.CompletedTask;
        }

        operation.Security ??= [];
        operation.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(BearerSecuritySchemeTransformer.SchemeId, context.Document, null)] = [],
        });

        var scopes = string.Join(" or ", required.Scopes);
        var note = $"Requires scope: `{scopes}`.";

        operation.Description = string.IsNullOrEmpty(operation.Description)
            ? note
            : $"{operation.Description}\n\n{note}";

        return Task.CompletedTask;
    }
}
