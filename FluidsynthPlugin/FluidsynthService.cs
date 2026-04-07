using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Purewwk.Plugin.Abstractions;

namespace Purewwk.Plugins.Fluidsynth;

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

    public async Task<string> GenerateHlsPlaylist(MediaItem item, int[] bitRates)
    {
        var id = item.Id;
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

                var midiPath = item.Path;

                if (!File.Exists(midiPath))
                {
                    _logger.LogError("MIDI file not found: {Path}", midiPath);
                    throw new FileNotFoundException("MIDI file not found", midiPath);
                }

                var bitRate = bitRates.Length > 0 ? bitRates[0] : 128;

                // Calculate MIDI duration to prevent infinite loops
                double duration = GetMidiDuration(midiPath);
                var limitSeconds = duration > 0 ? duration + 5.0 : 600.0; // Limit to duration + 5s buffer, or 10 min fallback

                var cts = new CancellationTokenSource();
                RegisterActiveProcess(id, cts, variantKey);
                
                _ = Task.Run(async () =>
                {
                    try
                    {
                        _logger.LogInformation("Starting MIDI rendering for {Id} ({Duration:F1}s) using SoundFont {Sf}", id, duration, soundFontPath);

                        await RunPipedCommand(soundFontPath, midiPath, playlistPath, cacheDir, bitRate, limitSeconds, cts.Token);
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

    private async Task RunPipedCommand(string soundFontPath, string midiPath, string playlistPath, string cacheDir, int bitRate, double limitSeconds, CancellationToken cancellationToken)
    {
        var fluidsynthInfo = new ProcessStartInfo
        {
            FileName = "fluidsynth",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // Use ArgumentList for safety
        fluidsynthInfo.ArgumentList.Add("-ni");
        fluidsynthInfo.ArgumentList.Add("-q");
        fluidsynthInfo.ArgumentList.Add("-a");
        fluidsynthInfo.ArgumentList.Add("file");
        fluidsynthInfo.ArgumentList.Add("-r");
        fluidsynthInfo.ArgumentList.Add("44100");
        fluidsynthInfo.ArgumentList.Add("-T");
        fluidsynthInfo.ArgumentList.Add("raw");
        fluidsynthInfo.ArgumentList.Add("-O");
        fluidsynthInfo.ArgumentList.Add("s16");
        fluidsynthInfo.ArgumentList.Add("-F");
        fluidsynthInfo.ArgumentList.Add("-");
        fluidsynthInfo.ArgumentList.Add(soundFontPath);
        fluidsynthInfo.ArgumentList.Add(midiPath);

        var ffmpegInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        ffmpegInfo.ArgumentList.Add("-y");
        ffmpegInfo.ArgumentList.Add("-v");
        ffmpegInfo.ArgumentList.Add("info"); // Changed from error to info for more info
        ffmpegInfo.ArgumentList.Add("-f");
        ffmpegInfo.ArgumentList.Add("s16le");
        ffmpegInfo.ArgumentList.Add("-ar");
        ffmpegInfo.ArgumentList.Add("44100");
        ffmpegInfo.ArgumentList.Add("-ac");
        ffmpegInfo.ArgumentList.Add("2");
        ffmpegInfo.ArgumentList.Add("-i");
        ffmpegInfo.ArgumentList.Add("-");
        ffmpegInfo.ArgumentList.Add("-af");
        ffmpegInfo.ArgumentList.Add("loudnorm");
        ffmpegInfo.ArgumentList.Add("-vn");
        ffmpegInfo.ArgumentList.Add("-t");
        ffmpegInfo.ArgumentList.Add(limitSeconds.ToString("F2", CultureInfo.InvariantCulture));
        ffmpegInfo.ArgumentList.Add("-c:a");
        ffmpegInfo.ArgumentList.Add("aac");
        ffmpegInfo.ArgumentList.Add("-b:a");
        ffmpegInfo.ArgumentList.Add($"{bitRate}k");
        ffmpegInfo.ArgumentList.Add("-f");
        ffmpegInfo.ArgumentList.Add("hls");
        ffmpegInfo.ArgumentList.Add("-hls_time");
        ffmpegInfo.ArgumentList.Add("3");
        ffmpegInfo.ArgumentList.Add("-hls_list_size");
        ffmpegInfo.ArgumentList.Add("0");
        ffmpegInfo.ArgumentList.Add("-start_number");
        ffmpegInfo.ArgumentList.Add("0");
        ffmpegInfo.ArgumentList.Add("-hls_segment_filename");
        ffmpegInfo.ArgumentList.Add($"{cacheDir}/segment_%03d.ts");
        ffmpegInfo.ArgumentList.Add(playlistPath);

        _logger.LogInformation("Executing MIDI pipe: {F} -> {G}", 
            string.Join(" ", fluidsynthInfo.ArgumentList.Select(a => a.Contains(" ") ? $"\"{a}\"" : a)),
            string.Join(" ", ffmpegInfo.ArgumentList.Select(a => a.Contains(" ") ? $"\"{a}\"" : a)));

        using var fluidsynth = Process.Start(fluidsynthInfo);
        using var ffmpeg = Process.Start(ffmpegInfo);

        if (fluidsynth == null || ffmpeg == null) throw new Exception("Failed to start rendering processes");

        // Forward fluidsynth output to ffmpeg input
        var pipeTask = Task.Run(async () =>
        {
            try
            {
                await fluidsynth.StandardOutput.BaseStream.CopyToAsync(ffmpeg.StandardInput.BaseStream, cancellationToken);
                _logger.LogDebug("Finished piping Fluidsynth output to FFmpeg stdin");
            }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                    _logger.LogWarning(ex, "Error piping Fluidsynth to FFmpeg (this may be normal if process exited)");
            }
            finally
            {
                try { ffmpeg.StandardInput.Close(); } catch { }
            }
        });
        
        // Log errors from both processes
        var fluidsythLogTask = Task.Run(async () => {
            while (!fluidsynth.StandardError.EndOfStream)
            {
                var line = await fluidsynth.StandardError.ReadLineAsync();
                if (line != null) _logger.LogInformation("Fluidsynth Output: {Msg}", line);
            }
        });
        var ffmpegLogTask = Task.Run(async () => {
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
            _logger.LogInformation("MIDI rendering cancelled for Task.WhenAll");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in piped rendering: {Message}", ex.Message);
        }
        finally
        {
            if (!fluidsynth.HasExited) { _logger.LogInformation("Killing stuck Fluidsynth..."); try { fluidsynth.Kill(); } catch { } }
            if (!ffmpeg.HasExited) { _logger.LogInformation("Killing stuck FFmpeg..."); try { ffmpeg.Kill(); } catch { } }
            
            // Ensure background tasks finish
            await Task.WhenAny(pipeTask, Task.Delay(1000));
        }
    }

    private double GetMidiDuration(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var reader = new BinaryReader(stream);

            if (BinaryPrimitives.ReadUInt32BigEndian(reader.ReadBytes(4)) != 0x4D546864) return 0; // 'MThd'

            reader.ReadUInt32(); // Header length (6)
            var format = BinaryPrimitives.ReadUInt16BigEndian(reader.ReadBytes(2));
            var trackCount = BinaryPrimitives.ReadUInt16BigEndian(reader.ReadBytes(2));
            var division = BinaryPrimitives.ReadUInt16BigEndian(reader.ReadBytes(2));

            if ((division & 0x8000) != 0) return 0; // SMPTE not supported

            // Tempo Map: Absolute Tick -> Tempo (microseconds per quarter note)
            var tempoMap = new SortedDictionary<long, uint> { { 0, 500000 } }; // Default 120 BPM
            long maxTicks = 0;

            for (int i = 0; i < trackCount; i++)
            {
                if (BinaryPrimitives.ReadUInt32BigEndian(reader.ReadBytes(4)) != 0x4D54726B) break; // 'MTrk'
                var trackLength = BinaryPrimitives.ReadUInt32BigEndian(reader.ReadBytes(4));
                long trackEndTime = stream.Position + trackLength;
                long currentTicks = 0;
                byte lastStatus = 0;

                while (stream.Position < trackEndTime)
                {
                    // Read Delta Time
                    uint deltaTime = 0;
                    byte b;
                    do { b = reader.ReadByte(); deltaTime = (deltaTime << 7) | (uint)(b & 0x7F); } while ((b & 0x80) != 0);
                    currentTicks += deltaTime;

                    byte status = reader.ReadByte();
                    if (status < 0x80)
                    {
                        // Running Status
                        status = lastStatus;
                        stream.Position--; // Reread this byte as data
                    }

                    if (status == 0xFF) // Meta Event
                    {
                        byte type = reader.ReadByte();
                        uint len = 0;
                        do { b = reader.ReadByte(); len = (len << 7) | (uint)(b & 0x7F); } while ((b & 0x80) != 0);

                        if (type == 0x51 && len == 3) // Set Tempo
                        {
                            var data = reader.ReadBytes(3);
                            uint tempo = (uint)((data[0] << 16) | (data[1] << 8) | data[2]);
                            tempoMap[currentTicks] = tempo;
                        }
                        else stream.Position += len;
                        lastStatus = 0; // Meta events reset running status
                    }
                    else if (status >= 0xF0) // SysEx
                    {
                        uint len = 0;
                        do { b = reader.ReadByte(); len = (len << 7) | (uint)(b & 0x7F); } while ((b & 0x80) != 0);
                        stream.Position += len;
                        lastStatus = 0;
                    }
                    else
                    {
                        // Voice Event
                        lastStatus = status;
                        // Important: Only update maxTicks for voice events (music)
                        // This avoids trailing silent space often added by MIDI editors
                        if (currentTicks > maxTicks) maxTicks = currentTicks;

                        int skip = (status & 0xF0) switch
                        {
                            0xC0 or 0xD0 => 1,
                            _ => 2
                        };
                        stream.Position += skip;
                    }
                }
            }

            // Convert max ticks to seconds using the tempo map
            double durationSeconds = 0;
            long lastTicks = 0;
            uint currentTempo = 500000;

            foreach (var entry in tempoMap)
            {
                if (entry.Key >= maxTicks) break;
                durationSeconds += (double)(entry.Key - lastTicks) * currentTempo / (division * 1000000.0);
                lastTicks = entry.Key;
                currentTempo = entry.Value;
            }
            durationSeconds += (double)(maxTicks - lastTicks) * currentTempo / (division * 1000000.0);

            return durationSeconds;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error calculating MIDI duration: {Msg}", ex.Message);
            return 0;
        }
    }

    private async Task WaitForFirstSegment(string id, string cacheDir, string playlistPath)
    {
        _logger.LogInformation("Waiting for first segment in {Path}", playlistPath);
        var timeout = TimeSpan.FromSeconds(60); // Increased from 30s
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

            await Task.Delay(1000); // Increased delay
        }
        
        // Timeout happened - collect more diagnostics
        bool dirExists = Directory.Exists(cacheDir);
        string fileList = "none";
        if (dirExists)
        {
            var files = Directory.GetFiles(cacheDir);
            fileList = string.Join(", ", files.Select(Path.GetFileName));
        }

        _logger.LogError("Timeout waiting for MIDI rendering for {Id}. Playlist exists: {Exists}, CacheDir exists: {DirExists}, Files in CacheDir: {Files}", 
            id, File.Exists(playlistPath), dirExists, fileList);
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



