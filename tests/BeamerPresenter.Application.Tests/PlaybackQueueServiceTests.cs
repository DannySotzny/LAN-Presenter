using BeamerPresenter.Application;
using BeamerPresenter.Domain;

namespace BeamerPresenter.Application.Tests;

public sealed class PlaybackQueueServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Automatic_planning_fills_target_without_replacing_manual_entries()
    {
        var store = new MemoryPlaybackStore();
        store.Queue.Add(Entry(1, QueueEntryOrigin.ManualNext, QueueEntryStatus.Pending, 1));
        var service = CreateService(store, [Video(1, TimeSpan.FromHours(1))], queueTargetLength: 3);

        await service.EnsureMinimumAsync();

        var queue = await service.GetQueueAsync();
        Assert.Equal(3, queue.Count(entry => entry.Status == QueueEntryStatus.Pending));
        Assert.Same(store.Queue[0], queue.Single(entry => entry.Origin == QueueEntryOrigin.ManualNext));
        Assert.Equal(2, queue.Count(entry => entry.Origin == QueueEntryOrigin.Automatic));
        Assert.Equal(3, queue.Select(entry => entry.SortOrder).Distinct().Count());
    }

    [Fact]
    public async Task Play_next_moves_existing_pending_entries_back_and_keeps_current_playing()
    {
        var store = new MemoryPlaybackStore();
        store.Queue.Add(Entry(1, QueueEntryOrigin.Automatic, QueueEntryStatus.Playing, 0));
        store.Queue.Add(Entry(2, QueueEntryOrigin.Automatic, QueueEntryStatus.Pending, 1));
        store.Queue.Add(Entry(3, QueueEntryOrigin.Automatic, QueueEntryStatus.Pending, 2));
        var service = CreateService(store, [Video(4, TimeSpan.FromMinutes(9))]);

        var inserted = await service.AddNextAsync(4);

        Assert.Equal(QueueEntryOrigin.ManualNext, inserted.Origin);
        Assert.Equal(1, inserted.SortOrder);
        Assert.Equal(QueueEntryStatus.Playing, store.Queue.Single(entry => entry.MediaId == 1).Status);
        Assert.Equal(2, store.Queue.Single(entry => entry.MediaId == 2).SortOrder);
        Assert.Equal(3, store.Queue.Single(entry => entry.MediaId == 3).SortOrder);
    }

    [Fact]
    public async Task Play_now_records_only_actual_part_and_preserves_future_queue()
    {
        var store = new MemoryPlaybackStore();
        var current = Entry(1, QueueEntryOrigin.Automatic, QueueEntryStatus.Playing, 0);
        current.StartPosition = TimeSpan.FromMinutes(10);
        current.EndPosition = TimeSpan.FromMinutes(20);
        store.Queue.Add(current);
        store.Queue.Add(Entry(2, QueueEntryOrigin.Automatic, QueueEntryStatus.Pending, 1));
        var service = CreateService(store, [Video(3, TimeSpan.FromMinutes(9))]);

        var immediate = await service.StartNowAsync(3, TimeSpan.FromMinutes(13));

        Assert.Equal(QueueEntryStatus.Interrupted, current.Status);
        var history = Assert.Single(store.History);
        Assert.Equal(TimeSpan.FromMinutes(10), history.ActualStart);
        Assert.Equal(TimeSpan.FromMinutes(13), history.ActualEnd);
        Assert.True(history.Interrupted);
        Assert.False(history.Completed);
        Assert.Equal(QueueEntryStatus.Playing, immediate.Status);
        Assert.Equal(QueueEntryOrigin.ManualNow, immediate.Origin);
        Assert.Equal(QueueEntryStatus.Pending, store.Queue.Single(entry => entry.MediaId == 2).Status);
    }

    [Fact]
    public async Task Pending_entries_can_be_reordered_and_removed_without_touching_current()
    {
        var store = new MemoryPlaybackStore();
        store.Queue.Add(Entry(1, QueueEntryOrigin.Automatic, QueueEntryStatus.Playing, 0));
        store.Queue.Add(Entry(2, QueueEntryOrigin.ManualNext, QueueEntryStatus.Pending, 1));
        store.Queue.Add(Entry(3, QueueEntryOrigin.Automatic, QueueEntryStatus.Pending, 2));
        store.Queue.Add(Entry(4, QueueEntryOrigin.Automatic, QueueEntryStatus.Pending, 3));
        var service = CreateService(store, []);

        await service.MoveAsync(4, -1);
        await service.RemoveAsync(3);

        Assert.Equal(QueueEntryStatus.Playing, store.Queue.Single(entry => entry.Id == 1).Status);
        Assert.Equal(QueueEntryStatus.Skipped, store.Queue.Single(entry => entry.Id == 3).Status);
        Assert.Equal(
            [(2L, 1), (4L, 2)],
            store.Queue.Where(entry => entry.Status == QueueEntryStatus.Pending)
                .OrderBy(entry => entry.SortOrder)
                .Select(entry => (entry.Id, entry.SortOrder)));
    }

    [Fact]
    public async Task Pending_entry_can_be_prioritized_as_manual_next()
    {
        var store = new MemoryPlaybackStore();
        store.Queue.Add(Entry(1, QueueEntryOrigin.Automatic, QueueEntryStatus.Pending, 1));
        store.Queue.Add(Entry(2, QueueEntryOrigin.Automatic, QueueEntryStatus.Pending, 2));
        store.Queue.Add(Entry(3, QueueEntryOrigin.Automatic, QueueEntryStatus.Pending, 3));
        var service = CreateService(store, []);

        await service.PrioritizeAsync(3);

        Assert.Equal(
            [(3L, 1), (1L, 2), (2L, 3)],
            store.Queue.Where(entry => entry.Status == QueueEntryStatus.Pending)
                .OrderBy(entry => entry.SortOrder)
                .Select(entry => (entry.Id, entry.SortOrder)));
        Assert.Equal(QueueEntryOrigin.ManualNext, store.Queue.Single(entry => entry.Id == 3).Origin);
    }

    [Fact]
    public async Task Pending_entry_can_interrupt_current_and_start_immediately()
    {
        var store = new MemoryPlaybackStore();
        var current = Entry(1, QueueEntryOrigin.Automatic, QueueEntryStatus.Playing, 0);
        current.StartPosition = TimeSpan.FromMinutes(1);
        current.EndPosition = TimeSpan.FromMinutes(8);
        store.Queue.Add(current);
        store.Queue.Add(Entry(2, QueueEntryOrigin.Automatic, QueueEntryStatus.Pending, 1));
        store.Queue.Add(Entry(3, QueueEntryOrigin.Automatic, QueueEntryStatus.Pending, 2));
        var service = CreateService(store, []);

        var started = await service.StartQueuedNowAsync(3, TimeSpan.FromMinutes(4));

        Assert.Equal(QueueEntryStatus.Interrupted, current.Status);
        Assert.True(Assert.Single(store.History).Interrupted);
        Assert.Equal(QueueEntryStatus.Playing, started.Status);
        Assert.Equal(QueueEntryOrigin.ManualNow, started.Origin);
        Assert.Equal(0, started.SortOrder);
        Assert.Equal(1, store.Queue.Single(entry => entry.Id == 2).SortOrder);
    }

    [Fact]
    public async Task Regeneration_replaces_only_automatic_pending_entries()
    {
        var store = new MemoryPlaybackStore();
        store.Queue.Add(Entry(1, QueueEntryOrigin.ManualNext, QueueEntryStatus.Pending, 1));
        store.Queue.Add(Entry(2, QueueEntryOrigin.Automatic, QueueEntryStatus.Pending, 2));
        var service = CreateService(
            store,
            [Video(1, TimeSpan.FromMinutes(9)), Video(2, TimeSpan.FromMinutes(9)), Video(3, TimeSpan.FromMinutes(9))],
            queueTargetLength: 2);

        await service.RegenerateAsync();

        Assert.Equal(QueueEntryStatus.Pending, store.Queue.Single(entry => entry.Id == 1).Status);
        Assert.Equal(QueueEntryStatus.Skipped, store.Queue.Single(entry => entry.Id == 2).Status);
        var pending = store.Queue.Where(entry => entry.Status == QueueEntryStatus.Pending).OrderBy(entry => entry.SortOrder).ToArray();
        Assert.Equal(2, pending.Length);
        Assert.Equal(QueueEntryOrigin.ManualNext, pending[0].Origin);
        Assert.Equal(QueueEntryOrigin.Automatic, pending[1].Origin);
    }

    [Fact]
    public async Task Playback_history_can_be_read_and_cleared()
    {
        var store = new MemoryPlaybackStore();
        store.History.Add(new PlaybackHistory
        {
            Id = 1,
            MediaId = 1,
            PlannedStart = TimeSpan.Zero,
            PlannedEnd = TimeSpan.FromMinutes(5),
            StartedUtc = Now,
            PlaybackReason = PlaybackReason.Automatic
        });
        var service = CreateService(store, []);

        Assert.Single(await service.GetHistoryAsync());
        await service.ClearHistoryAsync();
        Assert.Empty(await service.GetHistoryAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Queue_move_rejects_invalid_offsets(int offset)
    {
        var service = CreateService(new MemoryPlaybackStore(), []);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.MoveAsync(1, offset));
    }

    [Fact]
    public async Task Disabled_video_cannot_be_added_manually()
    {
        var video = Video(1, TimeSpan.FromMinutes(9));
        video.Enabled = false;
        var service = CreateService(new MemoryPlaybackStore(), [video]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddNextAsync(video.Id));

        Assert.Contains("nicht abspielbar", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Completing_current_starts_first_pending_entry_and_normalizes_order()
    {
        var store = new MemoryPlaybackStore();
        store.Queue.Add(Entry(1, QueueEntryOrigin.Automatic, QueueEntryStatus.Playing, 0));
        store.Queue.Add(Entry(2, QueueEntryOrigin.ManualNext, QueueEntryStatus.Pending, 4));
        store.Queue.Add(Entry(3, QueueEntryOrigin.Automatic, QueueEntryStatus.Pending, 9));
        var service = CreateService(store, [Video(1, TimeSpan.FromMinutes(20))]);

        var next = await service.CompleteCurrentAsync(TimeSpan.FromMinutes(7));

        Assert.Equal(2, next?.MediaId);
        Assert.Equal(QueueEntryStatus.Playing, next?.Status);
        Assert.Equal(0, next?.SortOrder);
        Assert.Equal(1, store.Queue.Single(entry => entry.MediaId == 3).SortOrder);
        Assert.True(Assert.Single(store.History).Completed);
    }

    [Fact]
    public async Task Failed_local_playback_marks_media_and_history_as_error()
    {
        var store = new MemoryPlaybackStore();
        var current = Entry(1, QueueEntryOrigin.Automatic, QueueEntryStatus.Playing, 0);
        current.EndPosition = TimeSpan.FromMinutes(9);
        store.Queue.Add(current);
        var video = Video(1, TimeSpan.FromMinutes(9));
        var service = CreateService(store, [video]);

        await service.CompleteCurrentAsync(TimeSpan.FromMinutes(2), successful: false);

        Assert.Equal(QueueEntryStatus.Failed, current.Status);
        Assert.Equal(MediaPlaybackStatus.Failed, video.PlaybackStatus);
        var history = Assert.Single(store.History);
        Assert.False(history.Completed);
        Assert.False(history.Interrupted);
    }

    [Fact]
    public async Task YouTube_next_normalizes_source_and_uses_maximum_duration()
    {
        var store = new MemoryPlaybackStore();
        var service = CreateService(store, []);

        var entry = await service.AddYouTubeNextAsync(
            "https://youtu.be/dQw4w9WgXcQ?t=30",
            maximumDuration: TimeSpan.FromMinutes(8));

        Assert.Equal(MediaSourceType.YouTube, entry.SourceType);
        Assert.Null(entry.MediaId);
        Assert.Equal("youtube:dQw4w9WgXcQ", entry.ExternalSourceKey);
        Assert.Equal(TimeSpan.Zero, entry.StartPosition);
        Assert.Equal(TimeSpan.FromMinutes(8), entry.EndPosition);
        Assert.Equal(QueueEntryOrigin.ManualNext, entry.Origin);
    }

    [Fact]
    public async Task YouTube_automatic_start_continues_after_used_and_reserved_ranges()
    {
        var store = new MemoryPlaybackStore();
        store.History.Add(new PlaybackHistory
        {
            SourceType = MediaSourceType.YouTube,
            ExternalSourceKey = "youtube:dQw4w9WgXcQ",
            ActualStart = TimeSpan.Zero,
            ActualEnd = TimeSpan.FromMinutes(5),
            StartedUtc = Now.AddHours(-1)
        });
        store.Queue.Add(new QueueEntry
        {
            Id = 1,
            SourceType = MediaSourceType.YouTube,
            ExternalSourceKey = "youtube:dQw4w9WgXcQ",
            StartPosition = TimeSpan.FromMinutes(5),
            EndPosition = TimeSpan.FromMinutes(12),
            Status = QueueEntryStatus.Pending,
            SortOrder = 1,
            CreatedUtc = Now
        });
        var service = CreateService(store, []);

        var entry = await service.AddYouTubeNextAsync(
            "https://youtube.com/watch?v=dQw4w9WgXcQ",
            maximumDuration: TimeSpan.FromMinutes(7));

        Assert.Equal(TimeSpan.FromMinutes(12), entry.StartPosition);
        Assert.Equal(TimeSpan.FromMinutes(19), entry.EndPosition);
    }

    [Fact]
    public async Task YouTube_requires_a_bounded_playback_duration()
    {
        var service = CreateService(new MemoryPlaybackStore(), []);

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.AddYouTubeNextAsync("https://youtu.be/dQw4w9WgXcQ"));

        Assert.Contains("maximale Wiedergabezeit", exception.Message, StringComparison.Ordinal);
    }

    private static PlaybackQueueService CreateService(
        MemoryPlaybackStore store,
        IReadOnlyList<VideoAsset> media,
        int queueTargetLength = 10)
    {
        var settings = new PresenterSettings
        {
            QueueTargetLength = queueTargetLength,
            VideoCooldownCount = 0,
            TimeCooldownMinutes = 0
        };
        return new PlaybackQueueService(
            store,
            new StubMediaLibrary(media),
            new StubSettingsService(settings),
            new MediaSegmentPlanner(new ZeroRandomSource()),
            new FixedTimeProvider(Now));
    }

    private static QueueEntry Entry(int mediaId, QueueEntryOrigin origin, QueueEntryStatus status, int sortOrder) => new()
    {
        Id = mediaId,
        MediaId = mediaId,
        SourceType = MediaSourceType.Local,
        StartPosition = TimeSpan.Zero,
        EndPosition = TimeSpan.FromMinutes(7),
        Origin = origin,
        SortOrder = sortOrder,
        Status = status,
        CreatedUtc = Now.AddMinutes(-10),
        StartedUtc = status == QueueEntryStatus.Playing ? Now.AddMinutes(-5) : null
    };

    private static VideoAsset Video(int id, TimeSpan duration) => new()
    {
        Id = id,
        FileName = $"video-{id}.mp4",
        FullPath = $"C:\\media\\video-{id}.mp4",
        Duration = duration,
        IsAvailable = true,
        PlaybackStatus = MediaPlaybackStatus.Supported
    };

    private sealed class MemoryPlaybackStore : IPlaybackStore
    {
        private long nextQueueId = 1;
        private long nextHistoryId = 1;

        public List<QueueEntry> Queue { get; } = [];
        public List<PlaybackHistory> History { get; } = [];

        public Task<IReadOnlyList<QueueEntry>> GetQueueAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<QueueEntry>>([.. Queue.Where(entry => entry.Status is QueueEntryStatus.Pending or QueueEntryStatus.Playing).OrderBy(entry => entry.SortOrder)]);

        public Task<QueueEntry> AddQueueEntryAsync(QueueEntry entry, CancellationToken cancellationToken = default)
        {
            nextQueueId = Math.Max(nextQueueId, Queue.Select(item => item.Id).DefaultIfEmpty().Max() + 1);
            entry.Id = nextQueueId++;
            Queue.Add(entry);
            return Task.FromResult(entry);
        }

        public Task UpdateQueueEntryAsync(QueueEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<PlaybackHistory>> GetHistoryAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlaybackHistory>>([.. History.OrderByDescending(entry => entry.StartedUtc)]);

        public Task<PlaybackHistory> AddHistoryAsync(PlaybackHistory entry, CancellationToken cancellationToken = default)
        {
            entry.Id = nextHistoryId++;
            History.Add(entry);
            return Task.FromResult(entry);
        }

        public Task UpdateHistoryAsync(PlaybackHistory entry, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ClearHistoryAsync(CancellationToken cancellationToken = default)
        {
            History.Clear();
            return Task.CompletedTask;
        }
    }

    private sealed class StubMediaLibrary(IReadOnlyList<VideoAsset> media) : IMediaLibraryService
    {
        public Task<IReadOnlyList<VideoAsset>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult(media);
        public Task<VideoAsset?> GetByIdAsync(int id, CancellationToken cancellationToken = default) => Task.FromResult(media.SingleOrDefault(asset => asset.Id == id));
        public Task<VideoAsset> AddUploadAsync(string originalFileName, Stream content, long length, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetEnabledAsync(int id, bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReanalyzeAsync(int id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task MarkPlaybackFailedAsync(int id, CancellationToken cancellationToken = default)
        {
            var asset = media.SingleOrDefault(candidate => candidate.Id == id);
            if (asset is not null)
            {
                asset.PlaybackStatus = MediaPlaybackStatus.Failed;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class StubSettingsService(PresenterSettings settings) : IPresenterSettingsService
    {
        public Task<PresenterSettings> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(settings);
        public Task SaveAsync(PresenterSettings value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetWebPasswordAsync(string password, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> VerifyWebPasswordAsync(string password, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> HasWebPasswordAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class ZeroRandomSource : IRandomSource
    {
        public int Next(int exclusiveMaximum) => 0;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
