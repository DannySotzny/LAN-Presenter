using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using BeamerPresenter.Application;
using BeamerPresenter.Domain;
using Microsoft.Extensions.Logging;

namespace BeamerPresenter.Infrastructure;

internal sealed record ProcessExecutionResult(int ExitCode, string StandardOutput, string StandardError);

internal interface IExternalProcessRunner
{
    Task<ProcessExecutionResult> RunAsync(string executablePath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);
}

internal sealed class ExternalProcessRunner : IExternalProcessRunner
{
    public async Task<ProcessExecutionResult> RunAsync(string executablePath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Der Prozess '{executablePath}' konnte nicht gestartet werden.");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"Der Prozess '{executablePath}' hat das Zeitlimit überschritten.");
        }

        return new ProcessExecutionResult(process.ExitCode, await standardOutput, await standardError);
    }
}

internal sealed class FfprobeService(
    IPresenterSettingsService settingsService,
    IExternalProcessRunner processRunner,
    ILogger<FfprobeService> logger,
    string toolsDirectory) : IFfprobeService
{
    private static readonly TimeSpan ValidationTimeout = TimeSpan.FromSeconds(10);

    public async Task<FfprobeAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetAsync(cancellationToken);
        foreach (var candidate in GetCandidates(settings.FfprobePath))
        {
            try
            {
                var result = await processRunner.RunAsync(candidate, ["-version"], ValidationTimeout, cancellationToken);
                if (result.ExitCode != 0)
                {
                    continue;
                }

                var version = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                return new FfprobeAvailability(true, candidate, version ?? "ffprobe", null);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogDebug(exception, "FFprobe candidate {FfprobePath} could not be validated", candidate);
            }
        }

        return new FfprobeAvailability(false, null, null, "FFprobe wurde nicht gefunden oder konnte nicht ausgeführt werden.");
    }

    public async Task<FfprobeAvailability> InstallWithWinGetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await processRunner.RunAsync(
                "winget.exe",
                ["install", "--id", "Gyan.FFmpeg", "-e", "--source", "winget", "--accept-package-agreements", "--accept-source-agreements"],
                TimeSpan.FromMinutes(10),
                cancellationToken);
            if (result.ExitCode != 0)
            {
                return new FfprobeAvailability(false, null, null, string.IsNullOrWhiteSpace(result.StandardError) ? "WinGet konnte FFmpeg nicht installieren." : result.StandardError.Trim());
            }

            return await CheckAvailabilityAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "FFmpeg installation through WinGet failed");
            return new FfprobeAvailability(false, null, null, exception.Message);
        }
    }

    public async Task<MediaProbeResult> ProbeAsync(string mediaPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(mediaPath))
        {
            return FailedProbe(MediaProbeStatus.Missing, "Die Mediendatei wurde nicht gefunden.");
        }

        var availability = await CheckAvailabilityAsync(cancellationToken);
        if (!availability.IsAvailable || availability.ExecutablePath is null)
        {
            return FailedProbe(MediaProbeStatus.Unknown, availability.Error ?? "FFprobe ist nicht verfügbar.");
        }

        try
        {
            var result = await processRunner.RunAsync(
                availability.ExecutablePath,
                ["-v", "error", "-show_entries", "format=duration,format_name:stream=codec_type,codec_name,width,height,r_frame_rate,channels", "-of", "json", mediaPath],
                TimeSpan.FromMinutes(2),
                cancellationToken);
            if (result.ExitCode != 0)
            {
                return FailedProbe(MediaProbeStatus.Invalid, LimitError(result.StandardError));
            }

            return ParseProbeResult(result.StandardOutput);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "FFprobe analysis failed for {MediaPath}", mediaPath);
            return FailedProbe(MediaProbeStatus.Invalid, LimitError(exception.Message));
        }
    }

    private IEnumerable<string> GetCandidates(string? configuredPath)
    {
        var candidates = new List<string?>
        {
            configuredPath,
            Path.Combine(toolsDirectory, "ffprobe.exe")
        };
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        candidates.AddRange(pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(path => Path.Combine(path, "ffprobe.exe")));
        candidates.AddRange(
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "ffprobe.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin", "ffprobe.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "ffmpeg", "bin", "ffprobe.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey", "bin", "ffprobe.exe")
        ]);

        return candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static MediaProbeResult ParseProbeResult(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var format = root.TryGetProperty("format", out var formatElement) ? formatElement : default;
        var duration = format.ValueKind == JsonValueKind.Object
            && format.TryGetProperty("duration", out var durationElement)
            && double.TryParse(durationElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var durationSeconds)
                ? TimeSpan.FromSeconds(durationSeconds)
                : (TimeSpan?)null;
        var container = format.ValueKind == JsonValueKind.Object && format.TryGetProperty("format_name", out var containerElement)
            ? containerElement.GetString()
            : null;

        JsonElement? videoStream = null;
        JsonElement? audioStream = null;
        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var codecType = GetString(stream, "codec_type");
                if (videoStream is null && string.Equals(codecType, "video", StringComparison.Ordinal))
                {
                    videoStream = stream.Clone();
                }
                else if (audioStream is null && string.Equals(codecType, "audio", StringComparison.Ordinal))
                {
                    audioStream = stream.Clone();
                }
            }
        }

        var videoCodec = videoStream is { } video ? GetString(video, "codec_name") : null;
        var audioCodec = audioStream is { } audio ? GetString(audio, "codec_name") : null;
        var playbackStatus = IsChromeCompatible(videoCodec, audioCodec)
            ? MediaPlaybackStatus.Supported
            : MediaPlaybackStatus.Unsupported;
        return new MediaProbeResult(
            MediaProbeStatus.Valid,
            playbackStatus,
            duration,
            container,
            videoCodec,
            videoStream is { } widthVideo ? GetInt32(widthVideo, "width") : null,
            videoStream is { } heightVideo ? GetInt32(heightVideo, "height") : null,
            videoStream is { } frameRateVideo ? ParseFrameRate(GetString(frameRateVideo, "r_frame_rate")) : null,
            audioCodec,
            audioStream is { } channelsAudio ? GetInt32(channelsAudio, "channels") : null,
            null);
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) ? property.GetString() : null;

    private static int? GetInt32(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value) ? value : null;

    private static double? ParseFrameRate(string? frameRate)
    {
        if (string.IsNullOrWhiteSpace(frameRate))
        {
            return null;
        }

        var parts = frameRate.Split('/', 2);
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator))
        {
            return null;
        }

        if (parts.Length == 1)
        {
            return numerator;
        }

        return double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) && denominator != 0
            ? numerator / denominator
            : null;
    }

    private static bool IsChromeCompatible(string? videoCodec, string? audioCodec)
    {
        var videoSupported = videoCodec is "h264" or "vp8" or "vp9" or "av1";
        var audioSupported = audioCodec is null or "aac" or "mp3" or "opus" or "vorbis";
        return videoSupported && audioSupported;
    }

    private static MediaProbeResult FailedProbe(MediaProbeStatus status, string error) =>
        new(
            status,
            status == MediaProbeStatus.Unknown ? MediaPlaybackStatus.Unknown : MediaPlaybackStatus.Unsupported,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            LimitError(error));

    private static string LimitError(string error) => error.Length <= 4096 ? error : error[..4096];
}
