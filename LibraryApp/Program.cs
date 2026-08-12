using System.ClientModel;
using System.ClientModel.Primitives;
using LibraryApp.Api;
using LibraryApp.Api.Caching;
using LibraryApp.Application;
using LibraryApp.Application.Abstractions;
using LibraryApp.Infrastructure.Gemini;
using LibraryApp.Infrastructure.OpenLibrary;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<SearchUnavailableExceptionHandler>();
builder.Services.AddHealthChecks();

builder.Services.AddOptions<GeminiOptions>()
    .Bind(builder.Configuration.GetSection(GeminiOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.ApiKey),
        "Gemini:ApiKey is not configured. Set it with: dotnet user-secrets set \"Gemini:ApiKey\" \"<key>\"")
    .ValidateOnStart();

builder.Services.AddOptions<OpenLibraryOptions>()
    .Bind(builder.Configuration.GetSection(OpenLibraryOptions.SectionName));

var openLibraryOptions = builder.Configuration
    .GetSection(OpenLibraryOptions.SectionName)
    .Get<OpenLibraryOptions>() ?? new OpenLibraryOptions();

// --- Output caching -------------------------------------------------------------------
// Order matters: NormalizedQueryCachePolicy has to come last so it can override the QueryKeys="*"
// that the built-in default policy sets. See that class for why. Do not add SetVaryByQuery("q")
// here either — it would put the verbatim query back into the key and defeat the normalization.
//
// Single-instance scope: this uses the default in-memory store, so the cache is per-process,
// lost on restart, and bounded at 100 MB. The 7-day expiry is therefore "7 days or until the
// process recycles". Fine as it stands; in production this would be a Redis-backed
// IOutputCacheStore so entries are durable and shared by every instance. That swap is a DI
// registration only — the key derivation in QueryNormalizer is independent of where it is stored.
//
// Worth knowing before making that change: AllowLocking is per-process. A distributed store does
// not give you a distributed lock, so N instances can still each make one upstream call for the
// same cold key. Closing that needs an explicit distributed lock, not just Redis.
builder.Services.AddOutputCache(opts =>
{
    opts.AddPolicy("BookSearchCache", policy => policy
        .Expire(TimeSpan.FromDays(7))
        .AddPolicy<NormalizedQueryCachePolicy>());
});

// --- Gemini ---------------------------------------------------------------------------
builder.Services.AddSingleton<IChatClient>(sp =>
{
    var options = sp.GetRequiredService<IOptions<GeminiOptions>>().Value;

    var clientOptions = new OpenAIClientOptions
    {
        Endpoint = new Uri(options.Endpoint),
        // One retry with exponential backoff, then fail — no silent degradation.
        RetryPolicy = new ClientRetryPolicy(maxRetries: 1),
        NetworkTimeout = TimeSpan.FromSeconds(10)
    };

    return new OpenAIClient(new ApiKeyCredential(options.ApiKey), clientOptions)
        .GetChatClient(options.Model)
        .AsIChatClient();
});

builder.Services.AddScoped<ISearchIntentParser, GeminiSearchIntentParser>();

// --- OpenLibrary ----------------------------------------------------------------------
builder.Services.AddHttpClient<IBookCatalog, OpenLibraryBookCatalog>(client =>
    {
        client.BaseAddress = new Uri(openLibraryOptions.BaseAddress);
    })
    .AddStandardResilienceHandler(resilience =>
    {
        // OpenLibrary answers a well-formed fielded query in ~3.5s but has been measured at ~18s on
        // awkward ones. A bounded per-attempt timeout plus a single retry keeps the worst case finite.
        resilience.AttemptTimeout.Timeout = openLibraryOptions.Timeout;
        resilience.Retry.MaxRetryAttempts = 1;
        resilience.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
        resilience.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
    });

builder.Services.AddScoped<BookSearchOrchestrator>();

var app = builder.Build();

// --- Behind a TLS-terminating proxy --------------------------------------------------
// Render (and Cloud Run, and App Service) terminate TLS at their edge and forward plain HTTP, so
// the app sees an insecure request no matter what the browser did. X-Forwarded-Proto carries what
// the client actually used, which keeps Request.Scheme honest for logging and link generation.
//
// The known-proxy lists have to be cleared: the middleware trusts only loopback by default, and the
// platform's proxy is neither loopback nor at a stable address. Safe here because the proxy is the
// only route to this process — nothing else can reach it to spoof the header.
var forwardedHeaders = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor
};

forwardedHeaders.KnownIPNetworks.Clear();
forwardedHeaders.KnownProxies.Clear();

app.UseForwardedHeaders(forwardedHeaders);

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Redirecting to HTTPS is the hosting platform's job wherever one terminates TLS, and doing it here
// as well actively breaks the deployment: the platform's health checks reach the container directly
// over plain HTTP with no X-Forwarded-Proto, so this middleware answers them 307 instead of 200. The
// instance is marked unhealthy, pulled from routing, and every request to the public URL comes back
// as the edge's own 404. Render already redirects http to https at that edge, so nothing is lost.
//
// Locally, where Kestrel serves HTTPS itself and there is no edge, the redirect is worth having.
if (!app.Configuration.GetValue<bool>("BehindTlsProxy"))
{
    app.UseHttpsRedirection();
}

// Serves the search UI from wwwroot. UseDefaultFiles only rewrites "/" to "/index.html" — it serves
// nothing itself, so it has to come first. Both must precede routing, and because the page is served
// from the same origin as the API, no CORS configuration is needed.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseOutputCache();
app.MapControllers();

// Liveness only, and deliberately shallow: this is what the host polls, so it must not touch Gemini
// or OpenLibrary. A check that verified them would bill an AI call every time the platform pinged
// it, and output caching does not sit in front of this route. "The process is up" is the question
// being asked — that the Gemini key exists was already settled at startup by ValidateOnStart.
app.MapHealthChecks("/health");

app.Run();
