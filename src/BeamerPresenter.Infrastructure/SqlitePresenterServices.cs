using System.Security.Cryptography;
using BeamerPresenter.Application;
using BeamerPresenter.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeamerPresenter.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPresenterInfrastructure(this IServiceCollection services, string dataDirectory, string? toolsDirectory = null)
    {
        Directory.CreateDirectory(dataDirectory);
        services.AddDbContextFactory<PresenterDbContext>(options => options.UseSqlite(PresenterDatabase.CreateConnectionString(dataDirectory)));
        services.AddSingleton<IPresenterSettingsService, SqlitePresenterSettingsService>();
        services.AddSingleton<IMediaFolderService, SqliteMediaFolderService>();
        services.AddSingleton<IMediaLibraryService, SqliteMediaLibraryService>();
        services.AddSingleton<IPlaybackStore, SqlitePlaybackStore>();
        services.AddSingleton<INewsService, SqliteNewsService>();
        services.AddSingleton<SqliteMediaScanner>();
        services.AddSingleton<IMediaScanner>(provider => provider.GetRequiredService<SqliteMediaScanner>());
        services.AddSingleton<IMediaScannerStatus>(provider => provider.GetRequiredService<SqliteMediaScanner>());
        services.AddSingleton<IFileStabilityChecker, FileStabilityChecker>();
        services.AddSingleton<MediaProbeQueue>();
        services.AddSingleton<IMediaProbeQueue>(provider => provider.GetRequiredService<MediaProbeQueue>());
        services.AddSingleton<IExternalProcessRunner, ExternalProcessRunner>();
        services.AddSingleton<IFfprobeService>(provider => new FfprobeService(
            provider.GetRequiredService<IPresenterSettingsService>(),
            provider.GetRequiredService<IExternalProcessRunner>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<FfprobeService>>(),
            toolsDirectory ?? Path.Combine(Directory.GetParent(Path.GetFullPath(dataDirectory))?.FullName ?? dataDirectory, "Tools")));
        services.AddHostedService<MediaProbeQueue>(provider => provider.GetRequiredService<MediaProbeQueue>());
        services.AddHostedService<MediaFolderWatcher>();
        services.AddHostedService<MediaReconciliationWorker>();
        services.AddSingleton<PlaybackController>();
        services.AddSingleton<IRandomSource, SystemRandomSource>();
        services.AddSingleton<MediaSegmentPlanner>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<PlaybackQueueService>();
        services.AddHostedService<QueuePlanningWorker>();
        services.AddHostedService(provider => new PresenterBackupWorker(
            dataDirectory,
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<PresenterBackupWorker>>()));
        return services;
    }
}

public static class PresenterDatabase
{
    private const string InitialMigrationId = "20260920165033_InitialSchema";

    public static int GetConfiguredWebPort(string dataDirectory) =>
        GetConfiguredHostSettings(dataDirectory).WebPort;

    public static PresenterHostSettings GetConfiguredHostSettings(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var options = new DbContextOptionsBuilder<PresenterDbContext>().UseSqlite(CreateConnectionString(dataDirectory)).Options;
        using var context = new PresenterDbContext(options);
        var hasExistingSchema = BaselineLegacyDatabase(context);
        if (hasExistingSchema && context.Database.GetPendingMigrations().Any())
        {
            CreateBackup(context, dataDirectory);
        }

        context.Database.Migrate();
        context.Database.ExecuteSqlRaw("PRAGMA journal_mode = WAL;");
        var configured = context.Settings.AsNoTracking()
            .Where(settings => settings.Id == 1)
            .Select(settings => new { settings.WebPort, settings.AllowLanAccess })
            .SingleOrDefault();
        var configuredPort = configured?.WebPort;
        var webPort = configuredPort is >= 1024 and <= 65535 ? configuredPort.Value : PresenterSettings.DefaultWebPort;
        return new PresenterHostSettings(webPort, configured?.AllowLanAccess ?? false);
    }

