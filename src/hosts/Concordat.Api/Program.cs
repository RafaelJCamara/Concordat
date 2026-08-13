using Concordat.Api;
using Concordat.Application;
using Concordat.Application.Abstractions;
using Concordat.Formats.Abstractions;
using Concordat.Formats.Avro;
using Concordat.Formats.Json;
using Concordat.Formats.Protobuf;
using Concordat.Infrastructure;
using Concordat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString =
    builder.Configuration.GetConnectionString("Concordat")
    ?? "Host=localhost;Database=concordat;Username=postgres;Password=postgres";

builder.Services.AddConcordatApplication();
builder.Services.AddConcordatPersistence(connectionString);

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

builder.Services.AddSingleton<IEnvironmentResolver, DerivedEnvironmentResolver>();

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi("v1");

builder.Services.AddHealthChecks()
    .AddDbContextCheck<ConcordatDbContext>("database");

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapOpenApi();

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
