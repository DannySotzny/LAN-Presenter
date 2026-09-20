using BeamerPresenter.Application;
using BeamerPresenter.Domain;
using BeamerPresenter.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeamerPresenter.Infrastructure.Tests;

public sealed class MediaProbeQueueTests
{
    [Fact]
    public async Task Queue_limits_concurrency_and_persists_probe_results()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "BeamerPresenter.Tests", Guid.NewGuid().ToString("N"));
        var dataDirectory = Path.Combine(testRoot, "Data");
        var mediaDirectory = Path.Combine(testRoot, "Media");
        Directory.CreateDirectory(mediaDirectory);
        for (var index = 1; index <= 3; index++)
        {
            await File.WriteAllBytesAsync(Path.Combine(mediaDirectory, $"video-{index}.mp4"), [1, 2, 3, (byte)index]);
        }

        MediaProbeQueue? queue = null;
        ServiceProvider? provider = null;
        try
        {
            PresenterDatabase.GetConfiguredWebPort(dataDirectory);
            var ffprobe = new ConcurrentFfprobeService();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddPresenterInfrastructure(dataDirectory);
            services.AddSingleton<IFileStabilityChecker, ImmediateFileStabilityChecker>();
            services.AddSingleton<IFfprobeService>(ffprobe);
            provider = services.BuildServiceProvider();
            queue = provider.GetRequiredService<MediaProbeQueue>();
            await queue.StartAsync(CancellationToken.None);
            await provider.GetRequiredService<IMediaFolderService>().AddAsync(mediaDirectory, includeSubdirectories: false);

            await provider.GetRequiredService<IMediaScanner>().ScanAllAsync();

            var videos = await WaitForAnalyzedVideosAsync(provider.GetRequiredService<IMediaLibraryService>());
            Assert.Equal(3, videos.Count);
            Assert.All(videos, video =>
            {
                Assert.Equal(MediaProbeStatus.Valid, video.ProbeStatus);
                Assert.Equal(MediaPlaybackStatus.Supported, video.PlaybackStatus);
                Assert.Equal(TimeSpan.FromSeconds(42), video.Duration);
                Assert.Equal("h264", video.VideoCodec);
            });
            Assert.Equal(2, ffprobe.MaximumConcurrency);
        }
        finally
        {
            if (queue is not null)
            {
                await queue.StopAsync(CancellationToken.None);
            }

            if (provider is not null)
            {
                await provider.DisposeAsync();
            }

            SqliteConnection.ClearAllPools();
            var allowedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BeamerPresenter.Tests"));
            var resolvedRoot = Path.GetFullPath(testRoot);
            if (resolvedRoot.StartsWith(allowedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolvedRoot))
            {
                Directory.Delete(resolvedRoot, recursive: true);
            }
        }
    }

    private static async Task<IReadOnlyList<VideoAsset>> WaitForAnalyzedVideosAsync(IMediaLibraryService mediaLibrary)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var videos = await mediaLibrary.GetAllAsync(timeout.Token);
            if (videos.Count == 3 && videos.All(video => video.ProbeStatus == MediaProbeStatus.Valid))
            {
                return videos;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
        }
    }

    private sealed class ImmediateFileStabilityChecker : IFileStabilityChecker
    {
        public Task<StableFileSnapshot?> WaitForStableFileAsync(string fullPath, CancellationToken cancellationToken)
        {
            var file = new FileInfo(fullPath);
            return Task.FromResult<StableFileSnapshot?>(
                new StableFileSnapshot(file.Length, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero)));
        }
    }

    private sealed class ConcurrentFfprobeService : IFfprobeService
    {
        private int activeCount;
        private int maximumConcurrency;

        public int MaximumConcurrency => Volatile.Read(ref maximumConcurrency);

        public Task<FfprobeAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new FfprobeAvailability(true, "ffprobe.exe", "test", null));

        public Task<FfprobeAvailability> InstallWithWinGetAsync(CancellationToken cancellationToken = default) =>
            CheckAvailabilityAsync(cancellationToken);

        public async Task<MediaProbeResult> ProbeAsync(string mediaPath, CancellationToken cancellationToken = default)
        {
            var currentConcurrency = Interlocked.Increment(ref activeCount);
            UpdateMaximumConcurrency(currentConcurrency);
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                return new MediaProbeResult(
                    MediaProbeStatus.Valid,
                    MediaPlaybackStatus.Supported,
                    TimeSpan.FromSeconds(42),
                    "mp4",
                    "h264",
                    1920,
                    1080,
                    60,
                    "aac",
                    2,
                    null);
            }
            finally
            {
                Interlocked.Decrement(ref activeCount);
            }
        }

        private void UpdateMaximumConcurrency(int currentConcurrency)
        {
            var observedMaximum = Volatile.Read(ref maximumConcurrency);
            while (currentConcurrency > observedMaximum)
            {
                var original = Interlocked.CompareExchange(ref maximumConcurrency, currentConcurrency, observedMaximum);
                if (original == observedMaximum)
                {
                    return;
                }

                observedMaximum = original;
            }
        }
    }
}
