using BeamerPresenter.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeamerPresenter.Infrastructure;

internal sealed class QueuePlanningWorker(
    PlaybackQueueService queue,
    ILogger<QueuePlanningWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PlanningInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await PlanSafelyAsync(stoppingToken);
        using var timer = new PeriodicTimer(PlanningInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await PlanSafelyAsync(stoppingToken);
        }
    }

    private async Task PlanSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await queue.EnsureMinimumAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Playback queue planning failed");
        }
    }
}