    public static string CreateDailyBackup(string dataDirectory) =>
        CreateDailyBackup(dataDirectory, DateTimeOffset.UtcNow);

    internal static string CreateDailyBackup(string dataDirectory, DateTimeOffset timestamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var databasePath = Path.Combine(Path.GetFullPath(dataDirectory), "presenter.db");
        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException("Die Presenter-Datenbank wurde noch nicht angelegt.", databasePath);
        }

        var presenterDirectory = Directory.GetParent(Path.GetFullPath(dataDirectory))?.FullName ?? Path.GetFullPath(dataDirectory);
        var backupDirectory = Path.Combine(presenterDirectory, "Backup");
        Directory.CreateDirectory(backupDirectory);
        var backupPath = Path.Combine(backupDirectory, $"presenter-daily-{timestamp.UtcDateTime:yyyyMMdd}.db");
        if (File.Exists(backupPath))
        {
            return backupPath;
        }

        var temporaryPath = backupPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using var sourceConnection = new SqliteConnection(CreateConnectionString(dataDirectory));
            sourceConnection.Open();
            using (var destinationConnection = new SqliteConnection(
                       new SqliteConnectionStringBuilder { DataSource = temporaryPath, Pooling = false }.ToString()))
            {
                destinationConnection.Open();
                sourceConnection.BackupDatabase(destinationConnection);
            }

            try
            {
                File.Move(temporaryPath, backupPath);
            }
            catch (IOException) when (File.Exists(backupPath))
            {
                File.Delete(temporaryPath);
            }

            foreach (var obsoleteBackup in Directory.GetFiles(backupDirectory, "presenter-daily-*.db")
                         .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                         .Skip(7))
            {
                File.Delete(obsoleteBackup);
            }

