using BeamerPresenter.Application;
using BeamerPresenter.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeamerPresenter.Infrastructure.Tests;

public sealed class MediaScannerTests
{
    [Fact]
    public async Task Reconciliation_detects_new_changed_and_missing_media_files()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "BeamerPresenter.Tests", Guid.NewGuid().ToString("N"));
        var dataDirectory = Path.Combine(testRoot, "Data");
        var mediaDirectory = Path.Combine(testRoot, "Media");
        var nestedDirectory = Path.Combine(mediaDirectory, "Nested");
        Directory.CreateDirectory(nestedDirectory);
        var topLevelVideo = Path.Combine(mediaDirectory, "intro.mp4");
        var nestedVideo = Path.Combine(nestedDirectory, "gameplay.mkv");
        await File.WriteAllBytesAsync(topLevelVideo, [1, 2, 3]);
        await File.WriteAllBytesAsync(nestedVideo, [4, 5, 6]);
        await File.WriteAllTextAsync(Path.Combine(mediaDirectory, "ignore.txt"), "not a video");

        try
        {
            PresenterDatabase.GetConfiguredWebPort(dataDirectory);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddPresenterInfrastructure(dataDirectory);
            await using var provider = services.BuildServiceProvider();
            await provider.GetRequiredService<IMediaFolderService>().AddAsync(mediaDirectory, includeSubdirectories: true);
            var scanner = provider.GetRequiredService<IMediaScanner>();

            var firstScan = await scanner.ScanAllAsync();

            Assert.Equal(new MediaScanResult(2, 0, 0, 0), firstScan);
            Assert.Equal(2, (await provider.GetRequiredService<IMediaLibraryService>().GetAllAsync()).Count);

            await File.AppendAllTextAsync(topLevelVideo, "changed");
            File.Delete(nestedVideo);
            var secondScan = await scanner.ScanAllAsync();

            Assert.Equal(new MediaScanResult(0, 1, 1, 0), secondScan);
            var videos = await provider.GetRequiredService<IMediaLibraryService>().GetAllAsync();
            Assert.True(videos.Single(video => video.FileName == "intro.mp4").IsAvailable);
            Assert.False(videos.Single(video => video.FileName == "gameplay.mkv").IsAvailable);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var allowedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BeamerPresenter.Tests"));
            var resolvedRoot = Path.GetFullPath(testRoot);
            if (resolvedRoot.StartsWith(allowedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolvedRoot))
            {
                Directory.Delete(resolvedRoot, recursive: true);
            }
        }
    }
}
