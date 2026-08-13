using Concordat.Application.Abstractions;
using Concordat.Application.Registry;
using Concordat.Domain.Registry;

namespace Concordat.Api;

/// <summary>The cold-start route.</summary>
public static class BootstrapEndpoint
{
    /// <summary>Maps the bootstrap route.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapBootstrapEndpoint(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/v1/environments/{env}/bootstrap", Bootstrap)
            .WithTags("Bootstrap")
            .WithSummary("Fetch everything needed to warm a client cache, in one request")
            .WithDescription(
                "Cold start is the real load pattern, not steady state. A fleet-wide rolling " +
                "restart empties every cache at the same moment and stampedes the registry — " +
                "which sits on the deserialize path, so a registry that buckles takes " +
                "consumption down with it. One request instead of N is the mitigation. " +
                "Referenced schemas are included transitively, so the payload is " +
                "self-sufficient and a client never has to follow a reference it did not plan " +
                "for. Retired subjects are excluded.");

        return app;
    }

    private static async Task<IResult> Bootstrap(
        string env,
        IDispatcher dispatcher,
        IEnvironmentResolver environments,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(
            new BootstrapQuery(environments.Resolve(env)), cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            return ProblemDetailsMapping.From(result.Error!);
        }

        var value = result.Value;

        return TypedResults.Ok(new BootstrapResponse(
            [.. value.Subjects.Select(s => new BootstrapSubjectResponse(
                s.Name,
                WireTokens.For(s.Format),
                s.LatestOrdinal,
                s.LatestSchemaId,
                s.LatestSemver))],
            value.Schemas.ToDictionary(
                pair => pair.Key,
                pair => SchemaResponse.From(pair.Value),
                StringComparer.Ordinal)));
    }
}
