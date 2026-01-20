
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using repos.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddHttpContextAccessor();

builder.Services.AddSingleton<IFileSystem, RealFileSystem>();
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
builder.Services.AddSingleton<CueService>();
builder.Services.AddSingleton<CueFolderService>();


// Support Shift_JIS and other encodings
System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

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
        logger.LogInformation("Starting library scan...");
        luceneService.IndexDirectory(musicDir);
        logger.LogInformation("Library scan completed.");
    }
});

// Ensure FileWatcherService is initialized (it starts automatically in constructor)
_ = fileWatcherService;


if (app.Environment.IsDevelopment())
{
    app.Use(async (context, next) =>
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("Incoming Request: {Method} {Path}{QueryString}", 
            context.Request.Method, 
            context.Request.Path, 
            context.Request.QueryString);
            
        await next();
        
        logger.LogInformation("Response: {StatusCode}", context.Response.StatusCode);
    });
}

app.MapControllers();

app.Run();
