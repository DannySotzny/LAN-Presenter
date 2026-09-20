using BeamerPresenter.Application;
using BeamerPresenter.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeamerPresenter.Infrastructure.Tests;

public sealed class FfprobeServiceTests
{
    [Fact]
    public async Task Availability_uses_configured_path_before_local_tools_fallback()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "BeamerPresenter.Tests", Guid.NewGuid().ToString("N"));
        var dataDirectory = Path.Combine(testRoot, "Data");
        var toolsDirectory = Path.Combine(testRoot, "Tools");
        var configuredFfprobe = Path.Combine(testRoot, "Configured", "ffprobe.exe");
        var localFfprobe = Path.Combine(toolsDirectory, "ffprobe.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(configuredFfprobe)!);
        Directory.CreateDirectory(toolsDirectory);
        await File.WriteAllTextAsync(configuredFfprobe, string.Empty);
        await File.WriteAllTextAsync(localFfprobe, string.Empty);

        try
        {
            PresenterDatabase.GetConfiguredWebPort(dataDirectory);
            var runner = new FakeProcessRunner(configuredFfprobe, localFfprobe);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddPresenterInfrastructure(dataDirectory, toolsDirectory);
            services.AddSingleton<IExternalProcessRunner>(runner);
            await using var provider = services.BuildServiceProvider();
            var settingsService = provider.GetRequiredService<IPresenterSettingsService>();
            var settings = await settingsService.GetAsync();
            settings.FfprobePath = configuredFfprobe;
            await settingsService.SaveAsync(settings);

            var availability = await provider.GetRequiredService<IFfprobeService>().CheckAvailabilityAsync();

            Assert.True(availability.IsAvailable);
            Assert.Equal(configuredFfprobe, availability.ExecutablePath);
            Assert.Equal("ffprobe version test", availability.Version);
            Assert.Equal([configuredFfprobe], runner.ExecutedPaths);
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

    private sealed class FakeProcessRunner(string configuredFfprobe, string localFfprobe) : IExternalProcessRunner
    {
        public List<string> ExecutedPaths { get; } = [];

        public Task<ProcessExecutionResult> RunAsync(string executablePath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            ExecutedPaths.Add(executablePath);
            var result = string.Equals(executablePath, configuredFfprobe, StringComparison.OrdinalIgnoreCase)
                ? new ProcessExecutionResult(0, "ffprobe version test\n", string.Empty)
                : string.Equals(executablePath, localFfprobe, StringComparison.OrdinalIgnoreCase)
                    ? new ProcessExecutionResult(0, "ffprobe version local\n", string.Empty)
                    : new ProcessExecutionResult(1, string.Empty, "not available");
            return Task.FromResult(result);
        }
    }
}
