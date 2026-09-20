using BeamerPresenter.Application;
using BeamerPresenter.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeamerPresenter.Infrastructure;

internal sealed class SqliteMediaScanner(
    IDbContextFactory<PresenterDbContext> contextFactory,
    IMediaFolderService mediaFolderService,
    ILogger<SqliteMediaScanner> logger) : IMediaScanner
{
    public async Task<MediaScanResult> ScanAllAsync(CancellationToken cancellationToken = default)
    {
        var folders = (await mediaFolderService.GetAllAsync(cancellationToken)).Where(folder => folder.Enabled).ToList();
        var discoveredFiles = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder.Path))
            {
                logger.LogWarning("Configured media folder {MediaFolder} is not available", folder.Path);
                continue;
            }

            var enumerationOptions = new EnumerationOptions
            {
                RecurseSubdirectories = folder.IncludeSubdirectories,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                ReturnSpecialDirectories = false
            };
            foreach (var path in Directory.EnumerateFiles(folder.Path, "*", enumerationOptions).Where(MediaFileSupport.IsSupported))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = new FileInfo(path);
                discoveredFiles[file.FullName] = file;
            }
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var existingFiles = await context.Videos.ToListAsync(cancellationToken);
        var existingByPath = existingFiles.ToDictionary(video => video.FullPath, StringComparer.OrdinalIgnoreCase);
        var scanTimestamp = DateTimeOffset.UtcNow;
        var added = 0;
        var updated = 0;
        var unchanged = 0;
        foreach (var (path, file) in discoveredFiles)
        {
            var lastWriteUtc = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
            if (!existingByPath.TryGetValue(path, out var video))
            {
                context.Videos.Add(new VideoAsset
                {
                    FileName = file.Name,
                    FullPath = file.FullName,
                    FileSize = file.Length,
                    AddedAtUtc = scanTimestamp,
                    LastWriteUtc = lastWriteUtc,
                    LastScannedUtc = scanTimestamp,
                    IsAvailable = true
                });
                added++;
                continue;
            }

            var changed = video.FileSize != file.Length || video.LastWriteUtc != lastWriteUtc || !video.IsAvailable;
            video.FileName = file.Name;
            video.FileSize = file.Length;
            video.LastWriteUtc = lastWriteUtc;
            video.LastScannedUtc = scanTimestamp;
            video.IsAvailable = true;
            if (changed)
            {
                updated++;
            }
            else
            {
                unchanged++;
            }
        }

        var missing = 0;
        foreach (var video in existingFiles.Where(video => video.IsAvailable && !discoveredFiles.ContainsKey(video.FullPath)))
        {
            video.IsAvailable = false;
            video.LastScannedUtc = scanTimestamp;
            missing++;
        }

        await context.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Media reconciliation completed: {Added} added, {Updated} updated, {Missing} missing, {Unchanged} unchanged",
            added,
            updated,
            missing,
            unchanged);
        return new MediaScanResult(added, updated, missing, unchanged);
    }
}

internal sealed class MediaReconciliationWorker(
    IMediaScanner mediaScanner,
    ILogger<MediaReconciliationWorker> logger) : BackgroundService
{
    private static readonly TimeSpan ReconciliationInterval = TimeSpan.FromMinutes(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ScanSafelyAsync(stoppingToken);
        using var timer = new PeriodicTimer(ReconciliationInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await ScanSafelyAsync(stoppingToken);
        }
    }

    private async Task ScanSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await mediaScanner.ScanAllAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Media reconciliation failed");
        }
    }
}
