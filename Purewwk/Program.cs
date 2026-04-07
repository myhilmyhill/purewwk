using Purewwk.Plugin.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Purewwk.Services;

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

PluginManager.LoadPlugins(builder.Services, builder.Configuration);
builder.Services.AddSingleton<PluginManager>();

var musicDir = builder.Configuration["MusicDirectory"];

if (string.IsNullOrEmpty(musicDir))
{
    throw new InvalidOperationException("MusicDirectory is not configured.");
}

var app = builder.Build();

var luceneService = app.Services.GetRequiredService<ILuceneService>();
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




app.UseHttpLogging();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapControllers();

app.Run();



