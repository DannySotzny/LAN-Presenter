using BeamerPresenter.Domain;
using Microsoft.EntityFrameworkCore;

namespace BeamerPresenter.Infrastructure;

public sealed class PresenterDbContext(DbContextOptions<PresenterDbContext> options) : DbContext(options)
{
    public DbSet<PresenterSettings> Settings => Set<PresenterSettings>();
    public DbSet<VideoAsset> Videos => Set<VideoAsset>();
    public DbSet<MediaFolder> MediaFolders => Set<MediaFolder>();
    public DbSet<QueueEntry> QueueEntries => Set<QueueEntry>();
    public DbSet<PlaybackHistory> PlaybackHistory => Set<PlaybackHistory>();
    public DbSet<NewsItem> NewsItems => Set<NewsItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PresenterSettings>().HasKey(x => x.Id);
        modelBuilder.Entity<PresenterSettings>().Property(x => x.MediaFolder).HasMaxLength(1024);
        modelBuilder.Entity<PresenterSettings>().Property(x => x.FfprobePath).HasMaxLength(4096);
        modelBuilder.Entity<PresenterSettings>().Property(x => x.ChromePath).HasMaxLength(4096);
        modelBuilder.Entity<PresenterSettings>().Property(x => x.MonitorDeviceName).HasMaxLength(128);
        modelBuilder.Entity<PresenterSettings>().Property(x => x.AlwaysOnTop).HasDefaultValue(true);
        modelBuilder.Entity<PresenterSettings>().Property(x => x.PreventDisplaySleep).HasDefaultValue(true);
        modelBuilder.Entity<PresenterSettings>().Property(x => x.PreventSystemSleep).HasDefaultValue(true);
        modelBuilder.Entity<PresenterSettings>().Property(x => x.ShortVideoThresholdSeconds).HasDefaultValue(PresenterSettings.DefaultShortVideoThresholdSeconds);
        modelBuilder.Entity<PresenterSettings>().Property(x => x.ClipLengthMinSeconds).HasDefaultValue(PresenterSettings.DefaultClipLengthMinSeconds);
        modelBuilder.Entity<PresenterSettings>().Property(x => x.ClipLengthMaxSeconds).HasDefaultValue(PresenterSettings.DefaultClipLengthMaxSeconds);
        modelBuilder.Entity<PresenterSettings>().Property(x => x.VideoCooldownCount).HasDefaultValue(PresenterSettings.DefaultVideoCooldownCount);
        modelBuilder.Entity<PresenterSettings>().Property(x => x.TimeCooldownMinutes).HasDefaultValue(PresenterSettings.DefaultTimeCooldownMinutes);
        modelBuilder.Entity<PresenterSettings>().Property(x => x.QueueTargetLength).HasDefaultValue(PresenterSettings.DefaultQueueTargetLength);
        modelBuilder.Entity<VideoAsset>().HasIndex(x => x.FullPath).IsUnique();
        modelBuilder.Entity<VideoAsset>().HasIndex(x => x.YouTubeSourceKey).IsUnique();
        modelBuilder.Entity<VideoAsset>().Property(x => x.YouTubeSourceKey).HasMaxLength(19);
        modelBuilder.Entity<VideoAsset>().Property(x => x.FileName).HasMaxLength(260);
        modelBuilder.Entity<VideoAsset>().Property(x => x.FullPath).HasMaxLength(4096).UseCollation("NOCASE");
        modelBuilder.Entity<VideoAsset>().Property(x => x.Container).HasMaxLength(256);
        modelBuilder.Entity<VideoAsset>().Property(x => x.VideoCodec).HasMaxLength(128);
        modelBuilder.Entity<VideoAsset>().Property(x => x.AudioCodec).HasMaxLength(128);
        modelBuilder.Entity<VideoAsset>().Property(x => x.ProbeError).HasMaxLength(4096);
        modelBuilder.Entity<VideoAsset>().Property(x => x.Enabled).HasDefaultValue(true);
        modelBuilder.Entity<MediaFolder>().HasIndex(x => x.Path).IsUnique();
        modelBuilder.Entity<MediaFolder>().Property(x => x.Path).HasMaxLength(4096).UseCollation("NOCASE");
        modelBuilder.Entity<QueueEntry>().HasIndex(x => new { x.Status, x.SortOrder });
        modelBuilder.Entity<QueueEntry>().Property(x => x.ExternalSourceKey).HasMaxLength(2048);
        modelBuilder.Entity<PlaybackHistory>().HasIndex(x => new { x.MediaId, x.StartedUtc });
        modelBuilder.Entity<PlaybackHistory>().HasIndex(x => new { x.ExternalSourceKey, x.StartedUtc });
        modelBuilder.Entity<PlaybackHistory>().Property(x => x.ExternalSourceKey).HasMaxLength(128);
        modelBuilder.Entity<NewsItem>().Property(x => x.Title).HasMaxLength(200);
        modelBuilder.Entity<NewsItem>().Property(x => x.Text).HasMaxLength(4000);
        modelBuilder.Entity<NewsItem>().HasIndex(x => new { x.ValidFrom, x.ValidUntil, x.Priority });
    }
}
