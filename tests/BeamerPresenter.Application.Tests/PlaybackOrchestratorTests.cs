using BeamerPresenter.Application;
using BeamerPresenter.Domain;

namespace BeamerPresenter.Application.Tests;

public sealed class PlaybackOrchestratorTests
{
    [Fact]
    public async Task Presenter_states_coordinate_browser_realtime_and_power_services()
    {
        var calls = new List<string>();
        var state = new PlaybackController();
        var settings = new StubSettingsService();
        var orchestrator = new PlaybackOrchestrator(
            state,
            new RecordingBrowser(calls),
            new RecordingPresenter(calls),
            settings,
            new RecordingPowerManagement(calls),
            new PlaybackQueueService(
                new EmptyPlaybackStore(),
                new EmptyMediaLibrary(),
                settings,
                new MediaSegmentPlanner(new ZeroRandomSource()),
                TimeProvider.System));

        await orchestrator.ActivateAsync();
        Assert.Equal(PresenterState.Active, state.State);
        await orchestrator.PauseAsync();
        Assert.Equal(PresenterState.Paused, state.State);
        await orchestrator.HideAsync();
        Assert.Equal(PresenterState.Hidden, state.State);
        await orchestrator.StopAsync();
        Assert.Equal(PresenterState.Stopped, state.State);

        Assert.Equal(
            [
                "browser:start",
                "power:apply:display=True:system=True",
                "presenter:pause",
                "browser:show",
                "presenter:pause",
                "browser:hide",
                "power:release",
                "presenter:stop",
                "browser:stop",
                "power:release"
            ],
            calls);
    }

    [Fact]
    public async Task Play_now_persists_interruption_before_stopping_and_loading_new_video()
    {
        var calls = new List<string>();
        var settings = new StubSettingsService();
        var store = new RecordingPlaybackStore(calls);
        var queue = new PlaybackQueueService(
            store,
            new OneVideoMediaLibrary(),
            settings,
            new MediaSegmentPlanner(new ZeroRandomSource()),
            TimeProvider.System);
        var orchestrator = new PlaybackOrchestrator(
            new PlaybackController(),
            new RecordingBrowser(calls),
            new RecordingPresenter(calls),
            settings,
            new RecordingPowerManagement(calls),
            queue);

        await orchestrator.PlayNowAsync(2, TimeSpan.FromMinutes(3));

        Assert.Equal(
            [
                "store:update:Interrupted",
                "store:history:00:03:00",
                "store:add:ManualNow",
                "presenter:stop",
                "presenter:load:2:00:00:00-00:09:00"
            ],
            calls);
    }

