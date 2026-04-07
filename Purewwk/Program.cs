using Purewwk.Models;
using Purewwk.Services;
using Purewwk.Plugin;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpLogging(logging =>
{
    logging.LoggingFields = Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.RequestMethod |
                            Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.RequestPath |
                            Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.RequestQuery |
                            Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.ResponseStatusCode;
});

builder.Services.AddSingleton<IFileSystem, RealFileSystem>();
builder.Services.AddSingleton<ILuceneService, LuceneService>();
builder.Services.AddSingleton<IIndexUpdater>(sp => sp.GetRequiredService<ILuceneService>());

builder.Services.AddOptions<PurewwkConfig>()
    .Bind(builder.Configuration)
    .ValidateDataAnnotations()
    .ValidateOnStart();

PluginManager.LoadPlugins(builder.Services, builder.Configuration);
builder.Services.AddSingleton<PluginManager>();

// var musicDir will be retrieved from Options later if needed, 
// but we still need it for the initial scan Task.Run below.

var app = builder.Build();

var luceneService = app.Services.GetRequiredService<ILuceneService>();
var logger = app.Services.GetRequiredService<ILogger<Program>>();
var config = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<PurewwkConfig>>().Value;

_ = Task.Run(() =>
{
    if (Directory.Exists(config.MusicDirectory))
    {
        logger.LogInformation("Starting library scan...");
        luceneService.IndexDirectory(config.MusicDirectory);
        logger.LogInformation("Library scan completed.");
    }
});

app.UseHttpLogging();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();
app.Run();
