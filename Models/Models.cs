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
    public DirectoryResponse? Directory { get; set; }
    [JsonPropertyName("randomSongs")]
    public RandomSongsResponse? RandomSongs { get; set; }
}

public class DirectoryResponse
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }
    [JsonPropertyName("parent")]
    public required string? Parent { get; set; }
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
    public required bool IsDir { get; set; }
    [JsonPropertyName("title")]
    public required string Title { get; set; }
    [JsonPropertyName("path")]
    public required string Path { get; set; }
}

public class ErrorResponse
{
    [JsonPropertyName("code")]
    public required int Code { get; set; }
    [JsonPropertyName("message")]
    public required string Message { get; set; }
}

public class RandomSongsResponse
{
    [JsonPropertyName("song")]
    public List<SongResponse> Song { get; set; } = new();
}

public class SongResponse
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }
    [JsonPropertyName("parent")]
    public string? Parent { get; set; }
    [JsonPropertyName("title")]
    public required string Title { get; set; }
    [JsonPropertyName("isDir")]
    public bool IsDir { get; set; } = false;
    [JsonPropertyName("isVideo")]
    public bool IsVideo { get; set; } = false;
    [JsonPropertyName("type")]
    public string Type { get; set; } = "music";
    [JsonPropertyName("albumId")]
    public string? AlbumId { get; set; }
    [JsonPropertyName("album")]
    public string? Album { get; set; }
    [JsonPropertyName("artistId")]
    public string? ArtistId { get; set; }
    [JsonPropertyName("artist")]
    public string? Artist { get; set; }
    [JsonPropertyName("coverArt")]
    public string? CoverArt { get; set; }
    [JsonPropertyName("duration")]
    public int? Duration { get; set; }
    [JsonPropertyName("bitRate")]
    public int? BitRate { get; set; }
    [JsonPropertyName("bitDepth")]
    public int? BitDepth { get; set; }
    [JsonPropertyName("samplingRate")]
    public int? SamplingRate { get; set; }
    [JsonPropertyName("channelCount")]
    public int? ChannelCount { get; set; }
    [JsonPropertyName("userRating")]
    public int? UserRating { get; set; }
    [JsonPropertyName("averageRating")]
    public double? AverageRating { get; set; }
    [JsonPropertyName("track")]
    public int? Track { get; set; }
    [JsonPropertyName("year")]
    public int? Year { get; set; }
    [JsonPropertyName("genre")]
    public string? Genre { get; set; }
    [JsonPropertyName("size")]
    public long? Size { get; set; }
    [JsonPropertyName("discNumber")]
    public int? DiscNumber { get; set; }
    [JsonPropertyName("suffix")]
    public string? Suffix { get; set; }
    [JsonPropertyName("contentType")]
    public string? ContentType { get; set; }
    [JsonPropertyName("path")]
    public string? Path { get; set; }
}
