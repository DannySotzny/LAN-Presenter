using BeamerPresenter.Domain;

namespace BeamerPresenter.Application;

public sealed class PlaybackOrchestrator(
    PlaybackController playback,
    IBrowserController browser,
    IPresenterGateway presenter,
    IPresenterSettingsService settingsService,
    IPowerManagementService powerManagement,
    PlaybackQueueService queue) : IPlaybackCommandService
{
    private readonly SemaphoreSlim commandGate = new(1, 1);

    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        await ExecuteSerializedAsync(async () =>
        {
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
        }, cancellationToken);
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
                await LoadEntryAsync(entry, cancellationToken);
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
                await LoadEntryAsync(next, cancellationToken);
            }

            await queue.EnsureMinimumAsync(cancellationToken);
            return next;
        }, cancellationToken);

    private Task LoadEntryAsync(QueueEntry entry, CancellationToken cancellationToken) => entry.SourceType switch
    {
        MediaSourceType.Local when entry.MediaId is int mediaId =>
            presenter.LoadLocalVideoAsync(mediaId, entry.StartPosition, entry.EndPosition, autoPlay: true, cancellationToken),
        MediaSourceType.YouTube when TryGetYouTubeId(entry.ExternalSourceKey, out var videoId) =>
            presenter.LoadYouTubeVideoAsync(videoId, entry.StartPosition, entry.EndPosition, autoPlay: true, cancellationToken),
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
