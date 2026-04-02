
namespace repos.IntegrationTests;

using Xunit;
using Microsoft.Extensions.Configuration;

public class CustomWebApplicationFactory : Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _testMusicDir;

    public CustomWebApplicationFactory()
    {
        _testMusicDir = Path.Combine(Path.GetTempPath(), "repos_test_music_" + Guid.NewGuid());
        Directory.CreateDirectory(_testMusicDir);
    }

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((context, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MusicDirectory"] = _testMusicDir
            });
        });
    }

    public string MusicDirectory => _testMusicDir;

    public Task InitializeAsync()
    {
        return Task.CompletedTask;
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        if (Directory.Exists(_testMusicDir))
        {
            try
            {
                Directory.Delete(_testMusicDir, true);
            }
            catch
            {
                // Ignore errors during cleanup
            }
        }
        await base.DisposeAsync();
    }
}
