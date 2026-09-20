using BeamerPresenter.Application;
using BeamerPresenter.Domain;
using Microsoft.EntityFrameworkCore;

namespace BeamerPresenter.Infrastructure;

internal sealed class SqliteMediaFolderService(IDbContextFactory<PresenterDbContext> contextFactory) : IMediaFolderService
{
    public async Task<IReadOnlyList<MediaFolder>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.MediaFolders.AsNoTracking().OrderBy(folder => folder.Path).ToListAsync(cancellationToken);
    }

    public async Task<MediaFolder> AddAsync(string path, bool includeSubdirectories, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path.Trim());
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Der Videoordner '{fullPath}' wurde nicht gefunden.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await context.MediaFolders.SingleOrDefaultAsync(folder => folder.Path == fullPath, cancellationToken);
        if (existing is not null)
        {
            existing.Enabled = true;
            existing.IncludeSubdirectories = includeSubdirectories;
            await context.SaveChangesAsync(cancellationToken);
            return existing;
        }

        var mediaFolder = new MediaFolder { Path = fullPath, IncludeSubdirectories = includeSubdirectories };
        context.MediaFolders.Add(mediaFolder);
        await context.SaveChangesAsync(cancellationToken);
        return mediaFolder;
    }

    public async Task RemoveAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var mediaFolder = await context.MediaFolders.SingleOrDefaultAsync(folder => folder.Id == id, cancellationToken);
        if (mediaFolder is null)
        {
            return;
        }

        context.MediaFolders.Remove(mediaFolder);
        await context.SaveChangesAsync(cancellationToken);
    }
}
