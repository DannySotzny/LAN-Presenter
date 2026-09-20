using System.Collections.Concurrent;
using System.Threading.Channels;
using BeamerPresenter.Application;
using BeamerPresenter.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeamerPresenter.Infrastructure;

internal sealed record StableFileSnapshot(long Length, DateTimeOffset LastWriteUtc);

internal interface IFileStabilityChecker
{
    Task<StableFileSnapshot?> WaitForStableFileAsync(string fullPath, CancellationToken cancellationToken);
}

internal sealed class FileStabilityChecker : IFileStabilityChecker
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RequiredStablePeriod = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumWait = TimeSpan.FromMinutes(10);

    public async Task<StableFileSnapshot?> WaitForStableFileAsync(string fullPath, CancellationToken cancellationToken)
    {
        var startedUtc = DateTimeOffset.UtcNow;
        var stableSinceUtc = startedUtc;
        StableFileSnapshot? previous = null;
        while (DateTimeOffset.UtcNow - startedUtc < MaximumWait)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = ReadSnapshot(fullPath);
            if (current is null)
            {
                return null;
            }

            if (current != previous)
            {
                previous = current;
                stableSinceUtc = DateTimeOffset.UtcNow;
            }
            else if (DateTimeOffset.UtcNow - stableSinceUtc >= RequiredStablePeriod)
            {
                return current;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        return null;
    }

    private static StableFileSnapshot? ReadSnapshot(string fullPath)
    {
        var file = new FileInfo(fullPath);
        file.Refresh();
        return file.Exists
            ? new StableFileSnapshot(file.Length, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero))
            : null;
    }
}

internal sealed class MediaProbeQueue(
    IDbContextFactory<PresenterDbContext> contextFactory,
    IFfprobeService ffprobeService,
    IFileStabilityChecker stabilityChecker,
    ILogger<MediaProbeQueue> logger) : BackgroundService, IMediaProbeQueue
{
    private const int WorkerCount = 2;
    private readonly Channel<MediaProbeRequest> requests = Channel.CreateUnbounded<MediaProbeRequest>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });
    private readonly ConcurrentDictionary<int, byte> pendingMediaIds = new();

    public async ValueTask QueueAsync(int mediaId, string fullPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        if (!pendingMediaIds.TryAdd(mediaId, 0))
        {
            return;
        }

        try
        {
            await requests.Writer.WriteAsync(new MediaProbeRequest(mediaId, fullPath), cancellationToken);
        }
        catch
        {
            pendingMediaIds.TryRemove(mediaId, out _);
            throw;
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, WorkerCount).Select(_ => ProcessQueueAsync(stoppingToken)));

    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        await foreach (var request in requests.Reader.ReadAllAsync(cancellationToken))
        {
            pendingMediaIds.TryRemove(request.MediaId, out _);
            try
            {
                await ProcessAsync(request, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Media analysis failed for {MediaPath}", request.FullPath);
            }
        }
    }

    private async Task ProcessAsync(MediaProbeRequest request, CancellationToken cancellationToken)
    {
        var stableFile = await stabilityChecker.WaitForStableFileAsync(request.FullPath, cancellationToken);
        if (stableFile is null)
        {
            if (File.Exists(request.FullPath))
            {
                logger.LogWarning("Media file {MediaPath} did not become stable in time and will be queued again", request.FullPath);
                await QueueAsync(request.MediaId, request.FullPath, cancellationToken);
                return;
            }

            await MarkMissingAsync(request, cancellationToken);
            return;
        }

        var result = await ffprobeService.ProbeAsync(request.FullPath, cancellationToken);
        var currentFile = ReadSnapshot(request.FullPath);
        if (currentFile is null)
        {
            await MarkMissingAsync(request, cancellationToken);
            return;
        }

        if (currentFile != stableFile)
        {
            await QueueAsync(request.MediaId, request.FullPath, cancellationToken);
            return;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var asset = await context.Videos.SingleOrDefaultAsync(video => video.Id == request.MediaId, cancellationToken);
        if (asset is null || !string.Equals(asset.FullPath, request.FullPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        asset.FileSize = stableFile.Length;
        asset.LastWriteUtc = stableFile.LastWriteUtc;
        asset.LastScannedUtc = DateTimeOffset.UtcNow;
        asset.IsAvailable = true;
        asset.Duration = result.Duration;
        asset.Container = result.Container;
        asset.VideoCodec = result.VideoCodec;
        asset.VideoWidth = result.VideoWidth;
        asset.VideoHeight = result.VideoHeight;
        asset.FrameRate = result.FrameRate;
        asset.AudioCodec = result.AudioCodec;
        asset.AudioChannels = result.AudioChannels;
        asset.ProbeStatus = result.ProbeStatus;
        asset.PlaybackStatus = result.PlaybackStatus;
        asset.ProbeError = result.Error;
        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task MarkMissingAsync(MediaProbeRequest request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var asset = await context.Videos.SingleOrDefaultAsync(video => video.Id == request.MediaId, cancellationToken);
        if (asset is null || !string.Equals(asset.FullPath, request.FullPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        asset.IsAvailable = false;
        asset.LastScannedUtc = DateTimeOffset.UtcNow;
        asset.ProbeStatus = MediaProbeStatus.Missing;
        asset.PlaybackStatus = MediaPlaybackStatus.Unsupported;
        asset.ProbeError = "Die Mediendatei wurde nicht gefunden.";
        await context.SaveChangesAsync(cancellationToken);
    }

    private static StableFileSnapshot? ReadSnapshot(string fullPath)
    {
        var file = new FileInfo(fullPath);
        file.Refresh();
        return file.Exists
            ? new StableFileSnapshot(file.Length, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero))
            : null;
    }

    private sealed record MediaProbeRequest(int MediaId, string FullPath);
}
