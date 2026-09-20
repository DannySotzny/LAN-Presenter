using BeamerPresenter.Domain;
using Microsoft.EntityFrameworkCore;

namespace BeamerPresenter.Infrastructure;

public sealed class PresenterDbContext(DbContextOptions<PresenterDbContext> options) : DbContext(options)
{
    public DbSet<PresenterSettings> Settings => Set<PresenterSettings>();
    public DbSet<VideoAsset> Videos => Set<VideoAsset>();
    public DbSet<MediaFolder> MediaFolders => Set<MediaFolder>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PresenterSettings>().HasKey(x => x.Id);
        modelBuilder.Entity<PresenterSettings>().Property(x => x.MediaFolder).HasMaxLength(1024);
        modelBuilder.Entity<VideoAsset>().HasIndex(x => x.FullPath).IsUnique();
        modelBuilder.Entity<VideoAsset>().Property(x => x.FileName).HasMaxLength(260);
        modelBuilder.Entity<VideoAsset>().Property(x => x.FullPath).HasMaxLength(4096).UseCollation("NOCASE");
        modelBuilder.Entity<MediaFolder>().HasIndex(x => x.Path).IsUnique();
        modelBuilder.Entity<MediaFolder>().Property(x => x.Path).HasMaxLength(4096).UseCollation("NOCASE");
    }
}
