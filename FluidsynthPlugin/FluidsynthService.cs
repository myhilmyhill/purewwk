using System.Buffers.Binary;
using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PureWwk.Plugin.Abstractions;

namespace PureWwk.Plugins.Fluidsynth;

public class FluidsynthService
{
    private readonly ILogger<FluidsynthService> _logger;
    private readonly IConfiguration _configuration;
    private readonly IHttpContextAccessor _httpContextAccessor;
    
    private readonly Dictionary<string, (CancellationTokenSource Cts, string VariantKey, DateTime StartTime)> _activeProcesses = new();
    private readonly object _processLock = new object();

    public FluidsynthService(ILogger<FluidsynthService> logger, IConfiguration configuration, IHttpContextAccessor httpContextAccessor)
    {
        _logger = logger;
        _configuration = configuration;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<string> GenerateHlsPlaylist(MediaFileMetadata metadata, int[] bitRates)
    {
        var id = metadata.Id;
        var variantKey = bitRates.Length > 0 ? bitRates[0].ToString() : "default";
        // Ensure id doesn't cause double slashes or path issues
        var cleanId = id.TrimStart('/');
        var key = $"/midi/{cleanId}/{variantKey}";
        
        string workingDir = _configuration["WorkingDirectory"] ?? AppContext.BaseDirectory;
        var cacheDir = Path.Combine(workingDir, "hls_segments", "midi", cleanId, variantKey);
        var playlistPath = Path.Combine(cacheDir, "playlist.m3u8");
        
        var baseUrl = GetBaseUrl();
        var encodedSuffix = Uri.EscapeDataString(key + "/");
        var segmentBaseUrl = $"{baseUrl}{encodedSuffix}";
        
        bool startNewProcess = true;

        lock (_processLock)
        {
            if (_activeProcesses.TryGetValue(id, out var active))
            {
                if (active.VariantKey == variantKey)
                {
                    startNewProcess = false;
                }
                else
                {
                    active.Cts.Cancel();
                    _activeProcesses.Remove(id);
                }
            }
        }

        if (startNewProcess)
        {
            if (Directory.Exists(cacheDir))
            {
                // Simple cache: if playlist exists, assume it's good
                if (File.Exists(playlistPath) && new FileInfo(playlistPath).Length > 0)
                {
                    _logger.LogDebug("Using existing MIDI cache for {Id}", id);
                    startNewProcess = false;
                }
                else
                {
                    Directory.Delete(cacheDir, true);
                }
            }
            
            if (startNewProcess)
            {
                Directory.CreateDirectory(cacheDir);

                var soundFontPath = _configuration["Fluidsynth:SoundFont"] ?? "/usr/share/sounds/sf2/FluidR3_GM.sf2";
                if (!File.Exists(soundFontPath))
                {
                    _logger.LogError("SoundFont not found: {Path}", soundFontPath);
                    throw new FileNotFoundException("SoundFont not found", soundFontPath);
                }

                var midiPath = metadata.Path;

                if (!File.Exists(midiPath))
                {
                    _logger.LogError("MIDI file not found: {Path}", midiPath);
                    throw new FileNotFoundException("MIDI file not found", midiPath);
                }

                var bitRate = bitRates.Length > 0 ? bitRates[0] : 128;

                // Calculate MIDI duration to prevent infinite loops
                double duration = GetMidiDuration(midiPath);
                var limitSeconds = duration > 0 ? duration + 5.0 : 600.0; // Limit to duration + 5s buffer, or 10 min fallback

                // Fluidsynth to FFmpeg pipe
                var fluidsynthArgs = $"-ni -q -r 44100 -T raw -O s16 -F - \"{soundFontPath}\" \"{midiPath}\"";
                var ffmpegArgs = $"-y -v error -f s16le -ar 44100 -ac 2 -i - -vn -t {limitSeconds:F2} -c:a aac -b:a {bitRate}k -f hls -hls_time 3 -hls_list_size 0 -start_number 0 -hls_segment_filename \"{cacheDir}/segment_%03d.ts\" \"{playlistPath}\"";

                _logger.LogInformation("Starting MIDI rendering for {Id} ({Duration:F1}s) using SoundFont {Sf}", id, duration, soundFontPath);

                var cts = new CancellationTokenSource();
                RegisterActiveProcess(id, cts, variantKey);
                
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await RunPipedCommand(fluidsynthArgs, ffmpegArgs, cts.Token);
                        _logger.LogInformation("MIDI rendering completed for {Id}", id);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "MIDI rendering failed for {Id}", id);
                    }
                    finally
                    {
                        UnregisterActiveProcess(id, cts);
                    }
                });
            }
        }
        
        await WaitForFirstSegment(id, cacheDir, playlistPath);

        var playlistContent = await File.ReadAllTextAsync(playlistPath);
        playlistContent = playlistContent.Replace("segment_", $"{segmentBaseUrl}segment_");

        return playlistContent;
    }

    private async Task RunPipedCommand(string fluidsynthArgs, string ffmpegArgs, CancellationToken cancellationToken)
    {
        var fluidsynthInfo = new ProcessStartInfo
        {
            FileName = "fluidsynth",
            Arguments = fluidsynthArgs,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var ffmpegInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = ffmpegArgs,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var fluidsynth = Process.Start(fluidsynthInfo);
        using var ffmpeg = Process.Start(ffmpegInfo);

        if (fluidsynth == null || ffmpeg == null) throw new Exception("Failed to start rendering processes");

        // Forward fluidsynth output to ffmpeg input
        _ = Task.Run(async () =>
        {
            try
            {
                await fluidsynth.StandardOutput.BaseStream.CopyToAsync(ffmpeg.StandardInput.BaseStream, cancellationToken);
                _logger.LogDebug("Finished piping Fluidsynth output to FFmpeg stdin");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error piping Fluidsynth to FFmpeg");
            }
            finally
            {
                ffmpeg.StandardInput.Close();
            }
        });
        
        // Log errors from both processes
        _ = Task.Run(async () => {
            while (!fluidsynth.StandardError.EndOfStream)
            {
                var line = await fluidsynth.StandardError.ReadLineAsync();
                if (line != null) _logger.LogInformation("Fluidsynth Output: {Msg}", line);
            }
        });
        _ = Task.Run(async () => {
            while (!ffmpeg.StandardError.EndOfStream)
            {
                var line = await ffmpeg.StandardError.ReadLineAsync();
                if (line != null) _logger.LogInformation("FFmpeg Output: {Msg}", line);
            }
        });

        try 
        {
            await Task.WhenAll(fluidsynth.WaitForExitAsync(cancellationToken), ffmpeg.WaitForExitAsync(cancellationToken));
            _logger.LogInformation("Piped rendering commands finished. Fluidsynth ExitCode: {Fe}, FFmpeg ExitCode: {Ge}", fluidsynth.ExitCode, ffmpeg.ExitCode);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("MIDI rendering cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in piped rendering: {Message}", ex.Message);
        }
        finally
        {
            if (!fluidsynth.HasExited) { _logger.LogInformation("Killing stuck Fluidsynth..."); fluidsynth.Kill(); }
            if (!ffmpeg.HasExited) { _logger.LogInformation("Killing stuck FFmpeg..."); ffmpeg.Kill(); }
        }
    }

    private double GetMidiDuration(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var reader = new BinaryReader(stream);

            if (reader.ReadUInt32() != 0x6468544D) return 0; // 'MThd' check

            reader.ReadUInt32(); // Length (6)
            reader.ReadUInt16(); // Format
            var trackCount = BinaryPrimitives.ReadUInt16BigEndian(reader.ReadBytes(2));
            var division = BinaryPrimitives.ReadUInt16BigEndian(reader.ReadBytes(2));

            if ((division & 0x8000) != 0) return 0; // SMPTE not supported here

            double maxSeconds = 0;

            for (int i = 0; i < trackCount; i++)
            {
                if (reader.ReadUInt32() != 0x6B72544D) break; // 'MTrk' check
                var trackLength = BinaryPrimitives.ReadUInt32BigEndian(reader.ReadBytes(4));
                long trackEndTime = stream.Position + trackLength;

                uint currentTempo = 500000;
                double currentSeconds = 0;

                while (stream.Position < trackEndTime)
                {
                    uint deltaTime = 0;
                    byte b;
                    do { b = reader.ReadByte(); deltaTime = (deltaTime << 7) | (uint)(b & 0x7F); } while ((b & 0x80) != 0);

                    currentSeconds += (double)deltaTime * currentTempo / (division * 1000000.0);

                    byte status = reader.ReadByte();
                    if (status == 0xFF) // Meta event
                    {
                        byte type = reader.ReadByte();
                        uint len = 0;
                        do { b = reader.ReadByte(); len = (len << 7) | (uint)(b & 0x7F); } while ((b & 0x80) != 0);

                        if (type == 0x51 && len == 3) // Set Tempo
                        {
                            var data = reader.ReadBytes(3);
                            currentTempo = (uint)((data[0] << 16) | (data[1] << 8) | data[2]);
                        }
                        else stream.Position += len;
                    }
                    else if (status >= 0xF0) // SysEx
                    {
                        uint len = 0;
                        do { b = reader.ReadByte(); len = (len << 7) | (uint)(b & 0x7F); } while ((b & 0x80) != 0);
                        stream.Position += len;
                    }
                    else
                    {
                        // Voice events
                        int skip = (status & 0xF0) switch { 0xC0 or 0xD0 => 1, _ => 2 };
                        if ((status & 0x80) == 0) skip--; // Running status
                        stream.Position += skip;
                    }
                }
                if (currentSeconds > maxSeconds) maxSeconds = currentSeconds;
            }
            return maxSeconds;
        }
        catch { return 0; }
    }

    private async Task WaitForFirstSegment(string id, string cacheDir, string playlistPath)
    {
        _logger.LogInformation("Waiting for first segment in {Path}", playlistPath);
        var timeout = TimeSpan.FromSeconds(30);
        var startTime = DateTime.Now;

        while (DateTime.Now - startTime < timeout)
        {
            if (File.Exists(playlistPath))
            {
                try
                {
                    var content = await File.ReadAllTextAsync(playlistPath);
                    if (content.Contains(".ts"))
                    {
                        _logger.LogInformation("Found valid playlist for {Id}", id);
                        return;
                    }
                    else
                    {
                        _logger.LogTrace("Playlist exists but no segments yet for {Id}", id);
                    }
                }
                catch (IOException ex)
                {
                    _logger.LogWarning("Retrying playlist read for {Id}: {Msg}", id, ex.Message);
                }
            }
            else
            {
                // Check if the .tmp version exists to confirm FFmpeg is working
                if (File.Exists(playlistPath + ".tmp"))
                {
                    _logger.LogTrace("Found .tmp playlist for {Id}, waiting for rename", id);
                }
            }

            bool active;
            lock (_processLock) active = _activeProcesses.ContainsKey(id);
            
            if (!active)
            {
                // Process finished, check one last time
                if (File.Exists(playlistPath))
                {
                    var content = await File.ReadAllTextAsync(playlistPath);
                    if (content.Contains(".ts")) return;
                }
                _logger.LogError("Rendering process for {Id} exited with active=false", id);
                throw new Exception("Rendering process failed or exited early");
            }

            await Task.Delay(500);
        }
        
        _logger.LogError("Timeout waiting for MIDI rendering for {Id}. Playlist exists: {Exists}", id, File.Exists(playlistPath));
        throw new Exception("Timeout waiting for MIDI rendering");
    }

    private string GetBaseUrl()
    {
        var request = _httpContextAccessor.HttpContext?.Request;
        if (request == null) throw new Exception("HttpContext not available");
        return $"{request.PathBase}/hls?key=";
    }

    private void RegisterActiveProcess(string id, CancellationTokenSource cts, string variantKey)
    {
        lock (_processLock) _activeProcesses[id] = (cts, variantKey, DateTime.Now);
    }

    private void UnregisterActiveProcess(string id, CancellationTokenSource cts)
    {
        lock (_processLock)
        {
            if (_activeProcesses.TryGetValue(id, out var active) && active.Cts == cts)
                _activeProcesses.Remove(id);
        }
    }
}
