using BeamerPresenter.Application;
using BeamerPresenter.Domain;
using BeamerPresenter.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeamerPresenter.Infrastructure.Tests;

public sealed class NewsServiceTests
{
    [Fact]
    public async Task News_items_are_validated_persisted_ordered_and_deleted()
    {
        var dataDirectory = CreateTestDirectory();
        try
        {
            PresenterDatabase.GetConfiguredWebPort(dataDirectory);
            long tickerId;
            await using (var provider = CreateProvider(dataDirectory))
            {
                var service = provider.GetRequiredService<INewsService>();
                var ticker = await service.AddAsync(new NewsItem
                {
                    Title = "CS2 5on5",
                    Text = "Start um 20 Uhr",
                    Mode = NewsMode.Ticker,
                    Permanent = true,
                    Priority = 2
                });
                await service.AddAsync(new NewsItem
                {
                    Title = "Turnierstart",
                    Text = "Treffpunkt Turnierleitung",
                    Mode = NewsMode.Fullscreen,
                    Duration = TimeSpan.FromMinutes(5),
                    Priority = 10
                });
                tickerId = ticker.Id;
            }

            await using (var provider = CreateProvider(dataDirectory))
            {
                var service = provider.GetRequiredService<INewsService>();
                var items = await service.GetAllAsync();
                Assert.Equal(2, items.Count);
                Assert.Equal(NewsMode.Fullscreen, items[0].Mode);
                Assert.Equal(NewsMode.Ticker, items[1].Mode);
                Assert.True(items[1].Permanent);

                await service.DeleteAsync(tickerId);
                Assert.Single(await service.GetAllAsync());
            }
        }
        finally
        {
            DeleteTestDirectory(dataDirectory);
        }
    }

    [Fact]
    public async Task Timed_news_requires_a_positive_duration_and_valid_window()
    {
        var dataDirectory = CreateTestDirectory();
        try
        {
            PresenterDatabase.GetConfiguredWebPort(dataDirectory);
            await using var provider = CreateProvider(dataDirectory);
            var service = provider.GetRequiredService<INewsService>();

            await Assert.ThrowsAsync<ArgumentException>(() => service.AddAsync(new NewsItem
            {
                Title = "Titel",
                Text = "Text",
                Mode = NewsMode.SplitScreen
            }));
            await Assert.ThrowsAsync<ArgumentException>(() => service.AddAsync(new NewsItem
            {
                Title = "Titel",
                Text = "Text",
                Mode = NewsMode.Fullscreen,
                Duration = TimeSpan.FromMinutes(1),
                ValidFrom = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero),
                ValidUntil = new DateTimeOffset(2026, 9, 21, 11, 0, 0, TimeSpan.Zero)
            }));
        }
        finally
        {
            DeleteTestDirectory(dataDirectory);
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

    private static void DeleteTestDirectory(string dataDirectory)
    {
        SqliteConnection.ClearAllPools();
        var allowedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BeamerPresenter.Tests"));
        var resolvedDirectory = Path.GetFullPath(dataDirectory);
        var testRoot = Directory.GetParent(resolvedDirectory)?.FullName;
        if (testRoot is not null &&
            testRoot.StartsWith(allowedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(testRoot))
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }
}
