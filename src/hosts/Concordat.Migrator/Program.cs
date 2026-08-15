using Concordat.Infrastructure;
using Concordat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

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
}
else
{
    // Guarded because CA1873 treats the argument expressions as potentially expensive when
    // the level is disabled. Irrelevant for a process that runs once, but the rule earns its
    // keep on the request paths and is not worth disabling globally for this.
    if (logger.IsEnabled(LogLevel.Information))
    {
        logger.LogInformation(
            "Applying {Count} migration(s): {Migrations}",
            pending.Count,
            string.Join(", ", pending));
    }

    await context.Database.MigrateAsync().ConfigureAwait(false);

    logger.LogInformation("Migrations applied.");
}

// Runs whether or not there were pending migrations: a password rotation with nothing else to
// migrate is exactly the case this has to still handle, not just the first-ever deploy.
//
// Optional: the connection string above is an admin login — DDL rights are what running
// migrations requires. The API itself needs none of that, only CRUD on the tables this just
// created, so if an application-role password is configured, provision that narrower role
// here rather than have the API run under the same admin credentials indefinitely. Skipped
// entirely when unset, which keeps every deployment that does not opt in (docker-compose,
// existing installs) exactly as it was.
var appRolePassword =
    builder.Configuration["Concordat:Provisioning:AppRolePassword"]
    ?? builder.Configuration["CONCORDAT__Provisioning__AppRolePassword"];

if (!string.IsNullOrEmpty(appRolePassword))
{
    await ProvisionApplicationRoleAsync(connectionString, appRolePassword, logger).ConfigureAwait(false);
}

return 0;

// Creates (or re-passwords) a login-only role with CRUD on the schema this migration run just
// established, and nothing else — no DDL, no ownership, no grants on other databases. Run
// every time rather than only once: a rotated password still needs applying, and every
// statement here is idempotent.
static async Task ProvisionApplicationRoleAsync(
    string connectionString, string password, ILogger logger)
{
    const string RoleName = "concordat_app";

    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync().ConfigureAwait(false);

    // Postgres has no CREATE ROLE IF NOT EXISTS, and a role's password cannot be set via a
    // regular bound parameter — CREATE/ALTER ROLE's PASSWORD clause only accepts a string
    // literal, not a query argument. Doubling embedded quotes is the standard escape for a
    // '...'-quoted Postgres literal under the default standard_conforming_strings=on (backslash
    // is not special there), so a password containing a quote cannot break out of it.
    var quotedPassword = "'" + password.Replace("'", "''") + "'";

    try
    {
        await using var create = new NpgsqlCommand(
            $"CREATE ROLE {RoleName} LOGIN PASSWORD {quotedPassword}", connection);
        await create.ExecuteNonQueryAsync().ConfigureAwait(false);

        // Guarded because CA1873 treats the argument expressions as potentially expensive
        // when the level is disabled — same reasoning as the migration-count log above.
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Created application role {Role}.", RoleName);
        }
    }
    catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.DuplicateObject)
    {
        await using var alter = new NpgsqlCommand(
            $"ALTER ROLE {RoleName} LOGIN PASSWORD {quotedPassword}", connection);
        await alter.ExecuteNonQueryAsync().ConfigureAwait(false);

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Application role {Role} already existed; password updated.", RoleName);
        }
    }

    // Re-granted every run, including on tables a later migration adds: GRANT is idempotent,
    // and ALTER DEFAULT PRIVILEGES only covers objects created *after* it runs, so a table
    // added by tomorrow's migration needs this statement to have run again, not just once.
    await using var grant = new NpgsqlCommand(
        $"""
        GRANT CONNECT ON DATABASE {connection.Database} TO {RoleName};
        GRANT USAGE ON SCHEMA public TO {RoleName};
        GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO {RoleName};
        ALTER DEFAULT PRIVILEGES IN SCHEMA public
          GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO {RoleName};
        """,
        connection);
    await grant.ExecuteNonQueryAsync().ConfigureAwait(false);

    if (logger.IsEnabled(LogLevel.Information))
    {
        logger.LogInformation("Granted {Role} CRUD on schema public.", RoleName);
    }
}

/// <summary>Entry point marker, so the logger has a category to bind to.</summary>
public partial class Program;
