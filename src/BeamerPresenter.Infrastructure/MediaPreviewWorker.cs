using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BeamerPresenter.Application;
using BeamerPresenter.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeamerPresenter.Infrastructure;

internal sealed class MediaPreviewWorker(
    IMediaLibraryService mediaLibrary,
    IMediaFolderService mediaFolders,
    IFfprobeService ffprobe,
    IExternalProcessRunner processRunner,
    MediaPreviewService previews,
    ILogger<MediaPreviewWorker> logger) : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(5);
    private readonly Dictionary<int, DateTimeOffset> retryAfter = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await GeneratePendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Media preview scan failed");
            }

            await Task.Delay(ScanInterval, stoppingToken);
        }
    }

    internal async Task GeneratePendingAsync(CancellationToken cancellationToken = default)
    {
        var assets = await mediaLibrary.GetAllAsync(cancellationToken);
        var folders = (await mediaFolders.GetAllAsync(cancellationToken)).Where(folder => folder.Enabled).ToList();
        var pending = assets.Where(asset => NeedsPreview(asset, folders)).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        var availability = await ffprobe.CheckAvailabilityAsync(cancellationToken);
        var executable = FindFfmpeg(availability.ExecutablePath);
        if (executable is null)
        {
            return;
        }

        foreach (var asset in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await GeneratePreviewAsync(asset, executable, cancellationToken);
        }
    }

    private bool NeedsPreview(VideoAsset asset, IReadOnlyList<MediaFolder> folders) =>
        asset.IsAvailable && asset.Duration > TimeSpan.Zero &&
        folders.Any(folder => IsInsideFolder(asset.FullPath, folder.Path)) &&
        asset.ProbeStatus == MediaProbeStatus.Valid && previews.GetReadyPreviewPath(asset) is null &&
        (!retryAfter.TryGetValue(asset.Id, out var nextRetry) || nextRetry <= DateTimeOffset.UtcNow);

    private async Task GeneratePreviewAsync(VideoAsset asset, string executable, CancellationToken cancellationToken)
    {
        var destination = previews.GetCachePath(asset);
        var temporary = Path.Combine(previews.CacheDirectory, $"{asset.Id}-{Guid.NewGuid():N}.jpg");
        try
        {
            var snapshot = new FileInfo(asset.FullPath);
            if (!MatchesSnapshot(snapshot, asset))
            {
                return;
            }

            Directory.CreateDirectory(previews.CacheDirectory);
            var result = await processRunner.RunAsync(executable,
                ["-nostdin", "-hide_banner", "-loglevel", "error", "-i", asset.FullPath,
                 "-t", "120", "-vf", "fps=1/5,scale=384:216:force_original_aspect_ratio=decrease,pad=384:216:(ow-iw)/2:(oh-ih)/2,tile=6x4:nb_frames=24",
                 "-frames:v", "1", "-update", "1", temporary],
                TimeSpan.FromMinutes(3), cancellationToken);
            snapshot.Refresh();
            if (!IsUsablePreview(result.ExitCode, temporary, snapshot, asset))
            {
                ScheduleRetry(asset, result.ExitCode);
                return;
            }

            PublishPreview(asset, temporary, destination);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ScheduleRetry(asset, exception);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static bool MatchesSnapshot(FileInfo snapshot, VideoAsset asset) =>
        snapshot.Exists && snapshot.Length == asset.FileSize && snapshot.LastWriteTimeUtc == asset.LastWriteUtc.UtcDateTime;

    private static bool IsUsablePreview(int exitCode, string temporary, FileInfo snapshot, VideoAsset asset) =>
        exitCode == 0 && File.Exists(temporary) && new FileInfo(temporary).Length > 0 && MatchesSnapshot(snapshot, asset);

    private void PublishPreview(VideoAsset asset, string temporary, string destination)
    {
        File.Move(temporary, destination, overwrite: true);
        foreach (var old in Directory.EnumerateFiles(previews.CacheDirectory, $"{asset.Id}-*.jpg")
                     .Where(path => !string.Equals(path, destination, StringComparison.OrdinalIgnoreCase)))
        {
            File.Delete(old);
        }

        retryAfter.Remove(asset.Id);
    }

    private void ScheduleRetry(VideoAsset asset, int exitCode)
    {
        retryAfter[asset.Id] = DateTimeOffset.UtcNow + RetryInterval;
        logger.LogWarning("Could not generate media preview for video {MediaId}: {ExitCode}", asset.Id, exitCode);
    }

    private void ScheduleRetry(VideoAsset asset, Exception exception)
    {
        retryAfter[asset.Id] = DateTimeOffset.UtcNow + RetryInterval;
        logger.LogWarning(exception, "Could not generate media preview for video {MediaId}", asset.Id);
    }

    private static bool IsInsideFolder(string path, string folder)
    {
        try
        {
            var fullFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            return Path.GetFullPath(path).StartsWith(fullFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string? FindFfmpeg(string? ffprobePath)
    {
        if (ffprobePath is null) return null;
        var candidates = new List<string> { Path.Combine(Path.GetDirectoryName(ffprobePath)!, "ffmpeg.exe") };
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => Path.Combine(directory, "ffmpeg.exe")));
        return candidates.FirstOrDefault(File.Exists);
    }
}

internal sealed class MediaPreviewService(string dataDirectory) : IMediaPreviewService
{
    internal string CacheDirectory { get; } = Path.Combine(Path.GetFullPath(dataDirectory), "Previews");

    public string? GetReadyPreviewPath(VideoAsset asset)
    {
        var source = new FileInfo(asset.FullPath);
        if (!asset.IsAvailable || !source.Exists || source.Length != asset.FileSize ||
            source.LastWriteTimeUtc != asset.LastWriteUtc.UtcDateTime)
        {
            return null;
        }

        var path = GetCachePath(asset);
        return File.Exists(path) ? path : null;
    }

    internal string GetCachePath(VideoAsset asset)
    {
        var version = $"v2|{asset.FullPath.ToUpperInvariant()}|{asset.FileSize.ToString(CultureInfo.InvariantCulture)}|{asset.LastWriteUtc.UtcTicks.ToString(CultureInfo.InvariantCulture)}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(version)))[..24];
        return Path.Combine(CacheDirectory, $"{asset.Id}-{hash}.jpg");
    }
}
