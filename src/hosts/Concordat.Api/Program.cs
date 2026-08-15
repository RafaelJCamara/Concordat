using System.Threading.RateLimiting;
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
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

// Must be the first statement: .NET reads the process-wide regex match timeout exactly once,
// the first time anything touches Regex, so this is the one place that can reliably win that
// race. See RegexSafety's remarks for why nothing downstream depends on it having won.
RegexSafety.ApplyProcessWideDefault();

var builder = WebApplication.CreateBuilder(args);

// Kestrel's own default is 30 MB, which is generous enough to let a slow or hostile request
// buffer a lot of memory before any of this host's own checks -- Schema.MaxBodyBytes (512 KiB)
// or NJsonSchemaPayloadValidator.MaxPayloadBytes (10 MB) -- ever run. Every route here is JSON:
// schemas, payloads-for-checking, and small governance bodies. Nothing legitimate needs more
// than a few MB of request body.
builder.WebHost.ConfigureKestrel(kestrel =>
    kestrel.Limits.MaxRequestBodySize = 4 * 1024 * 1024);

// The Development fallback exists so a fresh checkout runs with zero configuration, matching
// the compose loop's own zero-config promise. Outside Development, guessing a connection
// string is worse than refusing to start: a typo'd config-key name would otherwise boot
// silently against localhost/postgres/postgres -- possibly a real, wrong database -- instead
// of failing where an operator is watching. The migrator already fails fast the same way.
var connectionString = builder.Configuration.GetConnectionString("Concordat");

if (string.IsNullOrWhiteSpace(connectionString))
{
    // HostFactoryResolver -- the mechanism behind both `dotnet ef` and this project's own
    // OpenAPI-on-build step -- invokes Main with exactly one argument, "--applicationName=…",
    // to build the DI container without running it. It never opens a connection, so it does
    // not need a real one; refusing here would fail every build. A real run never receives
    // this argument.
    var isDesignTimeToolInvocation =
        args is [var only] && only.StartsWith("--applicationName=", StringComparison.Ordinal);

    if (!builder.Environment.IsDevelopment() && !isDesignTimeToolInvocation)
    {
        throw new InvalidOperationException(
            "ConnectionStrings:Concordat is not configured. Refusing to guess one outside " +
            "Development.");
    }

    connectionString = "Host=localhost;Database=concordat;Username=postgres;Password=postgres";
}

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

// Says out loud, repeatedly, that an unclaimed instance answers everyone as an owner
// (decision 27). A line at boot is as invisible as no line by the time it matters.
builder.Services.AddHostedService<UnclaimedInstanceWarning>();
builder.Services.AddScoped<CallerContext>();
builder.Services.AddScoped<ICallerContext>(p => p.GetRequiredService<CallerContext>());
builder.Services.AddScoped<CallerResolver>();
builder.Services.AddSingleton<IBootstrapPolicy, ConfiguredBootstrapPolicy>();

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

// Registered before AddProblemDetails' own fallback handler runs, so a race the database
// caught (a unique-constraint hit, an xmin mismatch) lands as 409 rather than an unhandled-
// exception 500. See DbConflictExceptionHandler for why nothing upstream catches these itself.
builder.Services.AddExceptionHandler<DbConflictExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi("v1");

builder.Services.AddHealthChecks()
    .AddDbContextCheck<ConcordatDbContext>("database");

// Slows credential stuffing and account-creation abuse on /v1/auth/{signin,bootstrap,signup}
// without materially affecting a legitimate user who mistypes a password a few times. PBKDF2
// already makes each guess costly (Authentication.cs); this bounds how many guesses a caller
// gets to make in the first place. Partitioned by IP, not by account -- an account-keyed limit
// would let an attacker lock a real user out just by naming them in enough failed attempts.
//
// DisableRateLimiting exists for the integration suite, not production: an in-memory TestServer
// never populates Connection.RemoteIpAddress, so every request in the whole test run would
// otherwise share one "unknown" partition and the many unrelated tests that incidentally sign
// in or sign up as setup would start failing on 429 rather than the thing they actually assert.
var disableRateLimiting = builder.Configuration.GetValue<bool>("Concordat:DisableRateLimiting");

builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    limiter.AddPolicy("auth-attempt", httpContext => disableRateLimiting
        ? RateLimitPartition.GetNoLimiter("disabled")
        : RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(5),
                QueueLimit = 0,
            }));
});

// X-Forwarded-Proto is what lets SessionCookie's Secure flag see the client's real scheme:
// every real deployment terminates TLS at a load balancer or Container Apps' own ingress and
// speaks plain HTTP to this process (see ASPNETCORE_URLS in deploy/azure/main.bicep), so
// Request.IsHttps would otherwise read false even for a browser on HTTPS and ship the session
// cookie without Secure. KnownNetworks/KnownProxies are cleared because that one hop's address
// is not knowable in advance in a platform-managed ingress -- the documented trade for that
// topology. A deployment adding an untrusted network path in front of the real proxy must
// populate these instead of clearing them.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// No origin configured means every cross-origin request is refused -- same-origin calls (the
// common case, including local dev through proxy.conf.json) are never subject to CORS at all,
// so this only matters for a deployment that serves the web app from a different origin than
// the API, which must set Concordat:WebOrigin to name it.
var webOrigin = builder.Configuration["Concordat:WebOrigin"];

builder.Services.AddCors(cors => cors.AddPolicy("web", policy =>
{
    if (!string.IsNullOrWhiteSpace(webOrigin))
    {
        policy.WithOrigins(webOrigin).AllowCredentials().AllowAnyHeader().AllowAnyMethod();
    }
}));

var app = builder.Build();

// Ahead of everything: forwarded-scheme has to be resolved before any middleware reads
// Request.IsHttps or Request.Scheme, which includes UseHsts below and SessionCookie later.
app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    // A response header, not a redirect -- TLS termination and enforcement belong to the
    // ingress in front of this process (see the ForwardedHeaders comment above), and a
    // UseHttpsRedirection() here would break the plain-HTTP health probes Container Apps and
    // compose both call directly against the container.
    app.UseHsts();
}

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["Referrer-Policy"] = "no-referrer";

    // This is a JSON API: it renders no HTML of its own (MapOpenApi returns the document, not
    // a viewer), so the tightest policy possible is also the correct one -- there is nothing
    // here for a script or a frame to legitimately do.
    headers["Content-Security-Policy"] =
        "default-src 'none'; frame-ancestors 'none'; base-uri 'none'";

    await next(context).ConfigureAwait(false);
});

app.UseCors("web");
app.UseRateLimiter();

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
app.MapBillingEndpoints();
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
