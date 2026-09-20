namespace BeamerPresenter.Domain;

public enum PresenterState
{
    Stopped,
    Paused,
    Active,
    Hidden
}

public sealed class PresenterSettings
{
    public const int DefaultWebPort = 8765;

    public int Id { get; set; } = 1;
    public int WebPort { get; set; } = DefaultWebPort;
    public bool AllowLanAccess { get; set; } = true;
    public string MediaFolder { get; set; } = string.Empty;
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
}

public sealed class MediaFolder
{
    public int Id { get; set; }
    public required string Path { get; set; }
    public bool IncludeSubdirectories { get; set; } = true;
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}
