using BeamerPresenter.Domain;

namespace BeamerPresenter.Application;

public interface IPresenterSettingsService
{
    Task<PresenterSettings> GetAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(PresenterSettings settings, CancellationToken cancellationToken = default);
    Task SetWebPasswordAsync(string password, CancellationToken cancellationToken = default);
    Task<bool> VerifyWebPasswordAsync(string password, CancellationToken cancellationToken = default);
    Task<bool> HasWebPasswordAsync(CancellationToken cancellationToken = default);
}

public interface IMediaLibraryService
{
    Task<IReadOnlyList<VideoAsset>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<VideoAsset?> GetByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<VideoAsset> AddUploadAsync(string originalFileName, Stream content, long length, CancellationToken cancellationToken = default);
    Task SetEnabledAsync(int id, bool enabled, CancellationToken cancellationToken = default);
    Task ReanalyzeAsync(int id, CancellationToken cancellationToken = default);
    Task MarkPlaybackFailedAsync(int id, CancellationToken cancellationToken = default);
}

public interface IMediaFolderService
{
    Task<IReadOnlyList<MediaFolder>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<MediaFolder> AddAsync(string path, bool includeSubdirectories, CancellationToken cancellationToken = default);
    Task RemoveAsync(int id, CancellationToken cancellationToken = default);
}

public interface IMediaScanner
{
    Task<MediaScanResult> ScanAllAsync(CancellationToken cancellationToken = default);
}

public sealed record MediaScanResult(int Added, int Updated, int Missing, int Unchanged);

public interface IMediaScannerStatus
{
    MediaScannerSnapshot Current { get; }
}

public sealed record MediaScannerSnapshot(
    bool IsRunning,
    DateTimeOffset? LastCompletedUtc,
    MediaScanResult? LastResult,
    string? LastError);

public interface IMediaProbeQueue
{
    ValueTask QueueAsync(int mediaId, string fullPath, CancellationToken cancellationToken = default);
}

public interface IFfprobeService
{
    Task<FfprobeAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken = default);
    Task<FfprobeAvailability> InstallWithWinGetAsync(CancellationToken cancellationToken = default);
    Task<MediaProbeResult> ProbeAsync(string mediaPath, CancellationToken cancellationToken = default);
}

public sealed record FfprobeAvailability(bool IsAvailable, string? ExecutablePath, string? Version, string? Error);

public interface IMonitorService
{
    IReadOnlyList<DisplayMonitor> GetAll();
}

public interface IBrowserController
{
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task ShowAsync(CancellationToken cancellationToken = default);
    Task HideAsync(CancellationToken cancellationToken = default);
    Task<bool> IsRunningAsync(CancellationToken cancellationToken = default);
    Task<bool> IsTopmostAsync(CancellationToken cancellationToken = default);
}

public interface IPresenterTelemetry
{
    PresenterTelemetrySnapshot Current { get; }
}

public sealed record PresenterTelemetrySnapshot(
    bool IsConnected,
    string Status,
    TimeSpan? Position,
    TimeSpan? Duration,
    string? Message,
    DateTimeOffset ReceivedUtc);

public interface IPresenterRecoveryService
{
    Task ReloadCurrentAsync(bool autoPlay, CancellationToken cancellationToken = default);
    Task FailCurrentAndAdvanceAsync(TimeSpan? actualPosition, CancellationToken cancellationToken = default);
}

public interface IPowerManagementService
{
    Task ApplyAsync(bool preventDisplaySleep, bool preventSystemSleep, CancellationToken cancellationToken = default);
    Task ReleaseAsync(CancellationToken cancellationToken = default);
}

public sealed record DisplayMonitor(
    string DeviceName,
    string FriendlyName,
    int X,
    int Y,
    int Width,
    int Height,
    bool IsPrimary);

public sealed record MediaProbeResult(
    MediaProbeStatus ProbeStatus,
    MediaPlaybackStatus PlaybackStatus,
    TimeSpan? Duration,
    string? Container,
    string? VideoCodec,
    int? VideoWidth,
    int? VideoHeight,
    double? FrameRate,
    string? AudioCodec,
    int? AudioChannels,
    string? Error);

public interface IPresenterGateway
{
    Task LoadLocalVideoAsync(int mediaId, TimeSpan? start, TimeSpan? end, bool autoPlay, CancellationToken cancellationToken = default);
    Task LoadYouTubeVideoAsync(string videoId, TimeSpan? start, TimeSpan? end, bool autoPlay, CancellationToken cancellationToken = default);
    Task PlayAsync(CancellationToken cancellationToken = default);
    Task PauseAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default);
    Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default);
    Task ShowNewsAsync(NewsItem item, CancellationToken cancellationToken = default);
    Task HideNewsAsync(CancellationToken cancellationToken = default);
}

public interface IPlaybackStore
{
    Task<IReadOnlyList<QueueEntry>> GetQueueAsync(CancellationToken cancellationToken = default);
    Task<QueueEntry> AddQueueEntryAsync(QueueEntry entry, CancellationToken cancellationToken = default);
    Task UpdateQueueEntryAsync(QueueEntry entry, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PlaybackHistory>> GetHistoryAsync(CancellationToken cancellationToken = default);
    Task<PlaybackHistory> AddHistoryAsync(PlaybackHistory entry, CancellationToken cancellationToken = default);
    Task UpdateHistoryAsync(PlaybackHistory entry, CancellationToken cancellationToken = default);
    Task ClearHistoryAsync(CancellationToken cancellationToken = default);
}

public interface INewsService
{
    Task<IReadOnlyList<NewsItem>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<NewsItem> AddAsync(NewsItem item, CancellationToken cancellationToken = default);
    Task UpdateAsync(NewsItem item, CancellationToken cancellationToken = default);
    Task DeleteAsync(long id, CancellationToken cancellationToken = default);
}

public interface IPlaybackCommandService
{
    Task<QueueEntry> PlayNextAsync(int mediaId, TimeSpan? start = null, TimeSpan? duration = null, CancellationToken cancellationToken = default);
    Task<QueueEntry> PlayNowAsync(int mediaId, TimeSpan? currentPosition, TimeSpan? start = null, TimeSpan? duration = null, CancellationToken cancellationToken = default);
    Task<QueueEntry> PlayYouTubeNextAsync(string url, TimeSpan? start = null, TimeSpan? duration = null, TimeSpan? maximumDuration = null, CancellationToken cancellationToken = default);
    Task<QueueEntry> PlayYouTubeNowAsync(string url, TimeSpan? currentPosition, TimeSpan? start = null, TimeSpan? duration = null, TimeSpan? maximumDuration = null, CancellationToken cancellationToken = default);
    Task<QueueEntry?> AdvanceAsync(TimeSpan? actualPosition, bool successful = true, CancellationToken cancellationToken = default);
}

public interface IPresenterControlService
{
    Task ActivateAsync(CancellationToken cancellationToken = default);
    Task PauseAsync(CancellationToken cancellationToken = default);
    Task HideAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public interface INewsCommandService
{
    Task ShowNewsAsync(NewsItem item, CancellationToken cancellationToken = default);
    Task StopNewsAsync(long? newsId = null, CancellationToken cancellationToken = default);
}

public sealed record PresenterStatus(PresenterState State, string Version, string WebUrl);
