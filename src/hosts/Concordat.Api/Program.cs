using Concordat.Api;
using Concordat.Application;
using Concordat.Application.Abstractions;
using Concordat.Formats.Abstractions;
using Concordat.Formats.Avro;
using Concordat.Formats.Json;
using Concordat.Formats.Protobuf;
using Concordat.Infrastructure;
using Concordat.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString =
    builder.Configuration.GetConnectionString("Concordat")
    ?? "Host=localhost;Database=concordat;Username=postgres;Password=postgres";

// M9.1: the one place the deployment flavour is read. Everything it implies — how the tenant
// is resolved, and therefore what every global query filter sees — is a registration below
// this line rather than a branch anywhere above it.
var profile = builder.Configuration.GetValue("Concordat:Profile", ConcordatProfile.SelfHosted);

builder.Services.AddConcordatApplication();
builder.Services.AddConcordatPersistence(connectionString, profile);

// M7.5: both channels are registered whether or not SMTP is configured. A channel is only
// reached when a subscription names it, and an unconfigured one fails loudly with the reason
// recorded on the message rather than resolving to nothing at delivery time.
builder.Services.AddConcordatNotifications(
    smtp => builder.Configuration.GetSection("Concordat:Smtp").Bind(smtp));

builder.Services.AddHostedService<OutboxPump>();

// M8: who is calling, resolved once per request before anything else looks at it.
builder.Services.Configure<AuthenticationOptions>(
    builder.Configuration.GetSection("Concordat:Authentication"));
builder.Services.AddScoped<CallerContext>();
builder.Services.AddScoped<ICallerContext>(p => p.GetRequiredService<CallerContext>());
builder.Services.AddScoped<CallerResolver>();

// M7.2 persists the Data Protection key ring in the database; M9.1 wraps it with a KMS key
// where one is configured, and refuses to start in Cloud without one.
builder.AddConcordatKeyProtection(profile);

// Concrete formats are registered here and nowhere else. The Application layer resolves them
// through ISchemaFormatRegistry and never names one, which is what keeps a second format an
// additive change (DESIGN §8).
builder.Services.AddSingleton<ISchemaCanonicalizer, JsonSchemaCanonicalizer>();
builder.Services.AddSingleton<ICompatibilityChecker, JsonSchemaCompatibilityChecker>();
builder.Services.AddSingleton<ISchemaReferenceExtractor, JsonSchemaReferenceExtractor>();
builder.Services.AddSingleton<ISchemaBundler, JsonSchemaBundler>();

// JSON Schema is the only format with a portability checker, and deliberately so: it is the
// one with no compatibility specification and five independent validators interpreting the
// same text (M6.1, ADR-021).
builder.Services.AddSingleton<ISchemaPortabilityChecker, JsonSchemaPortabilityChecker>();

// Avro and Protobuf are complete surfaces as of M5. Their reference extractors refuse
// cross-subject references rather than resolving them (ADR-023): neither format has anywhere
// to pin a version, so following a reference would bind to whatever the target currently holds.
// Self-contained schemas — the common shape for both — register normally.
builder.Services.AddSingleton<ISchemaCanonicalizer, AvroSchemaCanonicalizer>();
builder.Services.AddSingleton<ICompatibilityChecker, AvroSchemaCompatibilityChecker>();
builder.Services.AddSingleton<ISchemaReferenceExtractor, AvroSchemaReferenceExtractor>();
builder.Services.AddSingleton<ISchemaBundler, AvroSchemaBundler>();

builder.Services.AddSingleton<ISchemaCanonicalizer, ProtoSchemaCanonicalizer>();
builder.Services.AddSingleton<ICompatibilityChecker, ProtoSchemaCompatibilityChecker>();
builder.Services.AddSingleton<ISchemaReferenceExtractor, ProtoSchemaReferenceExtractor>();
builder.Services.AddSingleton<ISchemaBundler, ProtoSchemaBundler>();

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi("v1");

builder.Services.AddHealthChecks()
    .AddDbContextCheck<ConcordatDbContext>("database");

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

// Before routing runs anything: every endpoint filter and handler reads the caller this sets,
// and a request that reached a handler without one would be a request nobody authorised.
app.UseMiddleware<AuthenticationMiddleware>();

app.MapOpenApi();

app.MapEnvironmentEndpoints();
app.MapContractEndpoints();
app.MapGovernanceEndpoints();
app.MapNotificationEndpoints();
app.MapIdentityEndpoints();
app.MapSubjectEndpoints();
app.MapSchemaEndpoints();
app.MapBootstrapEndpoint();

// Liveness answers "is the process up", readiness answers "can it serve". Conflating them
// makes an orchestrator restart a healthy process during a database blip.
app.MapHealthChecks("/health/live", new()
{
    Predicate = _ => false,
}).WithTags("Health");

app.MapHealthChecks("/health/ready").WithTags("Health");

await app.RunAsync().ConfigureAwait(false);

/// <summary>Exposed so the integration tests can host the API in-process.</summary>
public partial class Program;
