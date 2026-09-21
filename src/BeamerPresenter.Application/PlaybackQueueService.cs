using BeamerPresenter.Domain;

namespace BeamerPresenter.Application;

public sealed class PlaybackQueueService(
    IPlaybackStore store,
    IMediaLibraryService mediaLibrary,
    IPresenterSettingsService settingsService,
    MediaSegmentPlanner segmentPlanner,
    TimeProvider timeProvider)
{
    private readonly SemaphoreSlim commandGate = new(1, 1);

    public Task<IReadOnlyList<QueueEntry>> GetQueueAsync(CancellationToken cancellationToken = default) =>
        store.GetQueueAsync(cancellationToken);

    public Task EnsureMinimumAsync(CancellationToken cancellationToken = default) =>
        ExecuteSerializedAsync(async () =>
        {
            var settings = await settingsService.GetAsync(cancellationToken);
            var options = PlaybackPlanningOptions.From(settings);
            var media = await mediaLibrary.GetAllAsync(cancellationToken);
            var queue = (await store.GetQueueAsync(cancellationToken)).ToList();
            var planningHistory = (await store.GetHistoryAsync(cancellationToken)).ToList();
            planningHistory.AddRange(queue.Select(ToReservedHistory));
            var pendingCount = queue.Count(entry => entry.Status == QueueEntryStatus.Pending);
            var nextSortOrder = queue.Where(entry => entry.Status == QueueEntryStatus.Pending)
                .Select(entry => entry.SortOrder)
                .DefaultIfEmpty(0)
                .Max() + 1;

            while (pendingCount < options.QueueTargetLength)
            {
                var plan = segmentPlanner.Plan(media, planningHistory, options, timeProvider.GetUtcNow());
                if (plan is null)
                {
                    break;
                }

                var entry = await store.AddQueueEntryAsync(new QueueEntry
                {
                    SourceType = MediaSourceType.Local,
                    MediaId = plan.MediaId,
                    StartPosition = plan.Start,
                    EndPosition = plan.End,
                    Origin = QueueEntryOrigin.Automatic,
                    SortOrder = nextSortOrder++,
                    Status = QueueEntryStatus.Pending,
                    CreatedUtc = timeProvider.GetUtcNow()
                }, cancellationToken);
                queue.Add(entry);
                planningHistory.Add(ToReservedHistory(entry));
                pendingCount++;
            }
        }, cancellationToken);

    public Task<QueueEntry> AddNextAsync(
        int mediaId,
        TimeSpan? start = null,
        TimeSpan? duration = null,
        CancellationToken cancellationToken = default) =>
        ExecuteSerializedAsync(async () =>
        {
            var segment = await ResolveManualSegmentAsync(mediaId, start, duration, cancellationToken);
            var queue = await store.GetQueueAsync(cancellationToken);
            foreach (var entry in queue.Where(entry => entry.Status == QueueEntryStatus.Pending))
            {
                entry.SortOrder++;
                await store.UpdateQueueEntryAsync(entry, cancellationToken);
            }

            return await store.AddQueueEntryAsync(CreateQueueEntry(segment, QueueEntryOrigin.ManualNext, QueueEntryStatus.Pending, 1), cancellationToken);
        }, cancellationToken);

    public Task<QueueEntry> StartNowAsync(
        int mediaId,
        TimeSpan? currentPosition,
        TimeSpan? start = null,
        TimeSpan? duration = null,
        CancellationToken cancellationToken = default) =>
        ExecuteSerializedAsync(async () =>
        {
            var segment = await ResolveManualSegmentAsync(mediaId, start, duration, cancellationToken);
            var queue = await store.GetQueueAsync(cancellationToken);
            var current = queue.SingleOrDefault(entry => entry.Status == QueueEntryStatus.Playing);
            if (current is not null)
            {
                await FinishEntryAsync(current, currentPosition, QueueEntryStatus.Interrupted, cancellationToken);
            }

            return await store.AddQueueEntryAsync(CreateQueueEntry(segment, QueueEntryOrigin.ManualNow, QueueEntryStatus.Playing, 0), cancellationToken);
        }, cancellationToken);

    public Task<QueueEntry?> CompleteCurrentAsync(
        TimeSpan? actualPosition,
        bool successful = true,
        CancellationToken cancellationToken = default) =>
        ExecuteSerializedAsync(async () =>
        {
            var queue = await store.GetQueueAsync(cancellationToken);
            var current = queue.SingleOrDefault(entry => entry.Status == QueueEntryStatus.Playing);
            if (current is not null)
            {
                await FinishEntryAsync(
                    current,
                    actualPosition,
                    successful ? QueueEntryStatus.Completed : QueueEntryStatus.Failed,
                    cancellationToken);
            }

            var next = queue
                .Where(entry => entry.Status == QueueEntryStatus.Pending)
                .OrderBy(entry => entry.SortOrder)
                .ThenBy(entry => entry.Id)
                .FirstOrDefault();
            if (next is null)
            {
                return null;
            }

            next.Status = QueueEntryStatus.Playing;
            next.SortOrder = 0;
            next.StartedUtc = timeProvider.GetUtcNow();
            await store.UpdateQueueEntryAsync(next, cancellationToken);
            var remaining = queue
                .Where(entry => entry.Status == QueueEntryStatus.Pending && entry.Id != next.Id)
                .OrderBy(entry => entry.SortOrder)
                .ThenBy(entry => entry.Id)
                .ToArray();
            for (var index = 0; index < remaining.Length; index++)
            {
                remaining[index].SortOrder = index + 1;
                await store.UpdateQueueEntryAsync(remaining[index], cancellationToken);
            }

            return next;
        }, cancellationToken);

    public Task MarkFailedAsync(QueueEntry entry, CancellationToken cancellationToken = default) =>
        ExecuteSerializedAsync(async () =>
        {
            entry.Status = QueueEntryStatus.Failed;
            entry.CompletedUtc = timeProvider.GetUtcNow();
            await store.UpdateQueueEntryAsync(entry, cancellationToken);
        }, cancellationToken);

    private async Task<PlannedMediaSegment> ResolveManualSegmentAsync(
        int mediaId,
        TimeSpan? start,
        TimeSpan? duration,
        CancellationToken cancellationToken)
    {
        var asset = await mediaLibrary.GetByIdAsync(mediaId, cancellationToken)
            ?? throw new InvalidOperationException("Das ausgewählte Video wurde nicht gefunden.");
        if (!asset.IsAvailable || asset.Duration is null || asset.Duration <= TimeSpan.Zero ||
            asset.PlaybackStatus is MediaPlaybackStatus.Unsupported or MediaPlaybackStatus.Failed)
        {
            throw new InvalidOperationException("Das ausgewählte Video ist nicht abspielbar.");
        }

        if (start.HasValue || duration.HasValue)
        {
            if (!start.HasValue || !duration.HasValue || start.Value < TimeSpan.Zero || duration.Value <= TimeSpan.Zero ||
                start.Value + duration.Value > asset.Duration.Value)
            {
                throw new ArgumentOutOfRangeException(nameof(duration), "Start und Dauer müssen innerhalb des Videos liegen.");
            }

            return new PlannedMediaSegment(mediaId, start.Value, start.Value + duration.Value);
        }

        var settings = await settingsService.GetAsync(cancellationToken);
        var options = PlaybackPlanningOptions.From(settings) with { VideoCooldownCount = 0, TimeCooldownMinutes = 0 };
        var history = (await store.GetHistoryAsync(cancellationToken)).ToList();
        history.AddRange((await store.GetQueueAsync(cancellationToken)).Select(ToReservedHistory));
        return segmentPlanner.Plan([asset], history, options, timeProvider.GetUtcNow())
            ?? throw new InvalidOperationException("Für das ausgewählte Video ist kein freies Segment mehr verfügbar.");
    }

    private async Task FinishEntryAsync(
        QueueEntry entry,
        TimeSpan? actualPosition,
        QueueEntryStatus status,
        CancellationToken cancellationToken)
    {
        var finishedUtc = timeProvider.GetUtcNow();
        var actualEnd = Clamp(actualPosition ?? entry.StartPosition, entry.StartPosition, entry.EndPosition);
        entry.Status = status;
        entry.CompletedUtc = finishedUtc;
        await store.UpdateQueueEntryAsync(entry, cancellationToken);

        if (entry.MediaId.HasValue && actualEnd > entry.StartPosition)
        {
            await store.AddHistoryAsync(new PlaybackHistory
            {
                MediaId = entry.MediaId.Value,
                SourceType = entry.SourceType,
                PlannedStart = entry.StartPosition,
                PlannedEnd = entry.EndPosition,
                ActualStart = entry.StartPosition,
                ActualEnd = actualEnd,
                StartedUtc = entry.StartedUtc ?? entry.CreatedUtc,
                FinishedUtc = finishedUtc,
                Completed = status == QueueEntryStatus.Completed && actualEnd >= entry.EndPosition,
                Interrupted = status == QueueEntryStatus.Interrupted,
                PlaybackReason = ToPlaybackReason(entry.Origin)
            }, cancellationToken);
        }
    }

    private QueueEntry CreateQueueEntry(
        PlannedMediaSegment segment,
        QueueEntryOrigin origin,
        QueueEntryStatus status,
        int sortOrder)
    {
        var now = timeProvider.GetUtcNow();
        return new QueueEntry
        {
            SourceType = MediaSourceType.Local,
            MediaId = segment.MediaId,
            StartPosition = segment.Start,
            EndPosition = segment.End,
            Origin = origin,
            SortOrder = sortOrder,
            Status = status,
            CreatedUtc = now,
            StartedUtc = status == QueueEntryStatus.Playing ? now : null
        };
    }

    private static PlaybackHistory ToReservedHistory(QueueEntry entry) => new()
    {
        MediaId = entry.MediaId ?? 0,
        SourceType = entry.SourceType,
        PlannedStart = entry.StartPosition,
        PlannedEnd = entry.EndPosition,
        ActualStart = entry.StartPosition,
        ActualEnd = entry.EndPosition,
        StartedUtc = entry.StartedUtc ?? entry.CreatedUtc,
        PlaybackReason = ToPlaybackReason(entry.Origin)
    };

    private static PlaybackReason ToPlaybackReason(QueueEntryOrigin origin) => origin switch
    {
        QueueEntryOrigin.ManualNext => PlaybackReason.ManualNext,
        QueueEntryOrigin.ManualNow => PlaybackReason.ManualNow,
        _ => PlaybackReason.Automatic
    };

    private static TimeSpan Clamp(TimeSpan value, TimeSpan minimum, TimeSpan maximum) =>
        value < minimum ? minimum : value > maximum ? maximum : value;

    private async Task ExecuteSerializedAsync(Func<Task> command, CancellationToken cancellationToken)
    {
        await commandGate.WaitAsync(cancellationToken);
        try
        {
            await command();
        }
        finally
        {
            commandGate.Release();
        }
    }

    private async Task<T> ExecuteSerializedAsync<T>(Func<Task<T>> command, CancellationToken cancellationToken)
    {
        await commandGate.WaitAsync(cancellationToken);
        try
        {
            return await command();
        }
        finally
        {
            commandGate.Release();
        }
    }
}