    private sealed class StubSettingsService : IPresenterSettingsService
    {
        private readonly PresenterSettings settings = new() { PreventDisplaySleep = true, PreventSystemSleep = true };
        public Task<PresenterSettings> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(settings);
        public Task SaveAsync(PresenterSettings value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetWebPasswordAsync(string password, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> VerifyWebPasswordAsync(string password, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> HasWebPasswordAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class RecordingBrowser(List<string> calls) : IBrowserController
    {
        public Task StartAsync(CancellationToken cancellationToken = default) { calls.Add("browser:start"); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken = default) { calls.Add("browser:stop"); return Task.CompletedTask; }
        public Task ShowAsync(CancellationToken cancellationToken = default) { calls.Add("browser:show"); return Task.CompletedTask; }
        public Task HideAsync(CancellationToken cancellationToken = default) { calls.Add("browser:hide"); return Task.CompletedTask; }
        public Task<bool> IsRunningAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class RecordingPresenter(List<string> calls) : IPresenterGateway
    {
        public Task LoadLocalVideoAsync(int mediaId, TimeSpan? start, TimeSpan? end, bool autoPlay, CancellationToken cancellationToken = default)
        {
            calls.Add($"presenter:load:{mediaId}:{start}-{end}");
            return Task.CompletedTask;
        }
        public Task PlayAsync(CancellationToken cancellationToken = default) { calls.Add("presenter:play"); return Task.CompletedTask; }
        public Task PauseAsync(CancellationToken cancellationToken = default) { calls.Add("presenter:pause"); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken = default) { calls.Add("presenter:stop"); return Task.CompletedTask; }
        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingPowerManagement(List<string> calls) : IPowerManagementService
    {
        public Task ApplyAsync(bool preventDisplaySleep, bool preventSystemSleep, CancellationToken cancellationToken = default)
        {
            calls.Add($"power:apply:display={preventDisplaySleep}:system={preventSystemSleep}");
            return Task.CompletedTask;
        }

        public Task ReleaseAsync(CancellationToken cancellationToken = default)
        {
            calls.Add("power:release");
            return Task.CompletedTask;
        }
    }

    private sealed class EmptyPlaybackStore : IPlaybackStore
    {
        public Task<IReadOnlyList<QueueEntry>> GetQueueAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<QueueEntry>>([]);
        public Task<QueueEntry> AddQueueEntryAsync(QueueEntry entry, CancellationToken cancellationToken = default) => Task.FromResult(entry);
        public Task UpdateQueueEntryAsync(QueueEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<PlaybackHistory>> GetHistoryAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PlaybackHistory>>([]);
        public Task<PlaybackHistory> AddHistoryAsync(PlaybackHistory entry, CancellationToken cancellationToken = default) => Task.FromResult(entry);
        public Task UpdateHistoryAsync(PlaybackHistory entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearHistoryAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class EmptyMediaLibrary : IMediaLibraryService
    {
        public Task<IReadOnlyList<VideoAsset>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<VideoAsset>>([]);
        public Task<VideoAsset?> GetByIdAsync(int id, CancellationToken cancellationToken = default) => Task.FromResult<VideoAsset?>(null);
        public Task<VideoAsset> AddUploadAsync(string originalFileName, Stream content, long length, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ZeroRandomSource : IRandomSource
    {
        public int Next(int exclusiveMaximum) => 0;
    }

    private sealed class RecordingPlaybackStore(List<string> calls) : IPlaybackStore
    {
        private readonly QueueEntry current = new()
        {
            Id = 1,
            MediaId = 1,
            SourceType = MediaSourceType.Local,
            StartPosition = TimeSpan.Zero,
            EndPosition = TimeSpan.FromMinutes(7),
            Origin = QueueEntryOrigin.Automatic,
            Status = QueueEntryStatus.Playing,
            CreatedUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            StartedUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
        };

        public Task<IReadOnlyList<QueueEntry>> GetQueueAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<QueueEntry>>([current]);

        public Task<QueueEntry> AddQueueEntryAsync(QueueEntry entry, CancellationToken cancellationToken = default)
        {
            calls.Add($"store:add:{entry.Origin}");
            entry.Id = 2;
            return Task.FromResult(entry);
        }

        public Task UpdateQueueEntryAsync(QueueEntry entry, CancellationToken cancellationToken = default)
        {
            calls.Add($"store:update:{entry.Status}");
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PlaybackHistory>> GetHistoryAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PlaybackHistory>>([]);

        public Task<PlaybackHistory> AddHistoryAsync(PlaybackHistory entry, CancellationToken cancellationToken = default)
        {
            calls.Add($"store:history:{entry.ActualEnd}");
            return Task.FromResult(entry);
        }

        public Task UpdateHistoryAsync(PlaybackHistory entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearHistoryAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class OneVideoMediaLibrary : IMediaLibraryService
    {
        private readonly VideoAsset video = new()
        {
            Id = 2,
            FileName = "manual.mp4",
            FullPath = "C:\\media\\manual.mp4",
            Duration = TimeSpan.FromMinutes(9),
            IsAvailable = true,
            PlaybackStatus = MediaPlaybackStatus.Supported
        };

        public Task<IReadOnlyList<VideoAsset>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<VideoAsset>>([video]);
        public Task<VideoAsset?> GetByIdAsync(int id, CancellationToken cancellationToken = default) => Task.FromResult(id == video.Id ? video : null);
        public Task<VideoAsset> AddUploadAsync(string originalFileName, Stream content, long length, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
