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

    [Fact]
    public async Task Reload_current_can_restore_paused_entry_without_autoplay()
    {
        var calls = new List<string>();
        var settings = new StubSettingsService();
        var presenter = new RecordingPresenter(calls);
        var orchestrator = new PlaybackOrchestrator(
            new PlaybackController(),
            new RecordingBrowser(calls),
            presenter,
            settings,
            new RecordingPowerManagement(calls),
            new PlaybackQueueService(
                new RecordingPlaybackStore(calls),
                new EmptyMediaLibrary(),
                settings,
                new MediaSegmentPlanner(new ZeroRandomSource()),
                TimeProvider.System));

        await orchestrator.ReloadCurrentAsync(autoPlay: false);

        Assert.Equal([false], presenter.AutoPlayFlags);
    }

    [Fact]
    public async Task Queued_play_now_stops_current_and_loads_selected_entry()
    {
        var calls = new List<string>();
        var settings = new StubSettingsService();
        var orchestrator = new PlaybackOrchestrator(
            new PlaybackController(),
            new RecordingBrowser(calls),
            new RecordingPresenter(calls),
            settings,
            new RecordingPowerManagement(calls),
            new PlaybackQueueService(
                new QueuedPlaybackStore(calls),
                new EmptyMediaLibrary(),
                settings,
                new MediaSegmentPlanner(new ZeroRandomSource()),
                TimeProvider.System));

        var started = await orchestrator.PlayQueuedNowAsync(2, TimeSpan.FromMinutes(3));

        Assert.Equal(2, started.Id);
        Assert.Equal(
            [
                "store:update:1:Interrupted",
                "store:history:00:03:00",
                "store:update:2:Playing",
                "presenter:stop",
                "presenter:load:2:00:00:00-00:09:00"
            ],
            calls);
    }

    [Fact]
    public async Task YouTube_play_now_stops_current_and_loads_normalized_iframe_source()
    {
        var calls = new List<string>();
        var settings = new StubSettingsService();
        var queue = new PlaybackQueueService(
            new RecordingPlaybackStore(calls),
            new EmptyMediaLibrary(),
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

        await orchestrator.PlayYouTubeNowAsync(
            "https://youtu.be/dQw4w9WgXcQ",
            TimeSpan.FromMinutes(3),
            maximumDuration: TimeSpan.FromMinutes(8));

        Assert.Equal(
            [
                "store:update:Interrupted",
                "store:history:00:03:00",
                "store:add:ManualNow",
                "presenter:stop",
                "presenter:youtube:dQw4w9WgXcQ:00:00:00-00:08:00"
            ],
            calls);
    }

    [Fact]
    public async Task Fullscreen_news_restores_split_screen_without_hiding_ticker()
    {
        var calls = new List<string>();
        var settings = new StubSettingsService();
        var orchestrator = new PlaybackOrchestrator(
            new PlaybackController(),
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
        var ticker = new NewsItem
        {
            Id = 1,
            Title = "Ticker",
            Text = "Turnierstart um 20 Uhr",
            Mode = NewsMode.Ticker,
            Permanent = true,
            Priority = 1
        };
        var fullscreen = new NewsItem
        {
            Id = 2,
            Title = "Wichtig",
            Text = "Jetzt zur Turnierleitung",
            Mode = NewsMode.Fullscreen,
            Permanent = true,
            Priority = 10
        };
        var split = new NewsItem
        {
            Id = 3,
            Title = "50:50",
            Text = "Aufstellung",
            Mode = NewsMode.SplitScreen,
            Permanent = true,
            Priority = 1
        };

        await orchestrator.ShowNewsAsync(ticker);
        await orchestrator.ShowNewsAsync(split);
        await orchestrator.ShowNewsAsync(fullscreen);
        var snapshot = await orchestrator.GetNewsDisplayAsync();
        Assert.Equal(fullscreen.Id, snapshot.Main?.Id);
        Assert.Equal(ticker.Id, snapshot.Ticker?.Id);
        await orchestrator.StopNewsAsync(fullscreen.Id);

        Assert.Equal(
            [
                "presenter:ticker:1",
                "presenter:news:3:SplitScreen",
                "presenter:pause",
                "presenter:news:2:Fullscreen",
                "presenter:hide-news",
                "presenter:play",
                "presenter:news:3:SplitScreen"
            ],
            calls.Where(call => call.StartsWith("presenter:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Timed_news_is_hidden_automatically()
    {
        var calls = new List<string>();
        var settings = new StubSettingsService();
        var presenter = new RecordingPresenter(calls);
        var orchestrator = new PlaybackOrchestrator(
            new PlaybackController(),
            new RecordingBrowser(calls),
            presenter,
            settings,
            new RecordingPowerManagement(calls),
            new PlaybackQueueService(
                new EmptyPlaybackStore(),
                new EmptyMediaLibrary(),
                settings,
                new MediaSegmentPlanner(new ZeroRandomSource()),
                TimeProvider.System));

        await orchestrator.ShowNewsAsync(new NewsItem
        {
            Id = 3,
            Title = "Kurzmeldung",
            Text = "Test",
            Mode = NewsMode.Ticker,
            Duration = TimeSpan.FromMilliseconds(25)
        });
        await presenter.TickerHidden.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(["presenter:ticker:3", "presenter:hide-ticker"], calls.Where(call => call.StartsWith("presenter:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Stopping_ticker_while_fullscreen_leaves_fullscreen_visible()
    {
        var calls = new List<string>();
        var settings = new StubSettingsService();
        var orchestrator = new PlaybackOrchestrator(
            new PlaybackController(),
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
        var ticker = new NewsItem { Id = 10, Title = "Ticker", Text = "Text", Mode = NewsMode.Ticker, Permanent = true };
        var fullscreen = new NewsItem { Id = 11, Title = "Fullscreen", Text = "Text", Mode = NewsMode.Fullscreen, Permanent = true };

        await orchestrator.ShowNewsAsync(ticker);
        await orchestrator.ShowNewsAsync(fullscreen);
        await orchestrator.StopNewsAsync(ticker.Id);
        await orchestrator.StopNewsAsync(fullscreen.Id);

        Assert.Equal(1, calls.Count(call => call == "presenter:ticker:10"));
        Assert.Equal(1, calls.Count(call => call == "presenter:hide-ticker"));
        Assert.Equal("presenter:play", calls[^1]);
    }

    [Fact]
    public async Task Ticker_timeout_does_not_end_split_screen_news()
    {
        var calls = new List<string>();
        var settings = new StubSettingsService();
        var presenter = new RecordingPresenter(calls);
        var orchestrator = new PlaybackOrchestrator(
            new PlaybackController(),
            new RecordingBrowser(calls),
            presenter,
            settings,
            new RecordingPowerManagement(calls),
            new PlaybackQueueService(
                new EmptyPlaybackStore(),
                new EmptyMediaLibrary(),
                settings,
                new MediaSegmentPlanner(new ZeroRandomSource()),
                TimeProvider.System));

        await orchestrator.ShowNewsAsync(new NewsItem
        {
            Id = 20, Title = "Ticker", Text = "Text", Mode = NewsMode.Ticker,
            Duration = TimeSpan.FromMilliseconds(25)
        });
        await orchestrator.ShowNewsAsync(new NewsItem
        {
            Id = 21, Title = "50:50", Text = "Text", Mode = NewsMode.SplitScreen,
            Permanent = true
        });
        await presenter.TickerHidden.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(["presenter:ticker:20", "presenter:news:21:SplitScreen", "presenter:hide-ticker"], calls.Where(call => call.StartsWith("presenter:", StringComparison.Ordinal)));
        await orchestrator.StopNewsAsync();
        Assert.Equal("presenter:hide-news", calls[^1]);
    }

    [Fact]
    public async Task Main_news_timeout_does_not_end_ticker()
    {
        var calls = new List<string>();
        var settings = new StubSettingsService();
        var presenter = new RecordingPresenter(calls);
        var orchestrator = new PlaybackOrchestrator(
            new PlaybackController(),
            new RecordingBrowser(calls),
            presenter,
            settings,
            new RecordingPowerManagement(calls),
            new PlaybackQueueService(
                new EmptyPlaybackStore(),
                new EmptyMediaLibrary(),
                settings,
                new MediaSegmentPlanner(new ZeroRandomSource()),
                TimeProvider.System));

        await orchestrator.ShowNewsAsync(new NewsItem
        {
            Id = 30, Title = "Ticker", Text = "Text", Mode = NewsMode.Ticker,
            Permanent = true
        });
        await orchestrator.ShowNewsAsync(new NewsItem
        {
            Id = 31, Title = "50:50", Text = "Text", Mode = NewsMode.SplitScreen,
            Duration = TimeSpan.FromMilliseconds(25)
        });
        await presenter.NewsHidden.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(["presenter:ticker:30", "presenter:news:31:SplitScreen", "presenter:hide-news"], calls.Where(call => call.StartsWith("presenter:", StringComparison.Ordinal)));
        await orchestrator.StopTickerAsync();
        Assert.Equal("presenter:hide-ticker", calls[^1]);
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
        public Task<bool> IsTopmostAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class RecordingPresenter(List<string> calls) : IPresenterGateway
    {
        public TaskCompletionSource NewsHidden { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource TickerHidden { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<bool> AutoPlayFlags { get; } = [];
        public Task LoadLocalVideoAsync(int mediaId, TimeSpan? start, TimeSpan? end, bool autoPlay, CancellationToken cancellationToken = default)
        {
            AutoPlayFlags.Add(autoPlay);
            calls.Add($"presenter:load:{mediaId}:{start}-{end}");
            return Task.CompletedTask;
        }
        public Task LoadYouTubeVideoAsync(string videoId, TimeSpan? start, TimeSpan? end, bool autoPlay, CancellationToken cancellationToken = default)
        {
            AutoPlayFlags.Add(autoPlay);
            calls.Add($"presenter:youtube:{videoId}:{start}-{end}");
            return Task.CompletedTask;
        }
        public Task PlayAsync(CancellationToken cancellationToken = default) { calls.Add("presenter:play"); return Task.CompletedTask; }
        public Task PauseAsync(CancellationToken cancellationToken = default) { calls.Add("presenter:pause"); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken = default) { calls.Add("presenter:stop"); return Task.CompletedTask; }
        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ShowNewsAsync(NewsItem item, CancellationToken cancellationToken = default)
        {
            calls.Add($"presenter:news:{item.Id}:{item.Mode}");
            return Task.CompletedTask;
        }
        public Task HideNewsAsync(CancellationToken cancellationToken = default)
        {
            calls.Add("presenter:hide-news");
            NewsHidden.TrySetResult();
            return Task.CompletedTask;
        }
        public Task ShowTickerAsync(NewsItem item, CancellationToken cancellationToken = default)
        {
            calls.Add($"presenter:ticker:{item.Id}");
            return Task.CompletedTask;
        }
        public Task HideTickerAsync(CancellationToken cancellationToken = default)
        {
            calls.Add("presenter:hide-ticker");
            TickerHidden.TrySetResult();
            return Task.CompletedTask;
        }
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
        public Task SetEnabledAsync(int id, bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReanalyzeAsync(int id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task MarkPlaybackFailedAsync(int id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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

    private sealed class QueuedPlaybackStore(List<string> calls) : IPlaybackStore
    {
        private readonly List<QueueEntry> queue =
        [
            new QueueEntry
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
            },
            new QueueEntry
            {
                Id = 2,
                MediaId = 2,
                SourceType = MediaSourceType.Local,
                StartPosition = TimeSpan.Zero,
                EndPosition = TimeSpan.FromMinutes(9),
                Origin = QueueEntryOrigin.Automatic,
                SortOrder = 1,
                Status = QueueEntryStatus.Pending,
                CreatedUtc = DateTimeOffset.UtcNow
            }
        ];

        public Task<IReadOnlyList<QueueEntry>> GetQueueAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<QueueEntry>>(queue);

        public Task<QueueEntry> AddQueueEntryAsync(QueueEntry entry, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpdateQueueEntryAsync(QueueEntry entry, CancellationToken cancellationToken = default)
        {
            calls.Add($"store:update:{entry.Id}:{entry.Status}");
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PlaybackHistory>> GetHistoryAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlaybackHistory>>([]);

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
        public Task SetEnabledAsync(int id, bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReanalyzeAsync(int id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task MarkPlaybackFailedAsync(int id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
