using System.Net;
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

    [Fact]
    public void Windows_monitor_service_returns_unique_valid_displays()
    {
        var monitors = new WindowsMonitorService().GetAll();

        Assert.NotEmpty(monitors);
        Assert.Equal(monitors.Count, monitors.Select(monitor => monitor.DeviceName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(monitors, monitor =>
        {
            Assert.False(string.IsNullOrWhiteSpace(monitor.DeviceName));
            Assert.False(string.IsNullOrWhiteSpace(monitor.FriendlyName));
            Assert.True(monitor.Width > 0);
            Assert.True(monitor.Height > 0);
        });
    }

    [Fact]
    public void Build_information_comes_from_the_built_app_assembly()
    {
        var information = BuildInformation.FromAssembly(typeof(BuildInformation).Assembly);

        Assert.Matches(@"^\d+\.\d+\.\d+$", information.Version);
        Assert.False(string.IsNullOrWhiteSpace(information.InformationalVersion));
        Assert.NotNull(information.BuildTimestampUtc);
        Assert.False(string.IsNullOrWhiteSpace(information.GitCommitSha));
        Assert.Contains(".NET", information.RuntimeVersion, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1234567890abcdef", "12345678")]
    [InlineData("abc123", "abc123")]
    public void Build_information_shortens_only_long_commit_identifiers(string commit, string expected)
    {
        var information = new BuildInformation("1.0.0", "1.0.0", null, commit, ".NET");

        Assert.Equal(expected, information.ShortGitCommitSha);
    }

    [Fact]
    public void Web_address_display_distinguishes_loopback_and_lan_urls()
    {
        Assert.Equal(
            "http://localhost:8765 (nur lokal)",
            PresenterNetworkAddresses.FormatWebUrls(8765, allowLanAccess: false, [IPAddress.Parse("192.168.1.42")]));

        var display = PresenterNetworkAddresses.FormatWebUrls(
            9123,
            allowLanAccess: true,
            [IPAddress.Loopback, IPAddress.Parse("192.168.1.42"), IPAddress.Parse("10.0.0.5")]);

        Assert.Contains("Lokal: http://localhost:9123", display, StringComparison.Ordinal);
        Assert.Contains("http://192.168.1.42:9123", display, StringComparison.Ordinal);
        Assert.Contains("http://10.0.0.5:9123", display, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", display, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Power_management_release_and_dispose_are_idempotent_without_active_requests()
    {
        var service = new WindowsPowerManagementService();

        await service.ReleaseAsync();
        await service.ReleaseAsync();
        service.Dispose();
        service.Dispose();
    }

    [Fact]
    public async Task Power_management_can_apply_and_release_native_requests()
    {
        using var service = new WindowsPowerManagementService();

        await service.ApplyAsync(preventDisplaySleep: true, preventSystemSleep: true);
        await service.ReleaseAsync();
    }

    [Fact]
    public async Task Power_management_honors_cancellation_before_native_calls()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        using var service = new WindowsPowerManagementService();

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.ApplyAsync(true, true, cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.ReleaseAsync(cancellation.Token));
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
