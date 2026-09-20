using BeamerPresenter.Domain;
using BeamerPresenter.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

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

            await using var connection = new SqliteConnection(PresenterDatabase.CreateConnectionString(dataDirectory));
            await connection.OpenAsync();
            Assert.Equal("20260920165033_InitialSchema", await ReadAppliedMigrationAsync(connection));
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
                await context.Database.EnsureCreatedAsync();
                context.Settings.Add(new PresenterSettings { WebPort = 9123, MediaFolder = "D:\\LAN\\Videos" });
                await context.SaveChangesAsync();
            }

            Assert.Equal(9123, PresenterDatabase.GetConfiguredWebPort(dataDirectory));

            await using var connection = new SqliteConnection(PresenterDatabase.CreateConnectionString(dataDirectory));
            await connection.OpenAsync();
            Assert.Equal("20260920165033_InitialSchema", await ReadAppliedMigrationAsync(connection));
            Assert.Equal("D:\\LAN\\Videos", await ReadScalarAsync(connection, "SELECT MediaFolder FROM Settings WHERE Id = 1;"));
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
        var directory = Path.Combine(Path.GetTempPath(), "BeamerPresenter.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTestDirectory(string dataDirectory)
    {
        SqliteConnection.ClearAllPools();
        var allowedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BeamerPresenter.Tests"));
        var resolvedDirectory = Path.GetFullPath(dataDirectory);
        if (resolvedDirectory.StartsWith(allowedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolvedDirectory))
        {
            Directory.Delete(resolvedDirectory, recursive: true);
        }
    }
}
