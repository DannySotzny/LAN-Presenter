using BeamerPresenter.Application;
using BeamerPresenter.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeamerPresenter.Infrastructure.Tests;

public sealed class NewsSchedulingWorkerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Synchronization_shows_selected_news_once_and_stops_it_after_expiration()
    {
        var newsService = new StubNewsService
        {
            Items =
            [
                new NewsItem
                {
                    Id = 42,
                    Title = "Turnier",
                    Text = "Finale beginnt",
                    Mode = NewsMode.Ticker,
                    Permanent = true,
                    ValidFrom = Now.AddMinutes(-1),
                    ValidUntil = Now.AddMinutes(1),
                    Priority = 10
                }
            ]
        };
        var commands = new RecordingNewsCommands();
        var clock = new MutableTimeProvider(Now);
        var worker = new NewsSchedulingWorker(newsService, commands, clock, NullLogger<NewsSchedulingWorker>.Instance);

        await worker.SynchronizeAsync();
        await worker.SynchronizeAsync();
        clock.Advance(TimeSpan.FromMinutes(2));
        await worker.SynchronizeAsync();

        Assert.Equal([42L], commands.ShownIds);
        Assert.Equal([42L], commands.StoppedIds);
    }

    [Fact]
    public async Task Synchronization_logs_and_swallows_service_failures()
    {
        var commands = new RecordingNewsCommands();
        var worker = new NewsSchedulingWorker(
            new StubNewsService { Failure = new InvalidOperationException("database unavailable") },
            commands,
            new MutableTimeProvider(Now),
            NullLogger<NewsSchedulingWorker>.Instance);

        await worker.SynchronizeAsync();

        Assert.Empty(commands.ShownIds);
        Assert.Empty(commands.StoppedIds);
    }

    [Fact]
    public async Task Synchronization_tracks_ticker_and_main_news_independently()
    {
        var newsService = new StubNewsService
        {
            Items =
            [
                new NewsItem { Id = 1, Title = "Ticker", Text = "Text", Mode = NewsMode.Ticker, Permanent = true, ValidFrom = Now.AddMinutes(-1), ValidUntil = Now.AddMinutes(1) },
                new NewsItem { Id = 2, Title = "50:50", Text = "Text", Mode = NewsMode.SplitScreen, Permanent = true, ValidFrom = Now.AddMinutes(-1), ValidUntil = Now.AddMinutes(10) }
            ]
        };
        var commands = new RecordingNewsCommands();
        var clock = new MutableTimeProvider(Now);
        var worker = new NewsSchedulingWorker(newsService, commands, clock, NullLogger<NewsSchedulingWorker>.Instance);

        await worker.SynchronizeAsync();
        Assert.Equal([1L, 2L], commands.ShownIds);

        clock.Advance(TimeSpan.FromMinutes(2));
        await worker.SynchronizeAsync();
        Assert.Equal([1L], commands.StoppedIds);

        clock.Advance(TimeSpan.FromMinutes(10));
        await worker.SynchronizeAsync();
        Assert.Equal([1L, 2L], commands.StoppedIds);
    }

    private sealed class StubNewsService : INewsService
    {
        public IReadOnlyList<NewsItem> Items { get; set; } = [];
        public Exception? Failure { get; set; }

        public Task<IReadOnlyList<NewsItem>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Failure is null ? Task.FromResult(Items) : Task.FromException<IReadOnlyList<NewsItem>>(Failure);

        public Task<NewsItem> AddAsync(NewsItem item, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateAsync(NewsItem item, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(long id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RecordingNewsCommands : INewsCommandService
    {
        public List<long> ShownIds { get; } = [];
        public List<long> StoppedIds { get; } = [];

        public Task ShowNewsAsync(NewsItem item, CancellationToken cancellationToken = default)
        {
            ShownIds.Add(item.Id);
            return Task.CompletedTask;
        }

        public Task StopNewsAsync(long? newsId = null, CancellationToken cancellationToken = default)
        {
            StoppedIds.Add(newsId.GetValueOrDefault());
            return Task.CompletedTask;
        }

        public Task StopTickerAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }
}
