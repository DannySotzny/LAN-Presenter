using BeamerPresenter.Application;
using BeamerPresenter.Domain;
using BeamerPresenter.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeamerPresenter.Infrastructure.Tests;

public sealed class MediaPreviewWorkerTests
{
    [Fact]
    public async Task Generates_preview_in_background_and_invalidates_it_when_source_changes()
    {
        var root = Path.Combine(Path.GetTempPath(), "BeamerPresenter.Tests", Guid.NewGuid().ToString("N"));
        var mediaFolder = Path.Combine(root, "Media");
        var toolsFolder = Path.Combine(root, "Tools");
        Directory.CreateDirectory(mediaFolder);
        Directory.CreateDirectory(toolsFolder);
        var source = Path.Combine(mediaFolder, "clip.mp4");
        var probe = Path.Combine(toolsFolder, "ffprobe.exe");
        var ffmpeg = Path.Combine(toolsFolder, "ffmpeg.exe");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        await File.WriteAllBytesAsync(probe, [1]);
        await File.WriteAllBytesAsync(ffmpeg, [1]);
        var sourceInfo = new FileInfo(source);
        var asset = new VideoAsset
        {
            Id = 7,
            FileName = sourceInfo.Name,
            FullPath = source,
            FileSize = sourceInfo.Length,
            LastWriteUtc = sourceInfo.LastWriteTimeUtc,
            AddedAtUtc = DateTimeOffset.UtcNow,
            Duration = TimeSpan.FromSeconds(40),
            ProbeStatus = MediaProbeStatus.Valid
        };
        var previews = new MediaPreviewService(Path.Combine(root, "Data"));
        var runner = new PreviewProcessRunner();
        var worker = new MediaPreviewWorker(new PreviewMediaLibrary(asset), new PreviewFolders(mediaFolder),
            new PreviewProbe(probe), runner, previews, NullLogger<MediaPreviewWorker>.Instance);

        try
        {
            Assert.Null(previews.GetReadyPreviewPath(asset));
            await worker.GeneratePendingAsync();
            var first = previews.GetReadyPreviewPath(asset);
            Assert.NotNull(first);
            Assert.True(File.Exists(first));
            Assert.Contains("fps=1/5", runner.LastArguments![Array.IndexOf(runner.LastArguments, "-vf") + 1], StringComparison.Ordinal);

            await worker.GeneratePendingAsync();
            Assert.Equal(1, runner.CallCount);

            await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);
            File.SetLastWriteTimeUtc(source, sourceInfo.LastWriteTimeUtc.AddMinutes(1));
            Assert.Null(previews.GetReadyPreviewPath(asset));
            sourceInfo.Refresh();
            asset.FileSize = sourceInfo.Length;
            asset.LastWriteUtc = sourceInfo.LastWriteTimeUtc;
            await worker.GeneratePendingAsync();

            Assert.Equal(2, runner.CallCount);
            Assert.NotEqual(first, previews.GetReadyPreviewPath(asset));
            Assert.False(File.Exists(first));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class PreviewProcessRunner : IExternalProcessRunner
    {
        public int CallCount { get; private set; }
        public string[]? LastArguments { get; private set; }

        public async Task<ProcessExecutionResult> RunAsync(string executablePath, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            Assert.EndsWith("ffmpeg.exe", executablePath, StringComparison.OrdinalIgnoreCase);
            LastArguments = [.. arguments];
            CallCount++;
            await File.WriteAllBytesAsync(arguments[^1], [0xff, 0xd8, 0xff, 0xd9], cancellationToken);
            return new ProcessExecutionResult(0, string.Empty, string.Empty);
        }
    }

    private sealed class PreviewMediaLibrary(VideoAsset asset) : IMediaLibraryService
    {
        public Task<IReadOnlyList<VideoAsset>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<VideoAsset>>([asset]);
        public Task<VideoAsset?> GetByIdAsync(int id, CancellationToken cancellationToken = default) =>
            Task.FromResult<VideoAsset?>(id == asset.Id ? asset : null);
        public Task<VideoAsset> AddUploadAsync(string originalFileName, Stream content, long length, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetEnabledAsync(int id, bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReanalyzeAsync(int id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task MarkPlaybackFailedAsync(int id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class PreviewFolders(string path) : IMediaFolderService
    {
        public Task<IReadOnlyList<MediaFolder>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MediaFolder>>([new MediaFolder { Id = 1, Path = path }]);
        public Task<MediaFolder> AddAsync(string path, bool includeSubdirectories, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveAsync(int id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class PreviewProbe(string path) : IFfprobeService
    {
        public Task<FfprobeAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new FfprobeAvailability(true, path, "test", null));
        public Task<FfprobeAvailability> InstallWithWinGetAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MediaProbeResult> ProbeAsync(string mediaPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
