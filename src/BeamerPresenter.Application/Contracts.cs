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

public sealed record PresenterStatus(PresenterState State, string Version, string WebUrl);
