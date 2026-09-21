using BeamerPresenter.Application;
using BeamerPresenter.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeamerPresenter.App;

internal sealed class PresenterWatchdog(
    PlaybackController playback,
    IBrowserController browser,
    IPresenterTelemetry telemetry,
    IPresenterRecoveryService recovery,
    IPresenterSettingsService settingsService,
    TimeProvider timeProvider,
    ILogger<PresenterWatchdog> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PlaybackStallTimeout = TimeSpan.FromSeconds(15);
    private DateTimeOffset? disconnectedSince;
    private DateTimeOffset? lastProgressUtc;
    private DateTimeOffset? lastWindowVerificationUtc;
    private TimeSpan? lastPosition;
    private bool reloadAttempted;
    private bool awaitingConnectionRecovery;
    private bool wasConnected;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await CheckSafelyAsync(stoppingToken);
        }
    }

    internal async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var presenterState = playback.State;
        if (presenterState is not (PresenterState.Active or PresenterState.Paused))
        {
            ResetInactiveState();
            return;
        }

        if (!await browser.IsRunningAsync(cancellationToken))
        {
            logger.LogWarning("Presenter Chrome process is not running; restarting kiosk");
            await browser.StartAsync(cancellationToken);
            awaitingConnectionRecovery = true;
            wasConnected = false;
            disconnectedSince = now;
            return;
        }

        var snapshot = telemetry.Current;
        var connectionFresh = snapshot.IsConnected && now - snapshot.ReceivedUtc < ConnectionTimeout;
        if (!connectionFresh)
        {
            disconnectedSince ??= now;
            if (now - disconnectedSince.Value >= ConnectionTimeout)
            {
                logger.LogWarning("Presenter connection or heartbeat is stale; restarting kiosk");
                await browser.StopAsync(cancellationToken);
                await browser.StartAsync(cancellationToken);
                awaitingConnectionRecovery = true;
                wasConnected = false;
                disconnectedSince = now;
            }

            return;
        }

        disconnectedSince = null;
        if (awaitingConnectionRecovery || !wasConnected)
        {
            await browser.ShowAsync(cancellationToken);
            await recovery.ReloadCurrentAsync(autoPlay: presenterState == PresenterState.Active, cancellationToken);
            awaitingConnectionRecovery = false;
            lastWindowVerificationUtc = now;
        }

        wasConnected = true;
        var settings = await settingsService.GetAsync(cancellationToken);
        var verificationInterval = settings.AggressiveTopmost ? PollInterval : TimeSpan.FromSeconds(30);
        if (settings.AlwaysOnTop &&
            (!await browser.IsTopmostAsync(cancellationToken) ||
             !lastWindowVerificationUtc.HasValue ||
             now - lastWindowVerificationUtc.Value >= verificationInterval))
        {
            await browser.ShowAsync(cancellationToken);
            lastWindowVerificationUtc = now;
        }

        if (presenterState == PresenterState.Active)
        {
            await CheckPlaybackProgressAsync(snapshot, now, cancellationToken);
        }
        else
        {
            ResetPlaybackProgress(now);
        }
    }

    private async Task CheckPlaybackProgressAsync(
        PresenterTelemetrySnapshot snapshot,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!snapshot.Status.Equals("Playing", StringComparison.OrdinalIgnoreCase) || !snapshot.Position.HasValue)
        {
            lastPosition = snapshot.Position;
            lastProgressUtc = now;
            return;
        }

        if (!lastPosition.HasValue || Math.Abs((snapshot.Position.Value - lastPosition.Value).TotalSeconds) >= 0.25)
        {
            lastPosition = snapshot.Position;
            lastProgressUtc = now;
            reloadAttempted = false;
            return;
        }

        lastProgressUtc ??= now;
        if (now - lastProgressUtc.Value < PlaybackStallTimeout)
        {
            return;
        }

        if (!reloadAttempted)
        {
            logger.LogWarning("Presenter playback position is stalled; reloading current queue entry");
            await recovery.ReloadCurrentAsync(autoPlay: true, cancellationToken);
            reloadAttempted = true;
            lastProgressUtc = now;
            return;
        }

        logger.LogError("Presenter playback did not recover; failing current queue entry");
        await recovery.FailCurrentAndAdvanceAsync(snapshot.Position, cancellationToken);
        reloadAttempted = false;
        lastPosition = null;
        lastProgressUtc = now;
    }

    private async Task CheckSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await CheckAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Presenter watchdog check failed");
        }
    }

    private void ResetInactiveState()
    {
        disconnectedSince = null;
        lastProgressUtc = null;
        lastWindowVerificationUtc = null;
        lastPosition = null;
        reloadAttempted = false;
        awaitingConnectionRecovery = false;
        wasConnected = false;
    }

    private void ResetPlaybackProgress(DateTimeOffset now)
    {
        lastPosition = null;
        lastProgressUtc = now;
        reloadAttempted = false;
    }
}
