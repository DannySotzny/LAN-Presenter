using BeamerPresenter.Application;
using BeamerPresenter.Domain;
using BeamerPresenter.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace BeamerPresenter.Infrastructure.Tests;

public sealed class PresenterDatabaseMigrationTests
{
    [Fact]
    public async Task Fresh_database_is_created_from_versioned_migrations()
    {
        var dataDirectory = CreateTestDirectory();
        try
        {
            Assert.Equal(PresenterSettings.DefaultWebPort, PresenterDatabase.GetConfiguredWebPort(dataDirectory));
            Assert.False(PresenterDatabase.GetConfiguredHostSettings(dataDirectory).AllowLanAccess);

            await using var connection = new SqliteConnection(PresenterDatabase.CreateConnectionString(dataDirectory));
            await connection.OpenAsync();
            Assert.EndsWith("_AddYouTubeDownloadSource", await ReadAppliedMigrationAsync(connection), StringComparison.Ordinal);
            Assert.Equal("YouTubeSourceKey", await ReadScalarAsync(connection,
                "SELECT name FROM pragma_table_info('Videos') WHERE name = 'YouTubeSourceKey';"));
            Assert.Equal("wal", await ReadScalarAsync(connection, "PRAGMA journal_mode;"));
            Assert.Equal("1", await ReadScalarAsync(connection, "PRAGMA foreign_keys;"));
        }
        finally
        {
            DeleteTestDirectory(dataDirectory);
        }
    }

    [Fact]
    public async Task Existing_ensure_created_database_is_baselined_without_data_loss()
    {
        var dataDirectory = CreateTestDirectory();
        try
        {
            var options = new DbContextOptionsBuilder<PresenterDbContext>()
                .UseSqlite(PresenterDatabase.CreateConnectionString(dataDirectory))
                .Options;
            await using (var context = new PresenterDbContext(options))
            {
                await context.Database.GetService<IMigrator>().MigrateAsync("20260920165033_InitialSchema");
                await context.Database.ExecuteSqlRawAsync("""
                    INSERT INTO Settings (Id, WebPort, AllowLanAccess, MediaFolder, PasswordHash, PasswordSalt)
                    VALUES (1, 9123, 1, 'D:\LAN\Videos', NULL, NULL);
                    """);
                await context.Database.ExecuteSqlRawAsync("""
                    INSERT INTO Videos (Id, FileName, FullPath, FileSize, AddedAtUtc)
                    VALUES (1, 'legacy.mp4', 'D:\LAN\Videos\legacy.mp4', 42, '2026-09-20 18:00:00+00:00');
                    """);
                await context.Database.ExecuteSqlRawAsync("DROP TABLE __EFMigrationsHistory;");
            }

            Assert.Equal(9123, PresenterDatabase.GetConfiguredWebPort(dataDirectory));
            Assert.True(PresenterDatabase.GetConfiguredHostSettings(dataDirectory).AllowLanAccess);

            await using var connection = new SqliteConnection(PresenterDatabase.CreateConnectionString(dataDirectory));
            await connection.OpenAsync();
            Assert.EndsWith("_AddYouTubeDownloadSource", await ReadAppliedMigrationAsync(connection), StringComparison.Ordinal);
            Assert.Equal("YouTubeSourceKey", await ReadScalarAsync(connection,
                "SELECT name FROM pragma_table_info('Videos') WHERE name = 'YouTubeSourceKey';"));
            Assert.Equal("D:\\LAN\\Videos", await ReadScalarAsync(connection, "SELECT MediaFolder FROM Settings WHERE Id = 1;"));
            Assert.Equal("D:\\LAN\\Videos", await ReadScalarAsync(connection, "SELECT Path FROM MediaFolders LIMIT 1;"));
            Assert.Equal("1", await ReadScalarAsync(connection, "SELECT AlwaysOnTop FROM Settings WHERE Id = 1;"));
            Assert.Equal("1", await ReadScalarAsync(connection, "SELECT PreventDisplaySleep FROM Settings WHERE Id = 1;"));
            Assert.Equal("1", await ReadScalarAsync(connection, "SELECT PreventSystemSleep FROM Settings WHERE Id = 1;"));
            Assert.Equal("1", await ReadScalarAsync(connection, "SELECT Enabled FROM Videos WHERE Id = 1;"));
            Assert.Single(Directory.GetFiles(Path.Combine(Directory.GetParent(dataDirectory)!.FullName, "Backup"), "presenter-before-migration-*.db"));
        }
        finally
        {
            DeleteTestDirectory(dataDirectory);
        }
    }

