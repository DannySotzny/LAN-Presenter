using BeamerPresenter.Application;
using BeamerPresenter.Domain;
using BeamerPresenter.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeamerPresenter.Infrastructure.Tests;

public sealed class MediaFolderWatcherTests
{
    [Fact]
    public async Task Supported_file_event_triggers_debounced_reconciliation()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "BeamerPresenter.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        var folderService = new StubMediaFolderService(testRoot);
        var scanner = new RecordingMediaScanner();
        var watcher = new MediaFolderWatcher(folderService, scanner, NullLogger<MediaFolderWatcher>.Instance);

        try
        {
            await watcher.StartAsync(CancellationToken.None);
            await WaitForWatcherAsync(watcher);

            await File.WriteAllBytesAsync(Path.Combine(testRoot, "new-video.mp4"), [1, 2, 3]);

            await scanner.WaitForScanAsync();
            Assert.Equal(1, scanner.ScanCount);
        }
        finally
        {
            await watcher.StopAsync(CancellationToken.None);
            watcher.Dispose();
            var allowedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BeamerPresenter.Tests"));
            var resolvedRoot = Path.GetFullPath(testRoot);
            if (resolvedRoot.StartsWith(allowedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolvedRoot))
            {
                Directory.Delete(resolvedRoot, recursive: true);
            }
        }
    }

    private static async Task WaitForWatcherAsync(MediaFolderWatcher watcher)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (watcher.ActiveWatcherCount == 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
        }
    }

    private sealed class StubMediaFolderService(string path) : IMediaFolderService
    {
        private readonly MediaFolder folder = new()
        {
            Id = 1,
            Path = path,
            Enabled = true,
            IncludeSubdirectories = true
        };

        public Task<IReadOnlyList<MediaFolder>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MediaFolder>>([folder]);

        public Task<MediaFolder> AddAsync(string path, bool includeSubdirectories, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RemoveAsync(int id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingMediaScanner : IMediaScanner
    {
        private readonly TaskCompletionSource scanCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int scanCount;

        public int ScanCount => Volatile.Read(ref scanCount);

        public Task<MediaScanResult> ScanAllAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref scanCount);
            scanCompleted.TrySetResult();
            return Task.FromResult(new MediaScanResult(0, 0, 0, 0));
        }

        public async Task WaitForScanAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await scanCompleted.Task.WaitAsync(timeout.Token);
        }
    }
}
