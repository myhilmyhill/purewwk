
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using repos.Services;

// Check for CLI commands
if (args.Length > 0)
{
    var command = args[0].ToLower();
    
    // Create a minimal configuration to access services
    var configBuilder = new ConfigurationBuilder()
        .SetBasePath(Directory.GetCurrentDirectory())
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
        .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json", optional: true)
        .AddEnvironmentVariables();
    var configuration = configBuilder.Build();
    
    var cliMusicDir = configuration["MusicDirectory"];
    if (string.IsNullOrEmpty(cliMusicDir))
    {
        Console.WriteLine("Error: MusicDirectory is not configured.");
        return 1;
    }
    
    switch (command)
    {
        case "--rebuild-index":
        case "rebuild-index":
            Console.WriteLine("Rebuilding Lucene index...");
            using (var cliLuceneService = new LuceneService(configuration))
            {
                if (Directory.Exists(cliMusicDir))
                {
                    cliLuceneService.IndexDirectory(cliMusicDir);
                    Console.WriteLine("Index rebuild completed successfully.");
                }
                else
                {
                    Console.WriteLine($"Error: Music directory not found: {cliMusicDir}");
                    return 1;
                }
            }
            return 0;
            
        case "--clear-cache":
        case "clear-cache":
            Console.WriteLine("Clearing HLS cache...");
            var workingDir = configuration["WorkingDirectory"];
            var baseDir = string.IsNullOrEmpty(workingDir) ? AppContext.BaseDirectory : workingDir;
            var hlsCacheDir = Path.Combine(baseDir, "hls_segments");
            
            if (Directory.Exists(hlsCacheDir))
            {
                try
                {
                    var directories = Directory.GetDirectories(hlsCacheDir);
                    foreach (var dir in directories)
                    {
                        Directory.Delete(dir, true);
                    }
                    var files = Directory.GetFiles(hlsCacheDir);
                    foreach (var file in files)
                    {
                        File.Delete(file);
                    }
                    Console.WriteLine($"Cleared {directories.Length + files.Length} items from HLS cache.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error clearing cache: {ex.Message}");
                    return 1;
                }
            }
            else
            {
                Console.WriteLine("HLS cache directory does not exist.");
            }
            return 0;
            
        case "--help":
        case "help":
        case "-h":
            Console.WriteLine("PureWWK - Music Server");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  repos                    Start the web server (default)");
            Console.WriteLine("  repos --rebuild-index    Rebuild the Lucene search index");
            Console.WriteLine("  repos --clear-cache      Clear the HLS cache");
            Console.WriteLine("  repos --help             Show this help message");
            return 0;
            
        default:
            Console.WriteLine($"Unknown command: {command}");
            Console.WriteLine("Use --help to see available commands.");
            return 1;
    }
}

// Normal web application startup
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddHttpContextAccessor();

builder.Services.AddSingleton<LuceneService>();
builder.Services.AddSingleton<FileWatcherService>();
builder.Services.AddSingleton<IHlsCacheStorage>(provider =>
{
    var config = provider.GetRequiredService<IConfiguration>();
    var maxSize = config.GetValue<int>("HlsCache:MaxSize", 100);
    var maxAgeMinutes = config.GetValue<int>("HlsCache:MaxAgeMinutes", 60);
    return new HlsCacheStorage(maxSize, TimeSpan.FromMinutes(maxAgeMinutes));
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

_ = Task.Run(() =>
{
    if (Directory.Exists(musicDir))
    {
        if (!luceneService.IsIndexValid())
        {
            Console.WriteLine("Index not found or invalid. Creating new index...");
            luceneService.IndexDirectory(musicDir);
        }
        else
        {
            Console.WriteLine("Valid index found. Skipping indexing.");
        }
    }
});

// Ensure FileWatcherService is initialized (it starts automatically in constructor)
_ = fileWatcherService;

app.MapControllers();

app.Run();

return 0;
