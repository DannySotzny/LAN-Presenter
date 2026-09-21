using BeamerPresenter.Application;
using BeamerPresenter.Domain;
using BeamerPresenter.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BeamerPresenter.Infrastructure.Tests;

public sealed class MediaScannerTests
{
    [Fact]
    public async Task Media_can_be_disabled_and_requeued_for_analysis()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "BeamerPresenter.Tests", Guid.NewGuid().ToString("N"));
        var dataDirectory = Path.Combine(testRoot, "Data");
        var mediaDirectory = Path.Combine(testRoot, "Media");
        Directory.CreateDirectory(mediaDirectory);
        var videoPath = Path.Combine(mediaDirectory, "reanalyze.mp4");
        await File.WriteAllBytesAsync(videoPath, [1, 2, 3]);

        try
        {
            PresenterDatabase.GetConfiguredWebPort(dataDirectory);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddPresenterInfrastructure(dataDirectory);
            var probeQueue = new RecordingMediaProbeQueue();
            services.AddSingleton<IMediaProbeQueue>(probeQueue);
            await using var provider = services.BuildServiceProvider();
            await provider.GetRequiredService<IMediaFolderService>().AddAsync(mediaDirectory, includeSubdirectories: false);
            await provider.GetRequiredService<IMediaScanner>().ScanAllAsync();
            var mediaLibrary = provider.GetRequiredService<IMediaLibraryService>();
            var video = Assert.Single(await mediaLibrary.GetAllAsync());

            await mediaLibrary.SetEnabledAsync(video.Id, enabled: false);
            Assert.False((await mediaLibrary.GetByIdAsync(video.Id))!.Enabled);

            var factory = provider.GetRequiredService<IDbContextFactory<PresenterDbContext>>();
            await using (var context = await factory.CreateDbContextAsync())
            {
                var tracked = await context.Videos.SingleAsync(asset => asset.Id == video.Id);
                tracked.ProbeStatus = MediaProbeStatus.Invalid;
                tracked.PlaybackStatus = MediaPlaybackStatus.Supported;
                tracked.ProbeError = "old failure";
                await context.SaveChangesAsync();
            }

            await mediaLibrary.MarkPlaybackFailedAsync(video.Id);
            Assert.Equal(MediaPlaybackStatus.Failed, (await mediaLibrary.GetByIdAsync(video.Id))!.PlaybackStatus);

            probeQueue.Paths.Clear();
            await mediaLibrary.ReanalyzeAsync(video.Id);

            var reset = await mediaLibrary.GetByIdAsync(video.Id);
            Assert.Equal(MediaProbeStatus.Unknown, reset!.ProbeStatus);
            Assert.Equal(MediaPlaybackStatus.Unknown, reset.PlaybackStatus);
            Assert.Null(reset.ProbeError);
            Assert.Equal([videoPath], probeQueue.Paths);

            File.Delete(videoPath);
            await Assert.ThrowsAsync<InvalidOperationException>(() => mediaLibrary.ReanalyzeAsync(video.Id));
            await Assert.ThrowsAsync<InvalidOperationException>(() => mediaLibrary.SetEnabledAsync(int.MaxValue, enabled: true));
            await Assert.ThrowsAsync<InvalidOperationException>(() => mediaLibrary.ReanalyzeAsync(int.MaxValue));
            await mediaLibrary.MarkPlaybackFailedAsync(int.MaxValue);
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
            var probeQueue = new RecordingMediaProbeQueue();
            services.AddSingleton<IMediaProbeQueue>(probeQueue);
            await using var provider = services.BuildServiceProvider();
            await provider.GetRequiredService<IMediaFolderService>().AddAsync(mediaDirectory, includeSubdirectories: true);
            var scanner = provider.GetRequiredService<IMediaScanner>();

            var firstScan = await scanner.ScanAllAsync();

            Assert.Equal(new MediaScanResult(2, 0, 0, 0), firstScan);
            Assert.Equal(2, (await provider.GetRequiredService<IMediaLibraryService>().GetAllAsync()).Count);
            Assert.Equal(2, probeQueue.Paths.Count);

            probeQueue.Paths.Clear();
            await File.AppendAllTextAsync(topLevelVideo, "changed");
            File.Delete(nestedVideo);
            var secondScan = await scanner.ScanAllAsync();

            Assert.Equal(new MediaScanResult(0, 1, 1, 0), secondScan);
            var videos = await provider.GetRequiredService<IMediaLibraryService>().GetAllAsync();
            Assert.True(videos.Single(video => video.FileName == "intro.mp4").IsAvailable);
            Assert.False(videos.Single(video => video.FileName == "gameplay.mkv").IsAvailable);
            Assert.Equal([topLevelVideo], probeQueue.Paths);
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

    private sealed class RecordingMediaProbeQueue : IMediaProbeQueue
    {
        public List<string> Paths { get; } = [];

        public ValueTask QueueAsync(int mediaId, string fullPath, CancellationToken cancellationToken = default)
        {
            Paths.Add(fullPath);
            return ValueTask.CompletedTask;
        }
    }
}
