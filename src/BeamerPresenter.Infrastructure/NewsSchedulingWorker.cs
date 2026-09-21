using BeamerPresenter.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeamerPresenter.Infrastructure;

public sealed class NewsSchedulingWorker(
    INewsService newsService,
    INewsCommandService commands,
    TimeProvider timeProvider,
    ILogger<NewsSchedulingWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private long? scheduledNewsId;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SynchronizeAsync(stoppingToken);
        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await SynchronizeAsync(stoppingToken);
        }
    }

    internal async Task SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var selected = NewsScheduleSelector.Select(await newsService.GetAllAsync(cancellationToken), timeProvider.GetUtcNow());
            if (selected?.Id == scheduledNewsId)
            {
                return;
            }

            if (scheduledNewsId.HasValue)
            {
                await commands.StopNewsAsync(scheduledNewsId.Value, cancellationToken);
            }

            scheduledNewsId = selected?.Id;
            if (selected is not null)
            {
                await commands.ShowNewsAsync(selected, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Scheduled news synchronization failed");
        }
    }
}
