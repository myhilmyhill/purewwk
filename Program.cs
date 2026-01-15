
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using repos.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddHttpContextAccessor();

builder.Services.AddSingleton<LuceneService>();
builder.Services.AddSingleton<FileWatcherService>();
builder.Services.AddSingleton<IHlsCacheStorage>(provider =>
{
    var config = provider.GetRequiredService<IConfiguration>();
    var logger = provider.GetRequiredService<ILogger<HlsCacheStorage>>();
    var maxSize = config.GetValue<int>("HlsCache:MaxSize", 100);
    var maxAgeMinutes = config.GetValue<int>("HlsCache:MaxAgeMinutes", 60);
    return new HlsCacheStorage(logger, maxSize, TimeSpan.FromMinutes(maxAgeMinutes));
});
builder.Services.AddSingleton<HlsService>();

var musicDir = builder.Configuration["MusicDirectory"];
if (string.IsNullOrEmpty(musicDir))
{
    throw new InvalidOperationException("MusicDirectory is not configured.");
}

var app = builder.Build();

// Initialize Lucene index
var luceneService = app.Services.GetRequiredService<LuceneService>();
var fileWatcherService = app.Services.GetRequiredService<FileWatcherService>();
var hlsService = app.Services.GetRequiredService<HlsService>();
var logger = app.Services.GetRequiredService<ILogger<Program>>();

_ = Task.Run(() =>
{
    if (Directory.Exists(musicDir))
    {
        if (!luceneService.IsIndexValid())
        {
            logger.LogInformation("Index not found or invalid. Creating new index...");
            luceneService.IndexDirectory(musicDir);
        }
        else
        {
            logger.LogInformation("Valid index found. Skipping indexing.");
        }
    }
});

// Ensure FileWatcherService is initialized (it starts automatically in constructor)
_ = fileWatcherService;

app.MapControllers();

app.Run();

public partial class Program { }
