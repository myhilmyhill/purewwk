using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Purewwk.Plugin;

/// <summary>
/// Unified base model representing a media entity.
/// </summary>
public abstract class MediaItem
{
    public required string Id { get; init; }
    public required string Parent { get; init; }
    public string IdSuffix { get; init; } = string.Empty; // Used during indexing
    public required string Title { get; init; }
    public required string Path { get; init; } // Source physical path or plugin-handled URI
    
    public abstract bool IsDir { get; }
}

/// <summary>
/// Represents a container (directory).
/// </summary>
public class MediaFolder : MediaItem
{
    public override bool IsDir => true;
}

    /// <summary>
    /// Represents a playable file.
    /// </summary>
    public class MediaFile : MediaItem
    {
        public override bool IsDir => false;
        
        public double? StartTime { get; init; }
        public double? Duration { get; init; }

        private string Extension => System.IO.Path.GetExtension(Path).ToLowerInvariant();
        public string MimeType => Player.GetMimeType(Extension);
        public string PlayerType => Player.GetPlayerType(Extension);

        /// <summary>
        /// The plugin instance responsible for playing this item.
        /// This is populated at runtime by the service layer.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public required IPlayablePlugin Player { get; init; }

        /// <summary>
        /// Triggers playback of this item using its assigned player.
        /// </summary>
        public Task<PlaybackResponse> PlayAsync(Dictionary<string, string> queryParams)
        {
            return Player.HandlePlaybackAsync(this, queryParams);
        }
    }
