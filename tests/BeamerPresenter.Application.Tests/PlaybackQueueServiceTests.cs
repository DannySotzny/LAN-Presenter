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
