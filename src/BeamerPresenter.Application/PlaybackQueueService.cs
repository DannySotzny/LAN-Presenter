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

    public sealed record YouTubeQueueSelection(YouTubeReference Reference, TimeSpan Start, TimeSpan End);

    public Task<IReadOnlyList<QueueEntry>> GetQueueAsync(CancellationToken cancellationToken = default) =>
        store.GetQueueAsync(cancellationToken);

    public Task<IReadOnlyList<PlaybackHistory>> GetHistoryAsync(CancellationToken cancellationToken = default) =>
        store.GetHistoryAsync(cancellationToken);

    public Task EnsureMinimumAsync(CancellationToken cancellationToken = default) =>
        ExecuteSerializedAsync(() => EnsureMinimumCoreAsync(cancellationToken), cancellationToken);

    public Task MoveAsync(long queueEntryId, int offset, CancellationToken cancellationToken = default) =>
        ExecuteSerializedAsync(async () =>
        {
            if (offset is not (-1 or 1))
            {
                throw new ArgumentOutOfRangeException(nameof(offset), "Die Queue kann nur um genau eine Position verschoben werden.");
            }

            var pending = (await store.GetQueueAsync(cancellationToken))
                .Where(entry => entry.Status == QueueEntryStatus.Pending)
                .OrderBy(entry => entry.SortOrder)
                .ThenBy(entry => entry.Id)
                .ToList();
            var currentIndex = pending.FindIndex(entry => entry.Id == queueEntryId);
            if (currentIndex < 0)
            {
                throw new InvalidOperationException("Der Queue-Eintrag wurde nicht gefunden oder läuft bereits.");
            }

            var targetIndex = currentIndex + offset;
            if (targetIndex < 0 || targetIndex >= pending.Count)
            {
                return;
            }

            (pending[currentIndex], pending[targetIndex]) = (pending[targetIndex], pending[currentIndex]);
            await NormalizePendingOrderAsync(pending, cancellationToken);
        }, cancellationToken);

    public Task RemoveAsync(long queueEntryId, CancellationToken cancellationToken = default) =>
        ExecuteSerializedAsync(async () =>
        {
            var queue = await store.GetQueueAsync(cancellationToken);
            var entry = queue.SingleOrDefault(candidate => candidate.Id == queueEntryId && candidate.Status == QueueEntryStatus.Pending)
                ?? throw new InvalidOperationException("Nur noch nicht gestartete Queue-Einträge können entfernt werden.");
            entry.Status = QueueEntryStatus.Skipped;
            entry.CompletedUtc = timeProvider.GetUtcNow();
            await store.UpdateQueueEntryAsync(entry, cancellationToken);
            await NormalizePendingOrderAsync(
                queue.Where(candidate => candidate.Status == QueueEntryStatus.Pending)
                    .OrderBy(candidate => candidate.SortOrder)
                    .ThenBy(candidate => candidate.Id)
                    .ToList(),
                cancellationToken);
        }, cancellationToken);

    public Task RegenerateAsync(CancellationToken cancellationToken = default) =>
        ExecuteSerializedAsync(async () =>
        {
            var queue = await store.GetQueueAsync(cancellationToken);
            foreach (var entry in queue.Where(entry =>
                         entry.Status == QueueEntryStatus.Pending && entry.Origin == QueueEntryOrigin.Automatic))
            {
                entry.Status = QueueEntryStatus.Skipped;
                entry.CompletedUtc = timeProvider.GetUtcNow();
                await store.UpdateQueueEntryAsync(entry, cancellationToken);
            }

            var remaining = queue.Where(entry => entry.Status == QueueEntryStatus.Pending)
                .OrderBy(entry => entry.SortOrder)
                .ThenBy(entry => entry.Id)
                .ToList();
            await NormalizePendingOrderAsync(remaining, cancellationToken);
            await EnsureMinimumCoreAsync(cancellationToken);
        }, cancellationToken);

    public Task ClearHistoryAsync(CancellationToken cancellationToken = default) =>
        ExecuteSerializedAsync(() => store.ClearHistoryAsync(cancellationToken), cancellationToken);

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

    public Task<QueueEntry> AddYouTubeNextAsync(
        string url,
        TimeSpan? start = null,
        TimeSpan? duration = null,
        TimeSpan? maximumDuration = null,
        CancellationToken cancellationToken = default) =>
        ExecuteSerializedAsync(async () =>
        {
            var selection = await ResolveYouTubeSegmentAsync(url, start, duration, maximumDuration, cancellationToken);
            var queue = await store.GetQueueAsync(cancellationToken);
            foreach (var entry in queue.Where(entry => entry.Status == QueueEntryStatus.Pending))
            {
                entry.SortOrder++;
                await store.UpdateQueueEntryAsync(entry, cancellationToken);
            }

            return await store.AddQueueEntryAsync(CreateYouTubeQueueEntry(selection, QueueEntryOrigin.ManualNext, QueueEntryStatus.Pending, 1), cancellationToken);
        }, cancellationToken);

    public Task<QueueEntry> StartYouTubeNowAsync(
        string url,
        TimeSpan? currentPosition,
        TimeSpan? start = null,
        TimeSpan? duration = null,
        TimeSpan? maximumDuration = null,
        CancellationToken cancellationToken = default) =>
        ExecuteSerializedAsync(async () =>
        {
            var selection = await ResolveYouTubeSegmentAsync(url, start, duration, maximumDuration, cancellationToken);
            var queue = await store.GetQueueAsync(cancellationToken);
            var current = queue.SingleOrDefault(entry => entry.Status == QueueEntryStatus.Playing);
            if (current is not null)
            {
                await FinishEntryAsync(current, currentPosition, QueueEntryStatus.Interrupted, cancellationToken);
            }

            return await store.AddQueueEntryAsync(CreateYouTubeQueueEntry(selection, QueueEntryOrigin.ManualNow, QueueEntryStatus.Playing, 0), cancellationToken);
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

    private async Task EnsureMinimumCoreAsync(CancellationToken cancellationToken)
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
    }

    private async Task NormalizePendingOrderAsync(IReadOnlyList<QueueEntry> pending, CancellationToken cancellationToken)
    {
        for (var index = 0; index < pending.Count; index++)
        {
            var sortOrder = index + 1;
            if (pending[index].SortOrder == sortOrder)
            {
                continue;
            }

            pending[index].SortOrder = sortOrder;
            await store.UpdateQueueEntryAsync(pending[index], cancellationToken);
        }
    }

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

        if ((entry.MediaId.HasValue || !string.IsNullOrWhiteSpace(entry.ExternalSourceKey)) && actualEnd > entry.StartPosition)
        {
            await store.AddHistoryAsync(new PlaybackHistory
            {
                MediaId = entry.MediaId,
                ExternalSourceKey = entry.ExternalSourceKey,
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

    private QueueEntry CreateYouTubeQueueEntry(
        YouTubeQueueSelection selection,
        QueueEntryOrigin origin,
        QueueEntryStatus status,
        int sortOrder)
    {
        var now = timeProvider.GetUtcNow();
        return new QueueEntry
        {
            SourceType = MediaSourceType.YouTube,
            ExternalSourceKey = selection.Reference.SourceKey,
            StartPosition = selection.Start,
            EndPosition = selection.End,
            Origin = origin,
            SortOrder = sortOrder,
            Status = status,
            CreatedUtc = now,
            StartedUtc = status == QueueEntryStatus.Playing ? now : null
        };
    }

    private async Task<YouTubeQueueSelection> ResolveYouTubeSegmentAsync(
        string url,
        TimeSpan? start,
        TimeSpan? duration,
        TimeSpan? maximumDuration,
        CancellationToken cancellationToken)
    {
        var reference = YouTubeUrlParser.Parse(url);
        var playbackDuration = duration ?? maximumDuration;
        if (playbackDuration is null || playbackDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Für YouTube ist eine positive Dauer oder maximale Wiedergabezeit erforderlich.");
        }

        if ((start.HasValue && start.Value < TimeSpan.Zero) || (start.HasValue && !duration.HasValue))
        {
            throw new ArgumentOutOfRangeException(nameof(start), "Ein eigener YouTube-Start benötigt eine positive Dauer.");
        }

        var resolvedStart = start ?? await FindNextYouTubeStartAsync(reference.SourceKey, cancellationToken);
        return new YouTubeQueueSelection(reference, resolvedStart, resolvedStart + playbackDuration.Value);
    }

    private async Task<TimeSpan> FindNextYouTubeStartAsync(string sourceKey, CancellationToken cancellationToken)
    {
        var history = await store.GetHistoryAsync(cancellationToken);
        var usedEnd = history
            .Where(entry => entry.SourceType == MediaSourceType.YouTube &&
                            entry.ExternalSourceKey == sourceKey &&
                            entry.ActualEnd.HasValue)
            .Select(entry => entry.ActualEnd!.Value)
            .DefaultIfEmpty(TimeSpan.Zero)
            .Max();
        var reservedEnd = (await store.GetQueueAsync(cancellationToken))
            .Where(entry => entry.SourceType == MediaSourceType.YouTube && entry.ExternalSourceKey == sourceKey)
            .Select(entry => entry.EndPosition)
            .DefaultIfEmpty(TimeSpan.Zero)
            .Max();
        return usedEnd > reservedEnd ? usedEnd : reservedEnd;
    }

    private static PlaybackHistory ToReservedHistory(QueueEntry entry) => new()
    {
        MediaId = entry.MediaId,
        ExternalSourceKey = entry.ExternalSourceKey,
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
