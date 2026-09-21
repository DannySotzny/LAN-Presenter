using BeamerPresenter.Application;
using BeamerPresenter.Domain;
using BeamerPresenter.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeamerPresenter.Infrastructure.Tests;

public sealed class PlaybackStoreTests
{
    [Fact]
    public async Task Queue_and_history_survive_a_service_provider_restart()
    {
        var dataDirectory = CreateTestDirectory();
        try
        {
            PresenterDatabase.GetConfiguredWebPort(dataDirectory);
            var createdUtc = new DateTimeOffset(2026, 9, 20, 18, 0, 0, TimeSpan.Zero);
            long queueId;
            long historyId;

            await using (var provider = CreateProvider(dataDirectory))
            {
                var store = provider.GetRequiredService<IPlaybackStore>();
                var queueEntry = await store.AddQueueEntryAsync(new QueueEntry
                {
                    SourceType = MediaSourceType.Local,
                    MediaId = 42,
                    StartPosition = TimeSpan.FromMinutes(3),
                    EndPosition = TimeSpan.FromMinutes(11),
                    Origin = QueueEntryOrigin.ManualNext,
                    SortOrder = 1,
                    Status = QueueEntryStatus.Pending,
                    CreatedUtc = createdUtc
                });
                var history = await store.AddHistoryAsync(new PlaybackHistory
                {
                    MediaId = 42,
                    SourceType = MediaSourceType.Local,
                    PlannedStart = TimeSpan.FromMinutes(3),
                    PlannedEnd = TimeSpan.FromMinutes(11),
                    ActualStart = TimeSpan.FromMinutes(3),
                    ActualEnd = TimeSpan.FromMinutes(7),
                    StartedUtc = createdUtc,
                    FinishedUtc = createdUtc.AddMinutes(4),
                    Interrupted = true,
                    PlaybackReason = PlaybackReason.ManualNext
                });
                queueId = queueEntry.Id;
                historyId = history.Id;
            }

            await using (var provider = CreateProvider(dataDirectory))
            {
                var store = provider.GetRequiredService<IPlaybackStore>();
                var queue = await store.GetQueueAsync();
                var history = await store.GetHistoryAsync();

                var persistedQueue = Assert.Single(queue);
                Assert.Equal(queueId, persistedQueue.Id);
                Assert.Equal(QueueEntryOrigin.ManualNext, persistedQueue.Origin);
                Assert.Equal(TimeSpan.FromMinutes(3), persistedQueue.StartPosition);
                Assert.Equal(TimeSpan.FromMinutes(11), persistedQueue.EndPosition);
                var persistedHistory = Assert.Single(history);
                Assert.Equal(historyId, persistedHistory.Id);
                Assert.Equal(TimeSpan.FromMinutes(7), persistedHistory.ActualEnd);
                Assert.True(persistedHistory.Interrupted);
                Assert.False(persistedHistory.Completed);

                await store.AddHistoryAsync(new PlaybackHistory
                {
                    SourceType = MediaSourceType.YouTube,
                    ExternalSourceKey = "youtube:dQw4w9WgXcQ",
                    PlannedStart = TimeSpan.Zero,
                    PlannedEnd = TimeSpan.FromMinutes(8),
                    ActualStart = TimeSpan.Zero,
                    ActualEnd = TimeSpan.FromMinutes(5),
                    StartedUtc = createdUtc,
                    PlaybackReason = PlaybackReason.ManualNext
                });
            }

            await using (var provider = CreateProvider(dataDirectory))
            {
                var history = await provider.GetRequiredService<IPlaybackStore>().GetHistoryAsync();
                Assert.Contains(history, entry => entry.MediaId is null && entry.ExternalSourceKey == "youtube:dQw4w9WgXcQ");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var testRoot = Directory.GetParent(dataDirectory)!.FullName;
            var allowedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BeamerPresenter.Tests"));
            var resolvedTestRoot = Path.GetFullPath(testRoot);
            if (resolvedTestRoot.StartsWith(allowedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(resolvedTestRoot))
            {
                Directory.Delete(resolvedTestRoot, recursive: true);
            }
        }
    }

    private static ServiceProvider CreateProvider(string dataDirectory)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPresenterInfrastructure(dataDirectory);
        return services.BuildServiceProvider();
    }

    private static string CreateTestDirectory()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "BeamerPresenter.Tests", Guid.NewGuid().ToString("N"));
        var dataDirectory = Path.Combine(testRoot, "Data");
        Directory.CreateDirectory(dataDirectory);
        return dataDirectory;
    }
}
