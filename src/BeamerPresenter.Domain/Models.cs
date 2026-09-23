namespace BeamerPresenter.Domain;

public enum PresenterState
{
    Stopped,
    Paused,
    Active,
    Hidden
}

public enum MediaProbeStatus
{
    Unknown,
    Valid,
    Invalid,
    Unsupported,
    Missing
}

public enum MediaPlaybackStatus
{
    Unknown,
    Supported,
    Unsupported,
    Failed
}

public enum MediaSourceType
{
    Local,
    YouTube
}

public enum PlaybackReason
{
    Automatic,
    ManualNext,
    ManualNow
}

public enum QueueEntryOrigin
{
    Automatic,
    Manual,
    ManualNext,
    ManualNow
}

public enum QueueEntryStatus
{
    Pending,
    Playing,
    Completed,
    Interrupted,
    Failed,
    Skipped
}

public enum NewsMode
{
    SplitScreen,
    Ticker,
    Fullscreen
}

public sealed class PresenterSettings
{
    public const int DefaultWebPort = 8765;
    public const int DefaultShortVideoThresholdSeconds = 10 * 60;
    public const int DefaultClipLengthMinSeconds = 7 * 60;
    public const int DefaultClipLengthMaxSeconds = 10 * 60;
    public const int DefaultVideoCooldownCount = 10;
    public const int DefaultTimeCooldownMinutes = 60;
    public const int DefaultQueueTargetLength = 10;

    public int Id { get; set; } = 1;
    public int WebPort { get; set; } = DefaultWebPort;
    public bool AllowLanAccess { get; set; }
    public string MediaFolder { get; set; } = string.Empty;
    public string? FfprobePath { get; set; }
    public string? ChromePath { get; set; }
    public string? MonitorDeviceName { get; set; }
    public bool AlwaysOnTop { get; set; } = true;
    public bool AggressiveTopmost { get; set; }
    public bool PreventDisplaySleep { get; set; } = true;
    public bool PreventSystemSleep { get; set; } = true;
    public int ShortVideoThresholdSeconds { get; set; } = DefaultShortVideoThresholdSeconds;
    public int ClipLengthMinSeconds { get; set; } = DefaultClipLengthMinSeconds;
    public int ClipLengthMaxSeconds { get; set; } = DefaultClipLengthMaxSeconds;
    public int VideoCooldownCount { get; set; } = DefaultVideoCooldownCount;
    public int TimeCooldownMinutes { get; set; } = DefaultTimeCooldownMinutes;
    public int QueueTargetLength { get; set; } = DefaultQueueTargetLength;
    public string? PasswordHash { get; set; }
    public string? PasswordSalt { get; set; }
}

public sealed class VideoAsset
{
    public int Id { get; set; }
    public string? YouTubeSourceKey { get; set; }
    public required string FileName { get; set; }
    public required string FullPath { get; set; }
    public long FileSize { get; set; }
    public DateTimeOffset AddedAtUtc { get; set; }
    public DateTimeOffset LastWriteUtc { get; set; }
    public DateTimeOffset? LastScannedUtc { get; set; }
    public bool IsAvailable { get; set; } = true;
    public bool Enabled { get; set; } = true;
    public TimeSpan? Duration { get; set; }
    public string? Container { get; set; }
    public string? VideoCodec { get; set; }
    public int? VideoWidth { get; set; }
    public int? VideoHeight { get; set; }
    public double? FrameRate { get; set; }
    public string? AudioCodec { get; set; }
    public int? AudioChannels { get; set; }
    public MediaProbeStatus ProbeStatus { get; set; }
    public MediaPlaybackStatus PlaybackStatus { get; set; }
    public string? ProbeError { get; set; }
}

public sealed class MediaFolder
{
    public int Id { get; set; }
    public required string Path { get; set; }
    public bool IncludeSubdirectories { get; set; } = true;
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PlaybackHistory
{
    public long Id { get; set; }
    public int? MediaId { get; set; }
    public string? ExternalSourceKey { get; set; }
    public MediaSourceType SourceType { get; set; }
    public TimeSpan PlannedStart { get; set; }
    public TimeSpan PlannedEnd { get; set; }
    public TimeSpan? ActualStart { get; set; }
    public TimeSpan? ActualEnd { get; set; }
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset? FinishedUtc { get; set; }
    public bool Completed { get; set; }
    public bool Interrupted { get; set; }
    public PlaybackReason PlaybackReason { get; set; }
}

public sealed class QueueEntry
{
    public long Id { get; set; }
    public MediaSourceType SourceType { get; set; }
    public int? MediaId { get; set; }
    public string? ExternalSourceKey { get; set; }
    public TimeSpan StartPosition { get; set; }
    public TimeSpan EndPosition { get; set; }
    public QueueEntryOrigin Origin { get; set; }
    public int SortOrder { get; set; }
    public QueueEntryStatus Status { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? StartedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
}

public sealed class NewsItem
{
    public long Id { get; set; }
    public required string Title { get; set; }
    public required string Text { get; set; }
    public NewsMode Mode { get; set; }
    public TimeSpan? Duration { get; set; }
    public bool Permanent { get; set; }
    public DateTimeOffset? ValidFrom { get; set; }
    public DateTimeOffset? ValidUntil { get; set; }
    public int Priority { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
}