            return backupPath;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    internal static string CreateConnectionString(string dataDirectory)
    {
        var databasePath = Path.Combine(dataDirectory, "presenter.db");
        return new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = true,
            DefaultTimeout = 5
        }.ToString();
    }

    private static bool BaselineLegacyDatabase(PresenterDbContext context)
    {
        var connection = context.Database.GetDbConnection();
        connection.Open();
        var hasSettings = TableExists(connection, "Settings");
        var hasVideos = TableExists(connection, "Videos");
        if (!hasSettings && !hasVideos)
        {
            return false;
        }

        if (!hasSettings || !hasVideos)
        {
            throw new InvalidOperationException("Die bestehende Presenter-Datenbank besitzt kein vollständiges Basisschema.");
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
                "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                "ProductVersion" TEXT NOT NULL
            );
            INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
            VALUES ('{InitialMigrationId}', '10.0.11');
            """;
        command.ExecuteNonQuery();
        return true;
    }

    private static void CreateBackup(PresenterDbContext context, string dataDirectory)
    {
        var presenterDirectory = Directory.GetParent(Path.GetFullPath(dataDirectory))?.FullName ?? Path.GetFullPath(dataDirectory);
        var backupDirectory = Path.Combine(presenterDirectory, "Backup");
        Directory.CreateDirectory(backupDirectory);
        var backupPath = Path.Combine(backupDirectory, $"presenter-before-migration-{DateTime.UtcNow:yyyyMMddHHmmssfff}.db");
        var sourceConnection = (SqliteConnection)context.Database.GetDbConnection();
        using (var destinationConnection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath }.ToString()))
        {
            destinationConnection.Open();
            sourceConnection.BackupDatabase(destinationConnection);
        }

        foreach (var obsoleteBackup in Directory.GetFiles(backupDirectory, "presenter-before-migration-*.db")
                     .OrderByDescending(File.GetCreationTimeUtc)
                     .Skip(7))
        {
            File.Delete(obsoleteBackup);
        }
    }

    private static bool TableExists(System.Data.Common.DbConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $tableName;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$tableName";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }
}

public sealed record PresenterHostSettings(int WebPort, bool AllowLanAccess);

internal sealed class PresenterBackupWorker(
    string dataDirectory,
    TimeProvider timeProvider,
    ILogger<PresenterBackupWorker> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await CreateBackupSafelyAsync(stoppingToken);
        using var timer = new PeriodicTimer(CheckInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await CreateBackupSafelyAsync(stoppingToken);
        }
    }

    internal Task CreateBackupAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PresenterDatabase.CreateDailyBackup(dataDirectory, timeProvider.GetUtcNow());
        return Task.CompletedTask;
    }

    private async Task CreateBackupSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await CreateBackupAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Daily presenter database backup failed");
        }
    }
}

internal sealed class SqlitePresenterSettingsService(IDbContextFactory<PresenterDbContext> contextFactory) : IPresenterSettingsService
{
    private const int PasswordIterations = 600_000;
    private readonly SemaphoreSlim commandGate = new(1, 1);

    public async Task<PresenterSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        await commandGate.WaitAsync(cancellationToken);
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            return await GetOrCreateAsync(context, cancellationToken);
        }
        finally
        {
            commandGate.Release();
        }
    }

    public async Task SaveAsync(PresenterSettings settings, CancellationToken cancellationToken = default)
    {
        await commandGate.WaitAsync(cancellationToken);
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            var existing = await context.Settings.SingleOrDefaultAsync(x => x.Id == 1, cancellationToken);
            if (existing is null)
            {
                context.Settings.Add(settings);
            }
            else
            {
                existing.WebPort = settings.WebPort;
                existing.AllowLanAccess = settings.AllowLanAccess;
                existing.MediaFolder = settings.MediaFolder;
                existing.FfprobePath = settings.FfprobePath;
                existing.ChromePath = settings.ChromePath;
                existing.MonitorDeviceName = settings.MonitorDeviceName;
                existing.AlwaysOnTop = settings.AlwaysOnTop;
                existing.AggressiveTopmost = settings.AggressiveTopmost;
                existing.PreventDisplaySleep = settings.PreventDisplaySleep;
                existing.PreventSystemSleep = settings.PreventSystemSleep;
                existing.ShortVideoThresholdSeconds = settings.ShortVideoThresholdSeconds;
                existing.ClipLengthMinSeconds = settings.ClipLengthMinSeconds;
                existing.ClipLengthMaxSeconds = settings.ClipLengthMaxSeconds;
                existing.VideoCooldownCount = settings.VideoCooldownCount;
                existing.TimeCooldownMinutes = settings.TimeCooldownMinutes;
                existing.QueueTargetLength = settings.QueueTargetLength;
            }

            await context.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            commandGate.Release();
        }
    }

    public async Task SetWebPasswordAsync(string password, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        await commandGate.WaitAsync(cancellationToken);
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            var settings = await GetOrCreateAsync(context, cancellationToken);
            var salt = RandomNumberGenerator.GetBytes(16);
            var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, PasswordIterations, HashAlgorithmName.SHA512, 32);
            settings.PasswordSalt = Convert.ToBase64String(salt);
            settings.PasswordHash = Convert.ToBase64String(hash);
            await context.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            commandGate.Release();
        }
    }

    public async Task<bool> VerifyWebPasswordAsync(string password, CancellationToken cancellationToken = default)
    {
        var settings = await GetAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.PasswordHash) || string.IsNullOrWhiteSpace(settings.PasswordSalt))
        {
            return false;
        }

        var expected = Convert.FromBase64String(settings.PasswordHash);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(settings.PasswordSalt), PasswordIterations, HashAlgorithmName.SHA512, expected.Length);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    public async Task<bool> HasWebPasswordAsync(CancellationToken cancellationToken = default) =>
        !string.IsNullOrWhiteSpace((await GetAsync(cancellationToken)).PasswordHash);

    private static async Task<PresenterSettings> GetOrCreateAsync(
        PresenterDbContext context,
        CancellationToken cancellationToken)
    {
        var settings = await context.Settings.SingleOrDefaultAsync(x => x.Id == 1, cancellationToken);
        if (settings is not null)
        {
            return settings;
        }

        settings = new PresenterSettings { MediaFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Beamer Presenter") };
        context.Settings.Add(settings);
        await context.SaveChangesAsync(cancellationToken);
        return settings;
    }
}

internal sealed class SqlitePlaybackStore(IDbContextFactory<PresenterDbContext> contextFactory) : IPlaybackStore
{
    public async Task<IReadOnlyList<QueueEntry>> GetQueueAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.QueueEntries
            .AsNoTracking()
            .Where(entry => entry.Status == QueueEntryStatus.Pending || entry.Status == QueueEntryStatus.Playing)
            .OrderBy(entry => entry.SortOrder)
            .ThenBy(entry => entry.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<QueueEntry> AddQueueEntryAsync(QueueEntry entry, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.QueueEntries.Add(entry);
        await context.SaveChangesAsync(cancellationToken);
        return entry;
    }

    public async Task UpdateQueueEntryAsync(QueueEntry entry, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.QueueEntries.Update(entry);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PlaybackHistory>> GetHistoryAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var history = await context.PlaybackHistory.AsNoTracking().ToListAsync(cancellationToken);
        return history.OrderByDescending(entry => entry.StartedUtc).ThenByDescending(entry => entry.Id).ToList();
    }

    public async Task<PlaybackHistory> AddHistoryAsync(PlaybackHistory entry, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.PlaybackHistory.Add(entry);
        await context.SaveChangesAsync(cancellationToken);
        return entry;
    }

    public async Task UpdateHistoryAsync(PlaybackHistory entry, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.PlaybackHistory.Update(entry);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task ClearHistoryAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.PlaybackHistory.ExecuteDeleteAsync(cancellationToken);
    }
}

internal sealed class SqliteNewsService(IDbContextFactory<PresenterDbContext> contextFactory) : INewsService
{
    public async Task<IReadOnlyList<NewsItem>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var items = await context.NewsItems.AsNoTracking().ToListAsync(cancellationToken);
        return items.OrderByDescending(item => item.Priority).ThenByDescending(item => item.CreatedUtc).ToList();
    }

    public async Task<NewsItem> AddAsync(NewsItem item, CancellationToken cancellationToken = default)
    {
        Validate(item);
        item.CreatedUtc = item.CreatedUtc == default ? DateTimeOffset.UtcNow : item.CreatedUtc;
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.NewsItems.Add(item);
        await context.SaveChangesAsync(cancellationToken);
        return item;
    }

    public async Task UpdateAsync(NewsItem item, CancellationToken cancellationToken = default)
    {
        Validate(item);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await context.NewsItems.AnyAsync(existing => existing.Id == item.Id, cancellationToken))
        {
            throw new InvalidOperationException("Die News wurde nicht gefunden.");
        }

        context.NewsItems.Update(item);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var deleted = await context.NewsItems.Where(item => item.Id == id).ExecuteDeleteAsync(cancellationToken);
        if (deleted == 0)
        {
            throw new InvalidOperationException("Die News wurde nicht gefunden.");
        }
    }

    private static void Validate(NewsItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(item.Title) || item.Title.Length > 200)
        {
            throw new ArgumentException("Der News-Titel muss zwischen 1 und 200 Zeichen lang sein.", nameof(item));
        }

        if (string.IsNullOrWhiteSpace(item.Text) || item.Text.Length > 4000)
        {
            throw new ArgumentException("Der News-Text muss zwischen 1 und 4000 Zeichen lang sein.", nameof(item));
        }

        if (!item.Permanent && (!item.Duration.HasValue || item.Duration.Value <= TimeSpan.Zero))
        {
            throw new ArgumentException("Zeitlich begrenzte News benötigen eine positive Dauer.", nameof(item));
        }

        if (item.ValidFrom.HasValue && item.ValidUntil.HasValue && item.ValidUntil <= item.ValidFrom)
        {
            throw new ArgumentException("Das Gültigkeitsende muss nach dem Beginn liegen.", nameof(item));
        }
    }
}

internal sealed class SqliteMediaLibraryService(
    IDbContextFactory<PresenterDbContext> contextFactory,
    IMediaFolderService mediaFolderService,
    IMediaProbeQueue mediaProbeQueue) : IMediaLibraryService
{
    public async Task<IReadOnlyList<VideoAsset>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var videos = await context.Videos.AsNoTracking().ToListAsync(cancellationToken);
        return videos.OrderByDescending(x => x.AddedAtUtc).ToList();
    }

    public async Task<VideoAsset?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Videos.AsNoTracking().SingleOrDefaultAsync(video => video.Id == id, cancellationToken);
    }

    public async Task<VideoAsset> AddUploadAsync(string originalFileName, Stream content, long length, CancellationToken cancellationToken = default)
    {
        var safeName = Path.GetFileName(originalFileName);
        if (string.IsNullOrWhiteSpace(safeName) || !MediaFileSupport.IsSupported(safeName))
        {
            throw new InvalidOperationException("Dieses Videoformat wird nicht unterstützt.");
        }

        var mediaFolder = (await mediaFolderService.GetAllAsync(cancellationToken)).FirstOrDefault(folder => folder.Enabled);
        if (mediaFolder is null)
        {
            throw new InvalidOperationException("Bitte zuerst einen Videoordner in der Desktop-App festlegen.");
        }

        Directory.CreateDirectory(mediaFolder.Path);
        var destinationPath = MakeUniquePath(mediaFolder.Path, safeName);
        await using (var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true))
        {
            await content.CopyToAsync(destination, cancellationToken);
        }

        var file = new FileInfo(destinationPath);
        var now = DateTimeOffset.UtcNow;
        var asset = new VideoAsset
        {
            FileName = file.Name,
            FullPath = file.FullName,
            FileSize = file.Length,
            AddedAtUtc = now,
            LastWriteUtc = file.LastWriteTimeUtc,
            LastScannedUtc = now,
            IsAvailable = true
        };
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.Videos.Add(asset);
        await context.SaveChangesAsync(cancellationToken);
        await mediaProbeQueue.QueueAsync(asset.Id, asset.FullPath, cancellationToken);
        return asset;
    }

    public async Task SetEnabledAsync(int id, bool enabled, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var asset = await context.Videos.SingleOrDefaultAsync(video => video.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Das Video wurde nicht gefunden.");
        asset.Enabled = enabled;
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task ReanalyzeAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var asset = await context.Videos.SingleOrDefaultAsync(video => video.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Das Video wurde nicht gefunden.");
        if (!asset.IsAvailable || !File.Exists(asset.FullPath))
        {
            throw new InvalidOperationException("Die Videodatei ist nicht verfügbar.");
        }

        asset.ProbeStatus = MediaProbeStatus.Unknown;
        asset.PlaybackStatus = MediaPlaybackStatus.Unknown;
        asset.ProbeError = null;
        await context.SaveChangesAsync(cancellationToken);
        await mediaProbeQueue.QueueAsync(asset.Id, asset.FullPath, cancellationToken);
    }

    public async Task MarkPlaybackFailedAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var asset = await context.Videos.SingleOrDefaultAsync(video => video.Id == id, cancellationToken);
        if (asset is null)
        {
            return;
        }

        asset.PlaybackStatus = MediaPlaybackStatus.Failed;
        await context.SaveChangesAsync(cancellationToken);
    }

    private static string MakeUniquePath(string folder, string fileName)
    {
        var candidate = Path.Combine(folder, fileName);
        if (!File.Exists(candidate)) return candidate;
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        return Path.Combine(folder, $"{baseName}-{DateTime.UtcNow:yyyyMMddHHmmss}{extension}");
    }
}
