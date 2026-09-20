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

public sealed class PresenterSettings
{
    public const int DefaultWebPort = 8765;

    public int Id { get; set; } = 1;
    public int WebPort { get; set; } = DefaultWebPort;
    public bool AllowLanAccess { get; set; } = true;
    public string MediaFolder { get; set; } = string.Empty;
    public string? FfprobePath { get; set; }
    public string? PasswordHash { get; set; }
    public string? PasswordSalt { get; set; }
}

public sealed class VideoAsset
{
    public int Id { get; set; }
    public required string FileName { get; set; }
    public required string FullPath { get; set; }
    public long FileSize { get; set; }
    public DateTimeOffset AddedAtUtc { get; set; }
    public DateTimeOffset LastWriteUtc { get; set; }
    public DateTimeOffset? LastScannedUtc { get; set; }
    public bool IsAvailable { get; set; } = true;
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
