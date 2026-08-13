using Concordat.Infrastructure;
using Concordat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Applies pending migrations and exits. Runs as a separate process rather than on API
// startup so that a rolling deployment migrates exactly once, instead of every replica
// racing to do it.
var builder = Host.CreateApplicationBuilder(args);

var connectionString =
    builder.Configuration.GetConnectionString("Concordat")
    ?? builder.Configuration["CONCORDAT__ConnectionStrings__Concordat"]
    ?? throw new InvalidOperationException(
        "No connection string. Set ConnectionStrings__Concordat or " +
        "CONCORDAT__ConnectionStrings__Concordat.");

builder.Services.AddConcordatPersistence(connectionString);

using var host = builder.Build();
using var scope = host.Services.CreateScope();

var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
var context = scope.ServiceProvider.GetRequiredService<ConcordatDbContext>();

var pending = (await context.Database.GetPendingMigrationsAsync().ConfigureAwait(false)).ToList();

if (pending.Count == 0)
{
    logger.LogInformation("Database is up to date; no migrations to apply.");
    return 0;
}

// Guarded because CA1873 treats the argument expressions as potentially expensive when the
// level is disabled. Irrelevant for a process that runs once, but the rule earns its keep on
// the request paths and is not worth disabling globally for this.
if (logger.IsEnabled(LogLevel.Information))
{
    logger.LogInformation(
        "Applying {Count} migration(s): {Migrations}",
        pending.Count,
        string.Join(", ", pending));
}

await context.Database.MigrateAsync().ConfigureAwait(false);

logger.LogInformation("Migrations applied.");
return 0;

/// <summary>Entry point marker, so the logger has a category to bind to.</summary>
public partial class Program;
