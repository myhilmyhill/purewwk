# PureWWK - Music Server

A music server application with Lucene-powered search and HLS streaming capabilities.

## Features

- Lucene-based music file indexing and search
- HLS (HTTP Live Streaming) support for audio files
- File system watching for automatic index updates
- Command-line tools for maintenance operations

## Configuration

The application is configured via `appsettings.json` or environment variables:

- `MusicDirectory`: Path to your music collection (required)
- `WorkingDirectory`: Path for index and cache storage (optional, defaults to application directory)
- `HlsCache:MaxSize`: Maximum number of cached HLS playlists (default: 100)
- `HlsCache:MaxAgeMinutes`: Cache expiration time in minutes (default: 60)
- `FileWatcher:Enabled`: Enable/disable automatic file system watching (default: true)

## Running the Application

### Normal Web Server Mode

```bash
dotnet run
```

Or with Docker:

```bash
docker run -v /path/to/music:/music -e MusicDirectory=/music -p 8080:8080 purewwk
```

### Command-Line Tools

The application includes command-line tools for maintenance operations:

#### Rebuild Index

Completely rebuilds the Lucene search index from scratch:

```bash
# Using dotnet
dotnet run -- --rebuild-index

# Using the helper script
./purewwk-cli.sh rebuild-index

# Using Docker
docker run -v /path/to/music:/music -e MusicDirectory=/music purewwk --rebuild-index
```

This command:
- Clears the existing index
- Scans the music directory
- Indexes all music files (mp3, flac, wav, ogg, m4a, aac, wma)
- Creates a new searchable index

#### Clear Cache

Removes all HLS cache files and directories:

```bash
# Using dotnet
dotnet run -- --clear-cache

# Using the helper script
./purewwk-cli.sh clear-cache

# Using Docker
docker run -v /app/hls_segments:/app/hls_segments purewwk --clear-cache
```

This command:
- Deletes all cached HLS segment files
- Frees up disk space
- Forces regeneration of HLS playlists on next request

#### Help

Display usage information:

```bash
# Using dotnet
dotnet run -- --help

# Using the helper script
./purewwk-cli.sh help

# Using Docker
docker run purewwk --help
```

## Docker Usage Examples

### Run the web server

```bash
docker run -d \
  -v /path/to/music:/music \
  -v /path/to/index:/app/music_index \
  -v /path/to/cache:/app/hls_segments \
  -e MusicDirectory=/music \
  -p 8080:8080 \
  purewwk
```

### Rebuild index in existing container

```bash
# One-time rebuild
docker run --rm \
  -v /path/to/music:/music \
  -v /path/to/index:/app/music_index \
  -e MusicDirectory=/music \
  purewwk --rebuild-index

# Or using docker-compose
docker-compose run --rm app --rebuild-index
```

### Clear cache in existing container

```bash
# One-time cache clear
docker run --rm \
  -v /path/to/cache:/app/hls_segments \
  purewwk --clear-cache

# Or using docker-compose
docker-compose run --rm app --clear-cache
```

## Development

### Build

```bash
dotnet build
```

### Run locally

```bash
export MusicDirectory=/path/to/music
dotnet run
```

### Run with specific command

```bash
export MusicDirectory=/path/to/music
dotnet run -- --rebuild-index
```

## API Endpoints

- `GET /rest/getMusicDirectory.view?id={id}` - Get directory contents
- `GET /rest/hls.m3u8?id={id}&bitRate={bitrate}` - Get HLS playlist
- `GET /rest/download.view?id={id}` - Download file

## Notes

- The index is automatically created on first startup if it doesn't exist
- File system changes are automatically indexed when FileWatcher is enabled
- HLS cache is cleaned up periodically based on age and size limits
- All CLI commands require the `MusicDirectory` configuration to be set
