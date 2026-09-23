using BeamerPresenter.Domain;

namespace BeamerPresenter.Application;

public sealed class PlaybackOrchestrator(
    PlaybackController playback,
    IBrowserController browser,
    IPresenterGateway presenter,
    IPresenterSettingsService settingsService,
    IPowerManagementService powerManagement,
    PlaybackQueueService queue) : IPlaybackCommandService, INewsCommandService, INewsDisplayState, IPresenterRecoveryService, IPresenterControlService
{
    private readonly SemaphoreSlim commandGate = new(1, 1);
    private CancellationTokenSource? newsTimeout;
    private CancellationTokenSource? tickerTimeout;
    private NewsItem? currentNews;
    private NewsItem? suspendedNews;
    private NewsItem? currentTicker;

    public Task<NewsDisplaySnapshot> GetNewsDisplayAsync(CancellationToken cancellationToken = default) =>
        ExecuteSerializedAsync(() => Task.FromResult(new NewsDisplaySnapshot(currentNews, currentTicker)), cancellationToken);

    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        await ExecuteSerializedAsync(async () =>
        {
            var settings = await settingsService.GetAsync(cancellationToken);
            try
            {
                await queue.EnsureMinimumAsync(cancellationToken);
                var activeQueue = await queue.GetQueueAsync(cancellationToken);
                if (!activeQueue.Any(entry => entry.Status == QueueEntryStatus.Playing))
                {
                    await queue.CompleteCurrentAsync(actualPosition: null, cancellationToken: cancellationToken);
                }

                await browser.StartAsync(cancellationToken);
                await powerManagement.ApplyAsync(settings.PreventDisplaySleep, settings.PreventSystemSleep, cancellationToken);
                playback.Activate();
            }
            catch
            {
                await powerManagement.ReleaseAsync(CancellationToken.None);
                await browser.StopAsync(CancellationToken.None);
                throw;
            }
        }, cancellationToken);
    }

    public Task ReloadCurrentAsync(bool autoPlay, CancellationToken cancellationToken = default) =>
        ExecuteSerializedAsync(async () =>
        {
            var current = (await queue.GetQueueAsync(cancellationToken))
                .SingleOrDefault(entry => entry.Status == QueueEntryStatus.Playing);
            if (current is not null)
            {
                await LoadEntryAsync(current, autoPlay, cancellationToken);
            }
        }, cancellationToken);

    public async Task FailCurrentAndAdvanceAsync(
        TimeSpan? actualPosition,
        CancellationToken cancellationToken = default)
    {
        await AdvanceAsync(actualPosition, successful: false, cancellationToken);
    }

    public Task PauseAsync(CancellationToken cancellationToken = default) =>
        ExecuteSerializedAsync(async () =>
        {
            await presenter.PauseAsync(cancellationToken);
            await browser.ShowAsync(cancellationToken);
            playback.Pause();
        }, cancellationToken);

    public Task HideAsync(CancellationToken cancellationToken = default) =>
        ExecuteSerializedAsync(async () =>
        {
            await presenter.PauseAsync(cancellationToken);
            await browser.HideAsync(cancellationToken);
            await powerManagement.ReleaseAsync(cancellationToken);
            playback.Hide();
        }, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        ExecuteSerializedAsync(async () =>
        {
            await presenter.StopAsync(cancellationToken);
            await browser.StopAsync(cancellationToken);
            await powerManagement.ReleaseAsync(cancellationToken);
            playback.Stop();
        }, cancellationToken);

    public Task<QueueEntry> PlayNextAsync(
        int mediaId,
        TimeSpan? start = null,
        TimeSpan? duration = null,
        CancellationToken cancellationToken = default) =>
        ExecuteSerializedAsync(() => queue.AddNextAsync(mediaId, start, duration, cancellationToken), cancellationToken);

    public Task<QueueEntry> PlayNowAsync(
        int mediaId,
        TimeSpan? currentPosition,
        TimeSpan? start = null,
        TimeSpan? duration = null,
        CancellationToken cancellationToken = default) =>
        ExecuteSerializedAsync(async () =>
        {
            var entry = await queue.StartNowAsync(mediaId, currentPosition, start, duration, cancellationToken);
            await presenter.StopAsync(cancellationToken);
            try
            {
                await presenter.LoadLocalVideoAsync(entry.MediaId!.Value, entry.StartPosition, entry.EndPosition, autoPlay: true, cancellationToken);
                playback.Activate();
                return entry;
            }
            catch
            {
                await queue.MarkFailedAsync(entry, CancellationToken.None);
                throw;
            }
        }, cancellationToken);

    public Task<QueueEntry> PlayYouTubeNextAsync(
        string url,
        TimeSpan? start = null,
        TimeSpan? duration = null,
        TimeSpan? maximumDuration = null,
        CancellationToken cancellationToken = default) =>
        ExecuteSerializedAsync(
            () => queue.AddYouTubeNextAsync(url, start, duration, maximumDuration, cancellationToken),
            cancellationToken);

    public Task<QueueEntry> PlayYouTubeNowAsync(
        string url,
        TimeSpan? currentPosition,
        TimeSpan? start = null,
        TimeSpan? duration = null,
        TimeSpan? maximumDuration = null,
        CancellationToken cancellationToken = default) =>
        ExecuteSerializedAsync(async () =>
        {
            var entry = await queue.StartYouTubeNowAsync(url, currentPosition, start, duration, maximumDuration, cancellationToken);
            await presenter.StopAsync(cancellationToken);
            try
            {
                await LoadEntryAsync(entry, autoPlay: true, cancellationToken);
                playback.Activate();
                return entry;
            }
            catch
            {
                await queue.MarkFailedAsync(entry, CancellationToken.None);
                throw;
            }
        }, cancellationToken);

    public Task PrioritizeQueuedAsync(long queueEntryId, CancellationToken cancellationToken = default) =>
        ExecuteSerializedAsync(() => queue.PrioritizeAsync(queueEntryId, cancellationToken), cancellationToken);

    public Task<QueueEntry> PlayQueuedNowAsync(
        long queueEntryId,
        TimeSpan? currentPosition,
        CancellationToken cancellationToken = default) =>
        ExecuteSerializedAsync(async () =>
        {
            var entry = await queue.StartQueuedNowAsync(queueEntryId, currentPosition, cancellationToken);
            await presenter.StopAsync(cancellationToken);
            try
            {
                await LoadEntryAsync(entry, autoPlay: true, cancellationToken);
                playback.Activate();
                return entry;
            }
            catch
            {
                await queue.MarkFailedAsync(entry, CancellationToken.None);
                throw;
            }
        }, cancellationToken);

    public Task<QueueEntry?> AdvanceAsync(
        TimeSpan? actualPosition,
        bool successful = true,
        CancellationToken cancellationToken = default) =>
        ExecuteSerializedAsync(async () =>
        {
            var next = await queue.CompleteCurrentAsync(actualPosition, successful, cancellationToken);
            if (next is not null)
            {
                await LoadEntryAsync(next, autoPlay: true, cancellationToken);
            }

            await queue.EnsureMinimumAsync(cancellationToken);
            return next;
        }, cancellationToken);

    public Task ShowNewsAsync(NewsItem item, CancellationToken cancellationToken = default) =>
        ExecuteSerializedAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(item);
            await EnsureDisplayActiveAsync(cancellationToken);

            if (item.Mode == NewsMode.Ticker)
            {
                if (currentTicker is null || item.Priority >= currentTicker.Priority)
                {
                    CancelTickerTimeout();
                    currentTicker = item;
                    await presenter.ShowTickerAsync(item, cancellationToken);
                    ScheduleNewsTimeout(item);
                }

                return;
            }

            if (item.Mode == NewsMode.Fullscreen)
            {
                if (currentNews?.Mode == NewsMode.SplitScreen)
                {
                    suspendedNews = currentNews;
                }

                CancelNewsTimeout();
                if (currentNews?.Mode != NewsMode.Fullscreen)
                {
                    await presenter.PauseAsync(cancellationToken);
                }

                currentNews = item;
                await presenter.ShowNewsAsync(item, cancellationToken);
                ScheduleNewsTimeout(item);
                return;
            }

            if (currentNews?.Mode == NewsMode.Fullscreen)
            {
                if (suspendedNews is null || item.Priority >= suspendedNews.Priority)
                {
                    suspendedNews = item;
                }

                return;
            }

            if (currentNews is null || item.Priority >= currentNews.Priority)
            {
                CancelNewsTimeout();
                currentNews = item;
                await presenter.ShowNewsAsync(item, cancellationToken);
                ScheduleNewsTimeout(item);
            }
        }, cancellationToken);

    private async Task EnsureDisplayActiveAsync(CancellationToken cancellationToken)
    {
        if (playback.State != PresenterState.Stopped)
        {
            return;
        }

        var settings = await settingsService.GetAsync(cancellationToken);
        try
        {
            await browser.StartAsync(cancellationToken);
            await powerManagement.ApplyAsync(settings.PreventDisplaySleep, settings.PreventSystemSleep, cancellationToken);
            playback.Activate();
        }
        catch
        {
            await powerManagement.ReleaseAsync(CancellationToken.None);
            await browser.StopAsync(CancellationToken.None);
            throw;
        }
    }

    public Task StopNewsAsync(long? newsId = null, CancellationToken cancellationToken = default) =>
        ExecuteSerializedAsync(async () =>
        {
            if (newsId.HasValue && currentTicker?.Id == newsId.Value)
            {
                await StopTickerCoreAsync(cancellationToken);
                return;
            }

            if (newsId.HasValue && suspendedNews?.Id == newsId.Value)
            {
                suspendedNews = null;
                return;
            }

            if (currentNews is null || (newsId.HasValue && currentNews.Id != newsId.Value))
            {
                return;
            }

            var stoppedMode = currentNews.Mode;
            CancelNewsTimeout();
            currentNews = null;
            await presenter.HideNewsAsync(cancellationToken);
            if (stoppedMode == NewsMode.Fullscreen)
            {
                await presenter.PlayAsync(cancellationToken);
                if (suspendedNews is not null)
                {
                    currentNews = suspendedNews;
                    suspendedNews = null;
                    await presenter.ShowNewsAsync(currentNews, cancellationToken);
                    ScheduleNewsTimeout(currentNews);
                }
            }
        }, cancellationToken);

    public Task StopTickerAsync(CancellationToken cancellationToken = default) =>
        ExecuteSerializedAsync(() => StopTickerCoreAsync(cancellationToken), cancellationToken);

    private async Task StopTickerCoreAsync(CancellationToken cancellationToken)
    {
        if (currentTicker is null)
        {
            return;
        }

        CancelTickerTimeout();
        currentTicker = null;
        await presenter.HideTickerAsync(cancellationToken);
    }

    private Task LoadEntryAsync(QueueEntry entry, bool autoPlay, CancellationToken cancellationToken) => entry.SourceType switch
    {
        MediaSourceType.Local when entry.MediaId is int mediaId =>
            presenter.LoadLocalVideoAsync(mediaId, entry.StartPosition, entry.EndPosition, autoPlay, cancellationToken),
        MediaSourceType.YouTube when TryGetYouTubeId(entry.ExternalSourceKey, out var videoId) =>
            presenter.LoadYouTubeVideoAsync(videoId, entry.StartPosition, entry.EndPosition, autoPlay, cancellationToken),
        _ => throw new InvalidOperationException("Der Queue-Eintrag besitzt keine gültige Wiedergabequelle.")
    };

    private static bool TryGetYouTubeId(string? sourceKey, out string videoId)
    {
        const string prefix = "youtube:";
        if (sourceKey?.StartsWith(prefix, StringComparison.Ordinal) == true && sourceKey.Length == prefix.Length + 11)
        {
            videoId = sourceKey[prefix.Length..];
            return true;
        }

        videoId = string.Empty;
        return false;
    }

    private void ScheduleNewsTimeout(NewsItem item)
    {
        if (item.Permanent || item.Duration is null || item.Duration <= TimeSpan.Zero)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        if (item.Mode == NewsMode.Ticker)
        {
            tickerTimeout = cancellation;
        }
        else
        {
            newsTimeout = cancellation;
        }
        _ = StopNewsAfterDelayAsync(item.Id, item.Duration.Value, cancellation.Token);
    }

    private async Task StopNewsAfterDelayAsync(long newsId, TimeSpan duration, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(duration, cancellationToken);
            await StopNewsAsync(newsId, CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void CancelNewsTimeout()
    {
        newsTimeout?.Cancel();
        newsTimeout?.Dispose();
        newsTimeout = null;
    }

    private void CancelTickerTimeout()
    {
        tickerTimeout?.Cancel();
        tickerTimeout?.Dispose();
        tickerTimeout = null;
    }

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
