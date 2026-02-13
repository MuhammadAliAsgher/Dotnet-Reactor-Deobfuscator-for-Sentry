using Sentry_Crashes_Deobfuscator.Repositories;
using Sentry_Crashes_Deobfuscator.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddHttpClient();

// Configure logging
builder.Logging.ClearProviders(); 
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Information);

// Register Repository Pattern dependencies
builder.Services.AddSingleton<IMappingRepository, NRMapRepository>();
builder.Services.AddSingleton<IDeobfuscationService, SentryDeobfuscationService>();

var app = builder.Build();

// Initialize mapping repository
var mappingRepository = app.Services.GetRequiredService<IMappingRepository>();
var mappingPath = builder.Configuration["Sentry:MappingPath"];
var logger = app.Services.GetRequiredService<ILogger<Program>>();

logger.LogInformation("Sentry Deobfuscation Proxy Starting");

if (string.IsNullOrEmpty(mappingPath))
{
    logger.LogError("CRITICAL: Sentry:MappingPath not configured!");
}
else
{
    logger.LogInformation("Loading mappings from: {Path}", mappingPath);
    mappingRepository.LoadMappings(mappingPath);

    if (!mappingRepository.HasMappings)
    {
        logger.LogError("FAILED: No mappings were loaded from: {Path}", mappingPath);
    }
    else
    {
        logger.LogInformation("Successfully loaded {Count} mappings", mappingRepository.TotalMappings);
    }
}

// Configure middleware
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

app.UseAuthorization();
app.MapControllers();

logger.LogInformation("Proxy is ready. Listening for Sentry envelopes...");

app.Run();