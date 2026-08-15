using System.Text.Json;
using Concordat.Domain.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// The handler that turns a race the database caught into a 409 (M8.4), exercised directly
/// rather than by trying to win a real race against Postgres -- a genuine two-request race is
/// timing-dependent and would make this test flaky for the thing it exists to prove reliably.
/// </summary>
public class DbConflictExceptionHandlerTests
{
    // ProblemHttpResult.ExecuteAsync resolves IProblemDetailsService from RequestServices, so
    // the fake context needs a real (if minimal) container behind it -- not a mock of that
    // service, which would only prove this test agrees with itself about what it returns.
    private static readonly IServiceProvider Services =
        new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();

    private static async Task<(bool Handled, int Status, JsonDocument? Body)> HandleAsync(
        Exception exception)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = Services,
            Response = { Body = new MemoryStream() },
        };

        var handled = await new DbConflictExceptionHandler()
            .TryHandleAsync(context, exception, CancellationToken.None);

        context.Response.Body.Position = 0;
        var body = context.Response.Body.Length > 0
            ? await JsonDocument.ParseAsync(context.Response.Body)
            : null;

        return (handled, context.Response.StatusCode, body);
    }

    private static PostgresException UniqueViolation() =>
        new("duplicate key value violates unique constraint", "ERROR", "ERROR", "23505");

    [Fact]
    public async Task AUniqueConstraintViolationIs409NotAnUnhandled500()
    {
        var (handled, status, body) = await HandleAsync(
            new DbUpdateException("insert failed", UniqueViolation()));

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status409Conflict, status);
        Assert.Equal(
            ConcordatCodes.ConcurrentWriteConflict,
            body!.RootElement.GetProperty("concordatCode").GetString());
    }

    [Fact]
    public async Task AnOptimisticConcurrencyConflictIs409NotAnUnhandled500()
    {
        var (handled, status, body) = await HandleAsync(
            new DbUpdateConcurrencyException("the row changed underneath this update"));

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status409Conflict, status);
        Assert.Equal(
            ConcordatCodes.ConcurrentWriteConflict,
            body!.RootElement.GetProperty("concordatCode").GetString());
    }

    [Fact]
    public async Task AnUnrelatedDbUpdateExceptionIsLeftToTheGenericHandler()
    {
        // A foreign-key violation, a check constraint, a dropped connection -- none of these
        // are a race between two legitimate requests, and this handler must not claim them.
        var foreignKeyViolation = new PostgresException(
            "violates foreign key constraint", "ERROR", "ERROR", "23503");

        var (handled, _, _) = await HandleAsync(
            new DbUpdateException("insert failed", foreignKeyViolation));

        Assert.False(handled);
    }

    [Fact]
    public async Task AnUnrelatedExceptionIsLeftToTheGenericHandler()
    {
        var (handled, _, _) = await HandleAsync(new InvalidOperationException("unrelated"));

        Assert.False(handled);
    }
}
