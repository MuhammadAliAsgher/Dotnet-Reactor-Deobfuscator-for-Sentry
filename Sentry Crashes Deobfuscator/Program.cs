using Prometheus;
using Sentry_Crashes_Deobfuscator.Observability;
using Sentry_Crashes_Deobfuscator.Repositories;
using Sentry_Crashes_Deobfuscator.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddHttpClient();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Configure logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Information);

// Multi-tenant mapping resolution (Project.md §3): one .nrmap per {product}:{release}
// tenant key, resolved on demand and cached per-instance (ADR-001, Option A -- see
// docs/adr/0001-nrmap-cache-strategy.md). Nothing is eagerly loaded at startup anymore;
// a tenant's mapping is loaded the first time an event for it arrives.
builder.Services.AddSingleton<INrmapBlobStore>(sp =>
{
    var root = sp.GetRequiredService<IConfiguration>()["Sentry:MappingPath"];
    if (string.IsNullOrWhiteSpace(root))
    {
        throw new InvalidOperationException("Sentry:MappingPath missing (now used as the .nrmap blob-store root, see README)");
    }
    return new LocalDiskNrmapBlobStore(root);
});
builder.Services.AddSingleton<SymbolResolverMetrics>();
builder.Services.AddSingleton<ITenantMappingCache, TenantMappingCache>();
builder.Services.AddSingleton<IDeobfuscationService, SentryDeobfuscationService>();

var app = builder.Build();

var logger = app.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("Sentry Deobfuscation Proxy Starting (multi-tenant .nrmap resolution)");
logger.LogInformation(".nrmap blob-store root: {Root}", builder.Configuration["Sentry:MappingPath"]);

// Configure middleware
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpMetrics();   // prometheus-net: request-level metrics
app.MapControllers();
app.MapMetrics();       // prometheus-net: exposes GET /metrics (Project.md §3.5)

logger.LogInformation("Proxy is ready. Listening for Sentry envelopes...");

app.Run();
