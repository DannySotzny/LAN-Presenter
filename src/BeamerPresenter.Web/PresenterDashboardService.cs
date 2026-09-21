using BeamerPresenter.Application;
using BeamerPresenter.Domain;

namespace BeamerPresenter.Web;

public sealed record PresenterDashboardSnapshot(
    string PresenterState,
    bool BrowserConnected,
    string BrowserStatus,
    DateTimeOffset LastHeartbeatUtc,
    string CurrentTitle,
    TimeSpan? Position,
    TimeSpan? Duration,
    bool FfprobeAvailable,
    string FfprobeStatus,
    bool MediaScannerRunning,
    DateTimeOffset? LastMediaScanUtc,
    string? MediaScannerError,
    int FileCount,
    int PlayableCount,
    int ErrorCount,
    int QueueCount,
    DateTimeOffset GeneratedUtc);

public sealed class PresenterDashboardService(
    IMediaLibraryService mediaLibrary,
    PlaybackQueueService playbackQueue,
    PlaybackController playback,
    IPresenterTelemetry telemetry,
    IMediaScannerStatus mediaScannerStatus,
    IFfprobeService ffprobeService,
    TimeProvider timeProvider)
{
    private static readonly TimeSpan PresenterFreshness = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FfprobeRefreshInterval = TimeSpan.FromMinutes(1);
    private readonly SemaphoreSlim ffprobeGate = new(1, 1);
    private FfprobeAvailability? ffprobeAvailability;
    private DateTimeOffset ffprobeCheckedUtc = DateTimeOffset.MinValue;

    public async Task<PresenterDashboardSnapshot> GetAsync(CancellationToken cancellationToken = default)
    {
        var videosTask = mediaLibrary.GetAllAsync(cancellationToken);
        var queueTask = playbackQueue.GetQueueAsync(cancellationToken);
        var ffprobeTask = GetFfprobeAvailabilityAsync(cancellationToken);
        await Task.WhenAll(videosTask, queueTask, ffprobeTask);

        var now = timeProvider.GetUtcNow();
        var videos = await videosTask;
        var queue = await queueTask;
        var ffprobe = await ffprobeTask;
        var presenter = telemetry.Current;
        var scanner = mediaScannerStatus.Current;
        var current = queue.SingleOrDefault(entry => entry.Status == QueueEntryStatus.Playing);
        var browserConnected = presenter.IsConnected && now - presenter.ReceivedUtc <= PresenterFreshness;

        return new PresenterDashboardSnapshot(
            playback.State.ToString(),
            browserConnected,
            browserConnected ? presenter.Status : "Disconnected",
            presenter.ReceivedUtc,
            GetTitle(current, videos),
            presenter.Position,
            presenter.Duration,
            ffprobe.IsAvailable,
            ffprobe.IsAvailable ? ffprobe.Version ?? "Verfügbar" : ffprobe.Error ?? "Nicht verfügbar",
            scanner.IsRunning,
            scanner.LastCompletedUtc,
            scanner.LastError,
            videos.Count,
            videos.Count(IsPlayable),
            videos.Count(IsProblem),
            queue.Count,
            now);
    }

    private async Task<FfprobeAvailability> GetFfprobeAvailabilityAsync(CancellationToken cancellationToken)
    {
        await ffprobeGate.WaitAsync(cancellationToken);
        try
        {
            var now = timeProvider.GetUtcNow();
            if (ffprobeAvailability is null || now - ffprobeCheckedUtc >= FfprobeRefreshInterval)
            {
                try
                {
                    ffprobeAvailability = await ffprobeService.CheckAvailabilityAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    ffprobeAvailability = new FfprobeAvailability(false, null, null, exception.Message);
                }

                ffprobeCheckedUtc = now;
            }

            return ffprobeAvailability;
        }
        finally
        {
            ffprobeGate.Release();
        }
    }

    private static string GetTitle(QueueEntry? entry, IReadOnlyList<VideoAsset> videos) => entry?.SourceType switch
    {
        MediaSourceType.Local when entry.MediaId is int mediaId =>
            videos.FirstOrDefault(video => video.Id == mediaId)?.FileName ?? $"Video #{mediaId}",
        MediaSourceType.YouTube when entry.ExternalSourceKey?.StartsWith("youtube:", StringComparison.Ordinal) == true =>
            $"YouTube: {entry.ExternalSourceKey[8..]}",
        _ => "–"
    };

    private static bool IsPlayable(VideoAsset video) =>
        video.IsAvailable && video.PlaybackStatus == MediaPlaybackStatus.Supported;

    private static bool IsProblem(VideoAsset video) =>
        !video.IsAvailable ||
        video.ProbeStatus is MediaProbeStatus.Invalid or MediaProbeStatus.Missing or MediaProbeStatus.Unsupported ||
        video.PlaybackStatus is MediaPlaybackStatus.Unsupported or MediaPlaybackStatus.Failed;
}