    [Fact]
    public async Task Presenter_display_settings_are_persisted()
    {
        var dataDirectory = CreateTestDirectory();
        try
        {
            PresenterDatabase.GetConfiguredWebPort(dataDirectory);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddPresenterInfrastructure(dataDirectory);
            await using var provider = services.BuildServiceProvider();
            var settingsService = provider.GetRequiredService<IPresenterSettingsService>();
            var settings = await settingsService.GetAsync();
            settings.ChromePath = "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe";
            settings.AllowLanAccess = true;
            settings.MonitorDeviceName = "\\\\.\\DISPLAY2";
            settings.AlwaysOnTop = false;
            settings.AggressiveTopmost = true;
            settings.PreventDisplaySleep = false;
            settings.PreventSystemSleep = true;
            settings.ShortVideoThresholdSeconds = 540;
            settings.ClipLengthMinSeconds = 360;
            settings.ClipLengthMaxSeconds = 720;
            settings.VideoCooldownCount = 8;
            settings.TimeCooldownMinutes = 45;
            settings.QueueTargetLength = 12;

            await settingsService.SaveAsync(settings);
            var persisted = await settingsService.GetAsync();

            Assert.Equal(settings.ChromePath, persisted.ChromePath);
            Assert.True(persisted.AllowLanAccess);
            Assert.Equal(settings.MonitorDeviceName, persisted.MonitorDeviceName);
            Assert.False(persisted.AlwaysOnTop);
            Assert.True(persisted.AggressiveTopmost);
            Assert.False(persisted.PreventDisplaySleep);
            Assert.True(persisted.PreventSystemSleep);
            Assert.Equal(540, persisted.ShortVideoThresholdSeconds);
            Assert.Equal(360, persisted.ClipLengthMinSeconds);
            Assert.Equal(720, persisted.ClipLengthMaxSeconds);
            Assert.Equal(8, persisted.VideoCooldownCount);
            Assert.Equal(45, persisted.TimeCooldownMinutes);
            Assert.Equal(12, persisted.QueueTargetLength);
        }
        finally
        {
            DeleteTestDirectory(dataDirectory);
        }
    }

    [Fact]
    public async Task Concurrent_initial_settings_reads_create_only_one_row()
    {
        var dataDirectory = CreateTestDirectory();
        try
        {
            PresenterDatabase.GetConfiguredWebPort(dataDirectory);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddPresenterInfrastructure(dataDirectory);
            await using var provider = services.BuildServiceProvider();
            var settingsService = provider.GetRequiredService<IPresenterSettingsService>();

            var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => settingsService.GetAsync()));

            Assert.All(results, settings => Assert.Equal(1, settings.Id));
            var factory = provider.GetRequiredService<IDbContextFactory<PresenterDbContext>>();
            await using var context = await factory.CreateDbContextAsync();
            Assert.Equal(1, await context.Settings.CountAsync());
        }
        finally
        {
            DeleteTestDirectory(dataDirectory);
        }
    }

    [Fact]
    public async Task Daily_backup_is_consistent_idempotent_and_keeps_seven_days()
    {
        var dataDirectory = CreateTestDirectory();
        try
        {
            PresenterDatabase.GetConfiguredWebPort(dataDirectory);
            var options = new DbContextOptionsBuilder<PresenterDbContext>()
                .UseSqlite(PresenterDatabase.CreateConnectionString(dataDirectory))
                .Options;
            await using (var context = new PresenterDbContext(options))
            {
                context.Settings.Add(new PresenterSettings { WebPort = 9876, MediaFolder = "D:\\LAN\\BackupTest" });
                await context.SaveChangesAsync();
            }

            var firstDay = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
            var firstPath = PresenterDatabase.CreateDailyBackup(dataDirectory, firstDay);
            Assert.Equal(firstPath, PresenterDatabase.CreateDailyBackup(dataDirectory, firstDay.AddHours(6)));

            await using (var backupConnection = new SqliteConnection(
                             new SqliteConnectionStringBuilder { DataSource = firstPath, Pooling = false }.ToString()))
            {
                await backupConnection.OpenAsync();
                Assert.Equal("9876", await ReadScalarAsync(backupConnection, "SELECT WebPort FROM Settings WHERE Id = 1;"));
                Assert.Equal("ok", await ReadScalarAsync(backupConnection, "PRAGMA integrity_check;"));
            }

            for (var day = 1; day < 8; day++)
            {
                PresenterDatabase.CreateDailyBackup(dataDirectory, firstDay.AddDays(day));
            }

            var backups = Directory.GetFiles(
                Path.Combine(Directory.GetParent(dataDirectory)!.FullName, "Backup"),
                "presenter-daily-*.db");
            Assert.Equal(7, backups.Length);
            Assert.DoesNotContain(firstPath, backups);
            Assert.DoesNotContain(Directory.GetFiles(Path.GetDirectoryName(firstPath)!), path => path.EndsWith(".tmp", StringComparison.Ordinal));
        }
        finally
        {
            DeleteTestDirectory(dataDirectory);
        }
    }

    private static async Task<string> ReadAppliedMigrationAsync(SqliteConnection connection) =>
        await ReadScalarAsync(connection, "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1;");

    private static async Task<string> ReadScalarAsync(SqliteConnection connection, string commandText)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static string CreateTestDirectory()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "BeamerPresenter.Tests", Guid.NewGuid().ToString("N"));
        var dataDirectory = Path.Combine(testRoot, "Data");
        Directory.CreateDirectory(dataDirectory);
        return dataDirectory;
    }

    private static void DeleteTestDirectory(string dataDirectory)
    {
        SqliteConnection.ClearAllPools();
        var allowedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BeamerPresenter.Tests"));
        var resolvedDirectory = Path.GetFullPath(dataDirectory);
        var testRoot = Directory.GetParent(resolvedDirectory)?.FullName;
        if (testRoot is not null && testRoot.StartsWith(allowedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(testRoot))
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }
}
