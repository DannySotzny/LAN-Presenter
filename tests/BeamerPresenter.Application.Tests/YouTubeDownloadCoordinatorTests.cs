using BeamerPresenter.Application;
using BeamerPresenter.Domain;

namespace BeamerPresenter.Application.Tests;

public sealed class YouTubeDownloadCoordinatorTests
{
    private const string VideoId = "M7lc1UVf-VE";
    private const string Url = "https://youtu.be/M7lc1UVf-VE";

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(5L * 1024 * 1024 * 1024, true)]
    [InlineData(5L * 1024 * 1024 * 1024 + 1, false)]
    public void Size_limit_rejects_empty_and_oversized_downloads(long bytes, bool accepted) =>
        Assert.Equal(accepted, YouTubeDownloadLimits.IsValidSize(bytes));

    [Fact]
    public async Task Concurrent_requests_share_download_and_pending_now_plays_local_asset_after_analysis()
    {
        using var fixture = new Fixture();
        fixture.Tool.Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = fixture.CreateCoordinator();

        await coordinator.StartAsync(Url);
        await coordinator.StartAsync("https://www.youtube.com/watch?v=" + VideoId);
        await coordinator.SetIntentAsync(VideoId, new YouTubeDownloadIntent(YouTubeDownloadAction.Now,
            YouTubeDownloadMode.Automatic, null, null, TimeSpan.FromSeconds(30)));
        await UntilAsync(() => fixture.Tool.Calls == 1);
        Assert.Equal(1, fixture.Tool.Calls);
        Assert.Empty(Directory.GetFiles(fixture.MediaDirectory));

        fixture.Tool.Block.SetResult();
        await UntilAsync(async () => (await coordinator.GetAsync(VideoId)).Phase == YouTubeDownloadPhase.Ready);
        Assert.Single(Directory.GetFiles(fixture.MediaDirectory, "*.mp4"));
        Assert.Equal(1, fixture.Playback.NowCalls);
        Assert.Equal(TimeSpan.FromSeconds(30), fixture.Playback.LastDuration);
        Assert.Equal(fixture.Store.Asset!.Id, fixture.Playback.LastMediaId);
    }

    [Fact]
    public async Task Existing_available_file_is_reused_without_changing_added_date()
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.MediaDirectory, "existing.mp4");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        var added = DateTimeOffset.UtcNow.AddDays(-5);
        fixture.Store.Asset = Fixture.MakeAsset(path, added);
        await using var coordinator = fixture.CreateCoordinator();

        await coordinator.StartAsync(Url);
        await UntilAsync(async () => (await coordinator.GetAsync(VideoId)).Phase == YouTubeDownloadPhase.Ready);

        Assert.Equal(0, fixture.Tool.Calls);
        Assert.Equal(added, fixture.Store.Asset.AddedAtUtc);
    }

    [Theory]
    [InlineData(true, true, true, MediaPlaybackStatus.Supported, YouTubeDownloadPhase.Ready)]
    [InlineData(false, true, true, MediaPlaybackStatus.Supported, YouTubeDownloadPhase.NotStarted)]
    [InlineData(true, false, true, MediaPlaybackStatus.Supported, YouTubeDownloadPhase.NotStarted)]
    [InlineData(true, true, false, MediaPlaybackStatus.Supported, YouTubeDownloadPhase.NotStarted)]
    [InlineData(true, true, true, MediaPlaybackStatus.Unsupported, YouTubeDownloadPhase.NotStarted)]
    public async Task Download_status_is_ready_only_for_available_enabled_playable_file(
        bool available, bool enabled, bool fileExists, MediaPlaybackStatus playbackStatus, YouTubeDownloadPhase expectedPhase)
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.MediaDirectory, "existing.mp4");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        fixture.Store.Asset = Fixture.MakeAsset(path, DateTimeOffset.UtcNow);
        if (!fileExists) File.Delete(path);
        fixture.Store.Asset.IsAvailable = available;
        fixture.Store.Asset.Enabled = enabled;
        fixture.Store.Asset.PlaybackStatus = playbackStatus;
        await using var coordinator = fixture.CreateCoordinator();

        var snapshot = await coordinator.GetAsync(VideoId);

        Assert.Equal(expectedPhase, snapshot.Phase);
        Assert.Equal(expectedPhase == YouTubeDownloadPhase.Ready ? fixture.Store.Asset.Id : null, snapshot.MediaId);
    }

    [Fact]
    public async Task Missing_destination_folder_fails_before_downloader_runs()
    {
        using var fixture = new Fixture();
        fixture.Folders.Folders.Clear();
        await using var coordinator = fixture.CreateCoordinator();

        await coordinator.StartAsync(Url);
        await UntilAsync(async () => (await coordinator.GetAsync(VideoId)).Phase == YouTubeDownloadPhase.Failed);

        Assert.Contains("verfügbaren Videoordner", (await coordinator.GetAsync(VideoId)).Error);
        Assert.Equal(0, fixture.Tool.Calls);
        Assert.False(Directory.Exists(fixture.DownloadDirectory));
    }

    [Fact]
    public async Task Download_keeps_existing_same_named_media_and_publishes_to_a_unique_path()
    {
        using var fixture = new Fixture();
        var existingPath = Path.Combine(fixture.MediaDirectory, $"YouTube-{VideoId}.mp4");
        await File.WriteAllTextAsync(existingPath, "existing user media");
        await using var coordinator = fixture.CreateCoordinator();

        await coordinator.StartAsync(Url);
        await UntilAsync(async () => (await coordinator.GetAsync(VideoId)).Phase == YouTubeDownloadPhase.Ready);

        Assert.Equal("existing user media", await File.ReadAllTextAsync(existingPath));
        Assert.NotEqual(existingPath, fixture.Store.Asset!.FullPath);
        Assert.True(File.Exists(fixture.Store.Asset.FullPath));
        Assert.Equal(2, Directory.GetFiles(fixture.MediaDirectory, "*.mp4").Length);
    }

    [Fact]
    public async Task Next_intent_applies_custom_segment_after_download_analysis()
    {
        using var fixture = new Fixture();
        fixture.Tool.Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = fixture.CreateCoordinator();

        await coordinator.SetIntentAsync(VideoId, new YouTubeDownloadIntent(YouTubeDownloadAction.Next,
            YouTubeDownloadMode.Custom, TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(17), null));
        await UntilAsync(() => fixture.Tool.Calls == 1);
        fixture.Tool.Block.SetResult();
        await UntilAsync(async () => (await coordinator.GetAsync(VideoId)).Phase == YouTubeDownloadPhase.Ready);

        Assert.Equal(1, fixture.Playback.NextCalls);
        Assert.Equal(0, fixture.Playback.NowCalls);
        Assert.Equal(TimeSpan.FromSeconds(12), fixture.Playback.LastStart);
        Assert.Equal(TimeSpan.FromSeconds(17), fixture.Playback.LastDuration);
    }

    [Fact]
    public async Task Failed_download_can_be_retried_without_reusing_failed_snapshot()
    {
        using var fixture = new Fixture();
        fixture.Tool.ReturnOutsidePath = true;
        await using var coordinator = fixture.CreateCoordinator();

        await coordinator.StartAsync(Url);
        await UntilAsync(async () => (await coordinator.GetAsync(VideoId)).Phase == YouTubeDownloadPhase.Failed);
        fixture.Tool.ReturnOutsidePath = false;
        var retry = await coordinator.StartAsync(Url);
        await UntilAsync(async () => (await coordinator.GetAsync(VideoId)).Phase == YouTubeDownloadPhase.Ready);

        Assert.NotEqual(YouTubeDownloadPhase.Failed, retry.Phase);
        Assert.Equal(2, fixture.Tool.Calls);
        Assert.Single(Directory.GetFiles(fixture.MediaDirectory, "*.mp4"));
    }

    [Fact]
    public async Task Missing_source_file_is_downloaded_again_and_published_only_after_probe_approval()
    {
        using var fixture = new Fixture();
        fixture.Store.Asset = new VideoAsset
        {
            Id = 23,
            YouTubeSourceKey = "youtube:" + VideoId,
            FileName = "missing.mp4",
            FullPath = Path.Combine(fixture.MediaDirectory, "missing.mp4"),
            AddedAtUtc = DateTimeOffset.UtcNow.AddDays(-10),
            IsAvailable = true,
            Enabled = true,
            ProbeStatus = MediaProbeStatus.Valid,
            PlaybackStatus = MediaPlaybackStatus.Supported
        };
        fixture.Probe.Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = fixture.CreateCoordinator();

        await coordinator.StartAsync(Url);
        await UntilAsync(async () => (await coordinator.GetAsync(VideoId)).Phase == YouTubeDownloadPhase.Analyzing);
        Assert.Empty(Directory.GetFiles(fixture.MediaDirectory));

        fixture.Probe.Block.SetResult();
        await UntilAsync(async () => (await coordinator.GetAsync(VideoId)).Phase == YouTubeDownloadPhase.Ready);
        Assert.Equal(1, fixture.Tool.Calls);
        Assert.Single(Directory.GetFiles(fixture.MediaDirectory, "*.mp4"));
        Assert.True(fixture.Store.Asset!.AddedAtUtc > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task Invalid_download_path_and_failed_probe_never_publish_file()
    {
        using var fixture = new Fixture();
        fixture.Tool.ReturnOutsidePath = true;
        await using (var coordinator = fixture.CreateCoordinator())
        {
            await coordinator.StartAsync(Url);
            await UntilAsync(async () => (await coordinator.GetAsync(VideoId)).Phase == YouTubeDownloadPhase.Failed);
            Assert.Contains("ungültigen Dateipfad", (await coordinator.GetAsync(VideoId)).Error);
            Assert.Empty(Directory.GetFiles(fixture.MediaDirectory));
        }

        fixture.Tool.ReturnOutsidePath = false;
        fixture.Probe.Result = new MediaProbeResult(MediaProbeStatus.Valid, MediaPlaybackStatus.Unsupported,
            TimeSpan.FromMinutes(1), "mp4", "vp9", 1920, 1080, 30, "opus", 2, "Nicht browserkompatibel");
        await using (var coordinator = fixture.CreateCoordinator())
        {
            await coordinator.StartAsync(Url);
            await UntilAsync(async () => (await coordinator.GetAsync(VideoId)).Phase == YouTubeDownloadPhase.Failed);
            Assert.Equal("Nicht browserkompatibel", (await coordinator.GetAsync(VideoId)).Error);
            Assert.Empty(Directory.GetFiles(fixture.MediaDirectory));
        }
    }

    [Fact]
    public async Task Shutting_down_during_download_cancels_work_and_removes_staged_video()
    {
        using var fixture = new Fixture();
        fixture.Tool.Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = fixture.CreateCoordinator();

        await coordinator.StartAsync(Url);
        await UntilAsync(() => fixture.Tool.Calls == 1);
        await coordinator.DisposeAsync();

        var snapshot = await coordinator.GetAsync(VideoId);
        Assert.Equal(YouTubeDownloadPhase.Failed, snapshot.Phase);
        Assert.Contains("beim Beenden der App abgebrochen", snapshot.Error);
        Assert.Empty(Directory.Exists(fixture.DownloadDirectory)
            ? Directory.GetFiles(fixture.DownloadDirectory, "*", SearchOption.AllDirectories)
            : []);
    }

    private static async Task UntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!predicate()) { await Task.Delay(20, timeout.Token); }
    }

    private static async Task UntilAsync(Func<Task<bool>> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await predicate()) { await Task.Delay(20, timeout.Token); }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "PresenterYouTubeCoordinator", Guid.NewGuid().ToString("N"));
        public string MediaDirectory { get; }
        public string DownloadDirectory => Path.Combine(root, "Data");
        public FakeTool Tool { get; } = new();
        public FakeStore Store { get; } = new();
        public FakeProbe Probe { get; } = new();
        public FakePlayback Playback { get; } = new();

        public Fixture()
        {
            MediaDirectory = Path.Combine(root, "Media");
            Directory.CreateDirectory(MediaDirectory);
            Folders = new FakeFolders(MediaDirectory);
        }

        public FakeFolders Folders { get; }

        public YouTubeDownloadCoordinator CreateCoordinator() => new(Tool, Store, Folders,
            new FakeScanner(), Probe, new YouTubeDownloadPlaybackContext(Playback, new FakeTelemetry()),
            Path.Combine(root, "Data"));

        public static VideoAsset MakeAsset(string path, DateTimeOffset added) => new()
        {
            Id = 23,
            YouTubeSourceKey = "youtube:" + VideoId,
            FileName = Path.GetFileName(path),
            FullPath = path,
            FileSize = new FileInfo(path).Length,
            AddedAtUtc = added,
            IsAvailable = true,
            Enabled = true,
            Duration = TimeSpan.FromMinutes(1),
            ProbeStatus = MediaProbeStatus.Valid,
            PlaybackStatus = MediaPlaybackStatus.Supported
        };

        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private sealed class FakeTool : IYouTubeDownloadTool
    {
        public int Calls;
        public bool ReturnOutsidePath;
        public TaskCompletionSource? Block;
        public async Task<string> DownloadAsync(string videoId, string stagingDirectory,
            Action<YouTubeDownloadPhase> reportPhase, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            reportPhase(YouTubeDownloadPhase.Downloading);
            if (Block is not null) await Block.Task.WaitAsync(cancellationToken);
            var path = ReturnOutsidePath ? Path.Combine(Path.GetTempPath(), "outside.mp4") :
                Path.Combine(stagingDirectory, "download.mp4");
            if (!ReturnOutsidePath) await File.WriteAllBytesAsync(path, [1, 2, 3], cancellationToken);
            return path;
        }
    }

    private sealed class FakeStore : IYouTubeMediaStore
    {
        public VideoAsset? Asset;
        public Task<VideoAsset?> GetBySourceKeyAsync(string sourceKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(Asset?.YouTubeSourceKey == sourceKey ? Asset : null);

        public Task<VideoAsset> RegisterDownloadedAsync(string sourceKey, string fullPath, CancellationToken cancellationToken = default)
        {
            Asset = new VideoAsset
            {
                Id = 23,
                YouTubeSourceKey = sourceKey,
                FullPath = fullPath,
                FileName = Path.GetFileName(fullPath),
                FileSize = new FileInfo(fullPath).Length,
                AddedAtUtc = DateTimeOffset.UtcNow,
                Duration = TimeSpan.FromMinutes(1),
                ProbeStatus = MediaProbeStatus.Valid,
                PlaybackStatus = MediaPlaybackStatus.Supported
            };
            return Task.FromResult(Asset);
        }
    }

    private sealed class FakeFolders(string path) : IMediaFolderService
    {
        public List<MediaFolder> Folders { get; } = [new MediaFolder { Id = 1, Path = path }];
        public Task<IReadOnlyList<MediaFolder>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MediaFolder>>(Folders);
        public Task<MediaFolder> AddAsync(string path, bool includeSubdirectories, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveAsync(int id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeScanner : IMediaScanner
    {
        public Task<MediaScanResult> ScanAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new MediaScanResult(1, 0, 0, 0));
    }

    private sealed class FakeProbe : IFfprobeService
    {
        public TaskCompletionSource? Block;
        public MediaProbeResult Result = new(MediaProbeStatus.Valid, MediaPlaybackStatus.Supported,
            TimeSpan.FromMinutes(1), "mp4", "h264", 1920, 1080, 30, "aac", 2, null);
        public async Task<MediaProbeResult> ProbeAsync(string mediaPath, CancellationToken cancellationToken = default)
        {
            if (Block is not null) await Block.Task.WaitAsync(cancellationToken);
            return Result;
        }
        public Task<FfprobeAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<FfprobeAvailability> InstallWithWinGetAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeTelemetry : IPresenterTelemetry
    {
        public PresenterTelemetrySnapshot Current { get; } = new(false, "Stopped", TimeSpan.FromSeconds(12), null, null, DateTimeOffset.UtcNow);
    }

    private sealed class FakePlayback : IPlaybackCommandService
    {
        public int NowCalls;
        public int NextCalls;
        public int? LastMediaId;
        public TimeSpan? LastStart;
        public TimeSpan? LastDuration;
        public Task<QueueEntry> PlayNowAsync(int mediaId, TimeSpan? currentPosition, TimeSpan? start = null,
            TimeSpan? duration = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref NowCalls);
            LastMediaId = mediaId;
            LastStart = start;
            LastDuration = duration;
            return Task.FromResult(new QueueEntry());
        }
        public Task<QueueEntry> PlayNextAsync(int mediaId, TimeSpan? start = null, TimeSpan? duration = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref NextCalls);
            LastMediaId = mediaId;
            LastStart = start;
            LastDuration = duration;
            return Task.FromResult(new QueueEntry());
        }
        public Task<QueueEntry> PlayYouTubeNextAsync(string url, TimeSpan? start = null, TimeSpan? duration = null, TimeSpan? maximumDuration = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<QueueEntry> PlayYouTubeNowAsync(string url, TimeSpan? currentPosition, TimeSpan? start = null, TimeSpan? duration = null, TimeSpan? maximumDuration = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task PrioritizeQueuedAsync(long queueEntryId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<QueueEntry> PlayQueuedNowAsync(long queueEntryId, TimeSpan? currentPosition, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<QueueEntry?> AdvanceAsync(TimeSpan? actualPosition, bool successful = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
