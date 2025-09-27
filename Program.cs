
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using repos.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();

// Register LuceneService
builder.Services.AddSingleton<LuceneService>();

// 初回のみインデックス作成（musicディレクトリを指定）
var musicDir = builder.Configuration["MusicDirectory"];
if (string.IsNullOrEmpty(musicDir))
{
    throw new InvalidOperationException("MusicDirectory is not configured.");
}

var app = builder.Build();

// Initialize Lucene index
var luceneService = app.Services.GetRequiredService<LuceneService>();

if (Directory.Exists(musicDir))
{
    luceneService.IndexDirectory(musicDir);
}

app.MapControllers();

app.Run();
