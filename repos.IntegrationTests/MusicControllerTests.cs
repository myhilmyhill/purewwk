
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;
using repos.Models;

namespace repos.IntegrationTests;

public class MusicControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public MusicControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetTest_ReturnsOk()
    {
        // Act
        var response = await _client.GetAsync("/rest/test");

        // Assert
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        Assert.Equal("Test endpoint is working", content);
    }

    [Fact]
    public async Task GetMusicDirectory_ReturnsDirectoryListing()
    {
        // Arrange
        // Create a dummy music file in the test directory
        var musicDir = _factory.MusicDirectory;
        var artistDir = Path.Combine(musicDir, "TestArtist");
        var albumDir = Path.Combine(artistDir, "TestAlbum");
        Directory.CreateDirectory(albumDir);

        var songPath = Path.Combine(albumDir, "01 - TestSong.mp3");
        await File.WriteAllBytesAsync(songPath, new byte[100]); // Dummy content

        // Wait a bit for the indexer to pick it up (LuceneService in Program.cs runs on Task.Run)
        // Since we don't have a direct way to trigger indexing or wait for it, we'll use a retry loop.
        // In a real scenario, we might want to expose a way to trigger indexing or wait for it.

        // However, Program.cs only indexes on startup if index is invalid.
        // FileWatcherService should pick up changes.
        // Let's verify if FileWatcherService is active. It is initialized in Program.cs.

        // We might need to wait for FileWatcher to detect the change and LuceneService to update the index.

        var maxRetries = 20;
        var delay = 500;
        bool found = false;

        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                // Act
                var response = await _client.GetAsync("/rest/getMusicDirectory.view?id=/TestArtist");
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
                    if (content != null && content.ContainsKey("subsonic-response"))
                    {
                        var json = JsonSerializer.Serialize(content["subsonic-response"]);
                        var subsonicResponse = JsonSerializer.Deserialize<SubsonicResponse>(json);

                        if (subsonicResponse?.Directory?.Child?.Any(c => c.Title == "TestAlbum") == true)
                        {
                            found = true;
                            break;
                        }
                    }
                }
            }
            catch
            {
                // Ignore transient errors
            }

            await Task.Delay(delay);
        }

        // We are actually testing if we can list the root first to find TestArtist
        // But since we created TestArtist folder, let's try to list root.

        found = false;
        for (int i = 0; i < maxRetries; i++)
        {
            var response = await _client.GetAsync("/rest/getMusicDirectory.view?id=/");
             if (response.IsSuccessStatusCode)
            {
                var stringContent = await response.Content.ReadAsStringAsync();
                if (stringContent.Contains("TestArtist"))
                {
                    found = true;
                    break;
                }
            }
             await Task.Delay(delay);
        }

        Assert.True(found, "Timed out waiting for file indexing.");

        // Now verifying the child directory
        var responseArtist = await _client.GetAsync("/rest/getMusicDirectory.view?id=/TestArtist");
        responseArtist.EnsureSuccessStatusCode();
        var stringContentArtist = await responseArtist.Content.ReadAsStringAsync();
        Assert.Contains("TestAlbum", stringContentArtist);
    }
}
