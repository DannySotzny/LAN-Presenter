using BeamerPresenter.Application;
using BeamerPresenter.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeamerPresenter.Infrastructure.Tests;

public sealed class MediaFolderServiceTests
{
    [Fact]
    public async Task Media_folders_are_persisted_updated_and_removed()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "BeamerPresenter.Tests", Guid.NewGuid().ToString("N"));
        var dataDirectory = Path.Combine(testRoot, "Data");
        var mediaDirectory = Path.Combine(testRoot, "Media");
        Directory.CreateDirectory(mediaDirectory);
        try
        {
            PresenterDatabase.GetConfiguredWebPort(dataDirectory);
            var services = new ServiceCollection();
            services.AddPresenterInfrastructure(dataDirectory);
            await using var provider = services.BuildServiceProvider();
            var folders = provider.GetRequiredService<IMediaFolderService>();

            var created = await folders.AddAsync(mediaDirectory, includeSubdirectories: true);
            var updated = await folders.AddAsync(mediaDirectory.ToUpperInvariant(), includeSubdirectories: false);

            Assert.Equal(created.Id, updated.Id);
            Assert.False(updated.IncludeSubdirectories);
            Assert.Single(await folders.GetAllAsync());

            await folders.RemoveAsync(created.Id);
            Assert.Empty(await folders.GetAllAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }
}
