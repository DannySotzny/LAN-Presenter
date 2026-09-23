using BeamerPresenter.Application;
using BeamerPresenter.Domain;
using BeamerPresenter.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BeamerPresenter.Infrastructure.Tests;

public sealed class YouTubeMediaStoreTests
{
    [Fact]
    public async Task Redownload_assigns_unique_source_key_to_new_scanned_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "PresenterYouTubeStore", Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "Data");
        var media = Path.Combine(root, "Media");
        Directory.CreateDirectory(media);
        var newPath = Path.Combine(media, "YouTube-M7lc1UVf-VE.mp4");
        await File.WriteAllBytesAsync(newPath, [1, 2, 3]);
        try
        {
            PresenterDatabase.GetConfiguredWebPort(data);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddPresenterInfrastructure(data);
            await using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IDbContextFactory<PresenterDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.Videos.AddRange(
                    new VideoAsset
                    {
                        FileName = "missing.mp4",
                        FullPath = Path.Combine(media, "missing.mp4"),
                        YouTubeSourceKey = "youtube:M7lc1UVf-VE"
                    },
                    new VideoAsset { FileName = Path.GetFileName(newPath), FullPath = newPath });
                await db.SaveChangesAsync();
            }

            var store = provider.GetRequiredService<IYouTubeMediaStore>();
            var asset = await store.RegisterDownloadedAsync("youtube:M7lc1UVf-VE", newPath);

            Assert.Equal(newPath, asset.FullPath);
            Assert.Equal("youtube:M7lc1UVf-VE", asset.YouTubeSourceKey);
            await using var verification = await factory.CreateDbContextAsync();
            Assert.Equal(2, await verification.Videos.CountAsync());
            Assert.Null((await verification.Videos.SingleAsync(video => video.FileName == "missing.mp4")).YouTubeSourceKey);
            Assert.Equal(asset.Id, (await verification.Videos.SingleAsync(video => video.YouTubeSourceKey == "youtube:M7lc1UVf-VE")).Id);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
