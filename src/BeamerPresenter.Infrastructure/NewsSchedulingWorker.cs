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
    private long? scheduledTickerId;
    private long? scheduledMainId;

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
            var selection = NewsScheduleSelector.Select(await newsService.GetAllAsync(cancellationToken), timeProvider.GetUtcNow());
            if (selection.Ticker?.Id != scheduledTickerId)
            {
                if (scheduledTickerId.HasValue)
                {
                    await commands.StopNewsAsync(scheduledTickerId.Value, cancellationToken);
                }

                scheduledTickerId = selection.Ticker?.Id;
                if (selection.Ticker is not null)
                {
                    await commands.ShowNewsAsync(selection.Ticker, cancellationToken);
                }
            }

            if (selection.Main?.Id != scheduledMainId)
            {
                if (scheduledMainId.HasValue)
                {
                    await commands.StopNewsAsync(scheduledMainId.Value, cancellationToken);
                }

                scheduledMainId = selection.Main?.Id;
                if (selection.Main is not null)
                {
                    await commands.ShowNewsAsync(selection.Main, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Scheduled news synchronization failed");
        }
    }
}
