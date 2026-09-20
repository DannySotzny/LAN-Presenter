using System.Text.Json;
using BeamerPresenter.App;

namespace BeamerPresenter.App.Tests;

public sealed class PresenterFoundationTests
{
    [Fact]
    public void Presenter_paths_create_the_expected_local_directories()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var paths = PresenterPaths.Create(testRoot);

            Assert.All(
                new[] { paths.DataDirectory, paths.LogsDirectory, paths.BackupDirectory, paths.ChromeProfileDirectory, paths.ToolsDirectory },
                path => Assert.True(Directory.Exists(path), $"Expected directory '{path}' to exist."));
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public void File_logger_writes_structured_properties()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var paths = PresenterPaths.Create(testRoot);
            using (var logger = PresenterLogging.CreateLogger(paths.LogsDirectory))
            {
                logger.Information("Started milestone {Milestone}", "logging");
            }

            var logFile = Assert.Single(Directory.GetFiles(paths.LogsDirectory, "presenter-*.jsonl"));
            using var logEvent = JsonDocument.Parse(Assert.Single(File.ReadLines(logFile)));
            Assert.Contains("Started milestone", logEvent.RootElement.GetProperty("RenderedMessage").GetString(), StringComparison.Ordinal);
            Assert.Equal("logging", logEvent.RootElement.GetProperty("Properties").GetProperty("Milestone").GetString());
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    private static string CreateTestRoot() => Path.Combine(Path.GetTempPath(), "BeamerPresenter.Tests", Guid.NewGuid().ToString("N"));

    private static void DeleteTestRoot(string testRoot)
    {
        var allowedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BeamerPresenter.Tests"));
        var resolvedTestRoot = Path.GetFullPath(testRoot);
        if (resolvedTestRoot.StartsWith(allowedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolvedTestRoot))
        {
            Directory.Delete(resolvedTestRoot, recursive: true);
        }
    }
}
