using Concordat.Application.Abstractions;
using Concordat.Application.Registry;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Concordat.Formats.Abstractions;

namespace Concordat.Api;

/// <summary>The content-addressed schema routes under <c>/v1/schemas</c>.</summary>
public static class SchemaEndpoints
{
    /// <summary>Maps the schema routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapSchemaEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/v1/schemas").WithTags("Schemas");

        group.MapGet("/{id}", GetSchema)
            .WithSummary("Get a schema by its content-addressed id")
            .WithDescription(
                "Schemas are stored globally and deduplicated by content (ADR-015), so there " +
                "is no tenant column to filter on. Access is authorised by reachability: a " +
                "caller may read a schema only if some subject in their tenant references it. " +
                "An unreachable id returns 404, identical to one that does not exist — a " +
                "distinct 403 would confirm another tenant's schema exists.");

        group.MapGet("/{id}/subjects", GetUsages)
            .WithSummary("List the subjects and versions using a schema");

        group.MapPost("/lookup", Lookup)
            .WithSummary("Find the id a schema document would receive")
            .WithDescription(
                "Canonicalises the document and returns its content-addressed id, plus " +
                "whether it is already known. Never writes.");

        return app;
    }

    private static async Task<IResult> GetSchema(
        string id, IDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(new GetSchemaQuery(id), cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Ok(SchemaResponse.From(result.Value));
    }

    private static async Task<IResult> GetUsages(
        string id, IDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(new GetSchemaUsagesQuery(id), cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Ok(result.Value);
    }

    private static async Task<IResult> Lookup(
        LookupSchemaRequest request,
        ISchemaFormatRegistry formats,
        ISchemaRepository schemas,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryParseFormat(request.Format, out var format))
        {
            return ProblemDetailsMapping.From(new DomainError(
                "invalid_request",
                $"Unknown format '{request.Format}'. Expected json, avro or protobuf."));
        }

        var canonical = formats.Canonicalizer(format).Canonicalize(request.Schema);
        if (canonical.IsFailure)
        {
            return ProblemDetailsMapping.From(canonical.Error!);
        }

        var references = formats.ReferenceExtractor(format).Extract(canonical.Value);
        if (references.IsFailure)
        {
            return ProblemDetailsMapping.From(references.Error!);
        }

        var id = SchemaIdComputer.Compute(format, canonical.Value, references.Value);

        // Reachability again: "known" must mean known to this caller, or lookup becomes an
        // oracle for probing whether another tenant has registered a given document.
        var known = await schemas.IsReachableByCurrentTenantAsync(id, cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(new LookupSchemaResponse(id.Value, known, canonical.Value));
    }

    private static bool TryParseFormat(string? value, out SchemaFormat format)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case WireTokens.FormatJson: format = SchemaFormat.Json; return true;
            case WireTokens.FormatAvro: format = SchemaFormat.Avro; return true;
            case WireTokens.FormatProtobuf: format = SchemaFormat.Protobuf; return true;
            default: format = default; return false;
        }
    }
}

/// <summary>The answer to a schema lookup.</summary>
/// <param name="SchemaId">The id this document would receive.</param>
/// <param name="Known">Whether the caller can already reach a stored schema with this id.</param>
/// <param name="Canonical">The canonical text the id was computed from.</param>
public sealed record LookupSchemaResponse(string SchemaId, bool Known, string Canonical);
