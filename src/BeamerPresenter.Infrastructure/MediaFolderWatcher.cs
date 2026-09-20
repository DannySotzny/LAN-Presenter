using System.Threading.Channels;
using BeamerPresenter.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeamerPresenter.Infrastructure;

internal sealed class MediaFolderWatcher(
    IMediaFolderService mediaFolderService,
    IMediaScanner mediaScanner,
    ILogger<MediaFolderWatcher> logger) : BackgroundService
{
    private static readonly TimeSpan DebouncePeriod = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FolderRefreshInterval = TimeSpan.FromSeconds(10);
    private readonly Channel<bool> changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropWrite,
        AllowSynchronousContinuations = false
    });
    private readonly Dictionary<string, WatcherRegistration> watchers = new(StringComparer.OrdinalIgnoreCase);
    private int activeWatcherCount;

    internal int ActiveWatcherCount => Volatile.Read(ref activeWatcherCount);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RefreshWatchersSafelyAsync(stoppingToken);
            await Task.WhenAll(ProcessChangesAsync(stoppingToken), RefreshWatchersPeriodicallyAsync(stoppingToken));
        }
        finally
        {
            foreach (var registration in watchers.Values)
            {
                registration.Watcher.Dispose();
            }

            watchers.Clear();
            Volatile.Write(ref activeWatcherCount, 0);
        }
    }

    private async Task ProcessChangesAsync(CancellationToken cancellationToken)
    {
        await foreach (var changeSignal in changes.Reader.ReadAllAsync(cancellationToken))
        {
            _ = changeSignal;
            await Task.Delay(DebouncePeriod, cancellationToken);
            while (changes.Reader.TryRead(out _))
            {
            }

            try
            {
                await mediaScanner.ScanAllAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Media reconciliation after a file-system event failed");
            }
        }
    }

    private async Task RefreshWatchersPeriodicallyAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(FolderRefreshInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            await RefreshWatchersSafelyAsync(cancellationToken);
        }
    }

    private async Task RefreshWatchersSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            var folders = (await mediaFolderService.GetAllAsync(cancellationToken))
                .Where(folder => folder.Enabled && Directory.Exists(folder.Path))
                .ToDictionary(folder => Path.GetFullPath(folder.Path), StringComparer.OrdinalIgnoreCase);

            foreach (var obsoletePath in watchers.Keys.Where(path => !folders.ContainsKey(path)).ToList())
            {
                watchers.Remove(obsoletePath, out var obsoleteWatcher);
                obsoleteWatcher?.Watcher.Dispose();
            }

            foreach (var (path, folder) in folders)
            {
                if (watchers.TryGetValue(path, out var current)
                    && current.IncludeSubdirectories == folder.IncludeSubdirectories)
                {
                    continue;
                }

                current?.Watcher.Dispose();
                watchers[path] = new WatcherRegistration(CreateWatcher(path, folder.IncludeSubdirectories), folder.IncludeSubdirectories);
            }

            Volatile.Write(ref activeWatcherCount, watchers.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Refreshing media folder watchers failed");
        }
    }

    private FileSystemWatcher CreateWatcher(string path, bool includeSubdirectories)
    {
        var watcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = includeSubdirectories,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
            Filter = "*.*"
        };
        watcher.Created += HandleChange;
        watcher.Changed += HandleChange;
        watcher.Deleted += HandleChange;
        watcher.Renamed += HandleRename;
        watcher.Error += HandleError;
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private void HandleChange(object sender, FileSystemEventArgs eventArgs)
    {
        if (MediaFileSupport.IsSupported(eventArgs.FullPath))
        {
            changes.Writer.TryWrite(true);
        }
    }

    private void HandleRename(object sender, RenamedEventArgs eventArgs)
    {
        if (MediaFileSupport.IsSupported(eventArgs.FullPath) || MediaFileSupport.IsSupported(eventArgs.OldFullPath))
        {
            changes.Writer.TryWrite(true);
        }
    }

    private void HandleError(object sender, ErrorEventArgs eventArgs)
    {
        logger.LogWarning(eventArgs.GetException(), "A media folder watcher lost file-system events; reconciliation was requested");
        changes.Writer.TryWrite(true);
    }

    private sealed record WatcherRegistration(FileSystemWatcher Watcher, bool IncludeSubdirectories);
}
