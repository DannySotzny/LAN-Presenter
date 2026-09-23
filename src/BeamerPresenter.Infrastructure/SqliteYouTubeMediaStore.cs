using BeamerPresenter.Application;
using BeamerPresenter.Domain;
using Microsoft.EntityFrameworkCore;

namespace BeamerPresenter.Infrastructure;

internal sealed class SqliteYouTubeMediaStore(IDbContextFactory<PresenterDbContext> contextFactory) : IYouTubeMediaStore
{
    public async Task<VideoAsset?> GetBySourceKeyAsync(string sourceKey, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Videos.AsNoTracking().SingleOrDefaultAsync(video => video.YouTubeSourceKey == sourceKey, cancellationToken);
    }

    public async Task<VideoAsset> RegisterDownloadedAsync(string sourceKey, string fullPath, CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(fullPath);
        if (!file.Exists || !MediaFileSupport.IsSupported(file.Name))
        {
            throw new InvalidOperationException("Die heruntergeladene Videodatei ist nicht verfügbar.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var bySource = await context.Videos.SingleOrDefaultAsync(video => video.YouTubeSourceKey == sourceKey, cancellationToken);
        var byPath = await context.Videos.SingleOrDefaultAsync(video => video.FullPath == file.FullName, cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (bySource is not null && byPath is not null && bySource.Id != byPath.Id)
        {
            bySource.YouTubeSourceKey = null;
            await context.SaveChangesAsync(cancellationToken);
        }

        var asset = byPath ?? bySource ?? new VideoAsset { FileName = file.Name, FullPath = file.FullName };
        if (asset.Id == 0) context.Videos.Add(asset);
        asset.YouTubeSourceKey = sourceKey;
        asset.FileName = file.Name;
        asset.FullPath = file.FullName;
        asset.FileSize = file.Length;
        asset.AddedAtUtc = DateTimeOffset.UtcNow;
        asset.LastWriteUtc = file.LastWriteTimeUtc;
        asset.LastScannedUtc = DateTimeOffset.UtcNow;
        asset.IsAvailable = true;
        asset.Enabled = true;
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return asset;
    }
}
