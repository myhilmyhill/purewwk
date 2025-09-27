using System.Text.Json.Serialization;

namespace repos.Models;

public class SubsonicResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "ok";
    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.16.1";
    [JsonPropertyName("type")]
    public string Type { get; set; } = "AwesomeServerName";
    [JsonPropertyName("serverVersion")]
    public string ServerVersion { get; set; } = "0.1.3 (tag)";
    [JsonPropertyName("openSubsonic")]
    public bool OpenSubsonic { get; set; } = true;
    [JsonPropertyName("directory")]
    public required DirectoryResponse Directory { get; set; }
}

public class DirectoryResponse
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }
    [JsonPropertyName("parent")]
    public required string Parent { get; set; }
    [JsonPropertyName("name")]
    public required string Name { get; set; }
    [JsonPropertyName("child")]
    public required List<ChildResponse> Child { get; set; }
}

public class ChildResponse
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }
    [JsonPropertyName("parent")]
    public required string Parent { get; set; }
    [JsonPropertyName("isDir")]
    public bool IsDir { get; set; }
    [JsonPropertyName("title")]
    public required string Title { get; set; }
    [JsonPropertyName("path")]
    public required string Path { get; set; }
}
