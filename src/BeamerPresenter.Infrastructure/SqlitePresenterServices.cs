using System.Security.Cryptography;
using BeamerPresenter.Application;
using BeamerPresenter.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BeamerPresenter.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPresenterInfrastructure(this IServiceCollection services, string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        services.AddDbContextFactory<PresenterDbContext>(options => options.UseSqlite(PresenterDatabase.CreateConnectionString(dataDirectory)));
        services.AddSingleton<IPresenterSettingsService, SqlitePresenterSettingsService>();
        services.AddSingleton<IMediaLibraryService, SqliteMediaLibraryService>();
        services.AddSingleton<PlaybackController>();
        return services;
    }
}

public static class PresenterDatabase
{
    private const string InitialMigrationId = "20260920165033_InitialSchema";

    public static int GetConfiguredWebPort(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var options = new DbContextOptionsBuilder<PresenterDbContext>().UseSqlite(CreateConnectionString(dataDirectory)).Options;
        using var context = new PresenterDbContext(options);
        BaselineLegacyDatabase(context);
        context.Database.Migrate();
        context.Database.ExecuteSqlRaw("PRAGMA journal_mode = WAL;");
        var configuredPort = context.Settings.AsNoTracking().Where(x => x.Id == 1).Select(x => (int?)x.WebPort).SingleOrDefault();
        return configuredPort is >= 1024 and <= 65535 ? configuredPort.Value : PresenterSettings.DefaultWebPort;
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

    private static void BaselineLegacyDatabase(PresenterDbContext context)
    {
        var connection = context.Database.GetDbConnection();
        connection.Open();
        var hasSettings = TableExists(connection, "Settings");
        var hasVideos = TableExists(connection, "Videos");
        if (!hasSettings && !hasVideos)
        {
            return;
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

internal sealed class SqlitePresenterSettingsService(IDbContextFactory<PresenterDbContext> contextFactory) : IPresenterSettingsService
{
    private const int PasswordIterations = 600_000;

    public async Task<PresenterSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
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

    public async Task SaveAsync(PresenterSettings settings, CancellationToken cancellationToken = default)
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
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task SetWebPasswordAsync(string password, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        var settings = await GetAsync(cancellationToken);
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, PasswordIterations, HashAlgorithmName.SHA512, 32);
        settings.PasswordSalt = Convert.ToBase64String(salt);
        settings.PasswordHash = Convert.ToBase64String(hash);
        await SavePasswordAsync(settings, cancellationToken);
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

    private async Task SavePasswordAsync(PresenterSettings settings, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await context.Settings.SingleAsync(x => x.Id == 1, cancellationToken);
        existing.PasswordHash = settings.PasswordHash;
        existing.PasswordSalt = settings.PasswordSalt;
        await context.SaveChangesAsync(cancellationToken);
    }
}

internal sealed class SqliteMediaLibraryService(
    IDbContextFactory<PresenterDbContext> contextFactory,
    IPresenterSettingsService settingsService) : IMediaLibraryService
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".webm", ".avi", ".mov", ".m4v"
    };

    public async Task<IReadOnlyList<VideoAsset>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var videos = await context.Videos.AsNoTracking().ToListAsync(cancellationToken);
        return videos.OrderByDescending(x => x.AddedAtUtc).ToList();
    }

    public async Task<VideoAsset> AddUploadAsync(string originalFileName, Stream content, long length, CancellationToken cancellationToken = default)
    {
        var safeName = Path.GetFileName(originalFileName);
        var extension = Path.GetExtension(safeName);
        if (string.IsNullOrWhiteSpace(safeName) || !AllowedExtensions.Contains(extension))
        {
            throw new InvalidOperationException("Dieses Videoformat wird nicht unterstützt.");
        }

        var settings = await settingsService.GetAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.MediaFolder))
        {
            throw new InvalidOperationException("Bitte zuerst einen Videoordner in der Desktop-App festlegen.");
        }

        Directory.CreateDirectory(settings.MediaFolder);
        var destinationPath = MakeUniquePath(settings.MediaFolder, safeName);
        await using (var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true))
        {
            await content.CopyToAsync(destination, cancellationToken);
        }

        var asset = new VideoAsset { FileName = Path.GetFileName(destinationPath), FullPath = destinationPath, FileSize = length, AddedAtUtc = DateTimeOffset.UtcNow };
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.Videos.Add(asset);
        await context.SaveChangesAsync(cancellationToken);
        return asset;
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
