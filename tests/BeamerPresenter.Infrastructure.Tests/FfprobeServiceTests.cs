using BeamerPresenter.Application;
using BeamerPresenter.Domain;
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

            runner.FailingVersionChecks.Add(configuredFfprobe);
            var fallback = await provider.GetRequiredService<IFfprobeService>().CheckAvailabilityAsync();
            Assert.True(fallback.IsAvailable);
            Assert.Equal(localFfprobe, fallback.ExecutablePath);

            runner.EmptyVersionOutputPaths.Add(localFfprobe);
            var emptyVersion = await provider.GetRequiredService<IFfprobeService>().CheckAvailabilityAsync();
            Assert.Equal("ffprobe", emptyVersion.Version);

            runner.ThrowOnVersionChecks.Add(configuredFfprobe);
            var afterBrokenConfiguredTool = await provider.GetRequiredService<IFfprobeService>().CheckAvailabilityAsync();
            Assert.Equal(localFfprobe, afterBrokenConfiguredTool.ExecutablePath);
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

    [Fact]
    public async Task Probe_reads_media_metadata_and_reports_browser_compatibility()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "BeamerPresenter.Tests", Guid.NewGuid().ToString("N"));
        var dataDirectory = Path.Combine(testRoot, "Data");
        var toolsDirectory = Path.Combine(testRoot, "Tools");
        var configuredFfprobe = Path.Combine(testRoot, "Configured", "ffprobe.exe");
        var mediaPath = Path.Combine(testRoot, "Videos", "intro.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(configuredFfprobe)!);
        Directory.CreateDirectory(Path.GetDirectoryName(mediaPath)!);
        await File.WriteAllTextAsync(configuredFfprobe, string.Empty);
        await File.WriteAllTextAsync(mediaPath, string.Empty);

        try
        {
            PresenterDatabase.GetConfiguredWebPort(dataDirectory);
            var runner = new FakeProcessRunner(configuredFfprobe, Path.Combine(toolsDirectory, "ffprobe.exe"));
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddPresenterInfrastructure(dataDirectory, toolsDirectory);
            services.AddSingleton<IExternalProcessRunner>(runner);
            await using var provider = services.BuildServiceProvider();
            var settingsService = provider.GetRequiredService<IPresenterSettingsService>();
            var settings = await settingsService.GetAsync();
            settings.FfprobePath = configuredFfprobe;
            await settingsService.SaveAsync(settings);

            var ffprobe = provider.GetRequiredService<IFfprobeService>();
            var result = await ffprobe.ProbeAsync(mediaPath);

            Assert.Equal(MediaProbeStatus.Valid, result.ProbeStatus);
            Assert.Equal(MediaPlaybackStatus.Supported, result.PlaybackStatus);
            Assert.Equal(TimeSpan.FromSeconds(65.5), result.Duration);
            Assert.Equal("mov,mp4,m4a,3gp,3g2,mj2", result.Container);
            Assert.Equal("h264", result.VideoCodec);
            Assert.Equal(1920, result.VideoWidth);
            Assert.Equal(1080, result.VideoHeight);
            Assert.Equal(29.970, result.FrameRate!.Value, precision: 3);
            Assert.Equal("aac", result.AudioCodec);
            Assert.Equal(2, result.AudioChannels);
            Assert.Null(result.Error);

            runner.ProbeResult = new ProcessExecutionResult(0, """
                {
                  "format": { "duration": "not-a-number" },
                  "streams": [
                    { "codec_type": "video", "codec_name": "mpeg4", "height": 720, "r_frame_rate": "30/0" },
                    { "codec_type": "audio", "codec_name": "ac3" },
                    { "codec_type": "video", "codec_name": "h264" }
                  ]
                }
                """, string.Empty);
            var unsupported = await ffprobe.ProbeAsync(mediaPath);
            Assert.Equal(MediaProbeStatus.Valid, unsupported.ProbeStatus);
            Assert.Equal(MediaPlaybackStatus.Unsupported, unsupported.PlaybackStatus);
            Assert.Null(unsupported.Duration);
            Assert.Null(unsupported.Container);
            Assert.Null(unsupported.VideoWidth);
            Assert.Equal(720, unsupported.VideoHeight);
            Assert.Null(unsupported.FrameRate);
            Assert.Equal("ac3", unsupported.AudioCodec);
            Assert.Null(unsupported.AudioChannels);

            runner.ProbeResult = new ProcessExecutionResult(0, """
                {
                  "format": { "duration": "4.25", "format_name": "mp4" },
                  "streams": [
                    { "codec_type": "video", "codec_name": "h264", "r_frame_rate": "30" }
                  ]
                }
                """, string.Empty);
            var integerFrameRate = await ffprobe.ProbeAsync(mediaPath);
            Assert.Equal(30, integerFrameRate.FrameRate);
            Assert.Equal(MediaPlaybackStatus.Supported, integerFrameRate.PlaybackStatus);

            runner.ProbeResult = new ProcessExecutionResult(0, """
                { "streams": [ { "codec_type": "video", "codec_name": "h264", "r_frame_rate": "invalid" } ] }
                """, string.Empty);
            var invalidFrameRate = await ffprobe.ProbeAsync(mediaPath);
            Assert.Null(invalidFrameRate.FrameRate);

            runner.ProbeResult = new ProcessExecutionResult(0, """
                { "format": { "duration": "4.25" }, "streams": [] }
                """, string.Empty);
            var audioVideoMissing = await ffprobe.ProbeAsync(mediaPath);
            Assert.Equal(TimeSpan.FromSeconds(4.25), audioVideoMissing.Duration);
            Assert.Null(audioVideoMissing.VideoCodec);
            Assert.Null(audioVideoMissing.AudioCodec);
            Assert.Equal(MediaPlaybackStatus.Unsupported, audioVideoMissing.PlaybackStatus);

            runner.ProbeResult = new ProcessExecutionResult(7, string.Empty, new string('x', 5000));
            var invalid = await ffprobe.ProbeAsync(mediaPath);
            Assert.Equal(MediaProbeStatus.Invalid, invalid.ProbeStatus);
            Assert.Equal(4096, invalid.Error!.Length);

            runner.ProbeException = new IOException("probe process failed");
            var failed = await ffprobe.ProbeAsync(mediaPath);
            Assert.Equal(MediaProbeStatus.Invalid, failed.ProbeStatus);
            Assert.Equal("probe process failed", failed.Error);

            var missing = await ffprobe.ProbeAsync(Path.Combine(testRoot, "missing.mp4"));
            Assert.Equal(MediaProbeStatus.Missing, missing.ProbeStatus);

            runner.ProbeException = null;
            runner.VersionExitCode = 1;
            var unavailable = await ffprobe.ProbeAsync(mediaPath);
            Assert.Equal(MediaProbeStatus.Unknown, unavailable.ProbeStatus);
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
        public int VersionExitCode { get; set; }
        public HashSet<string> FailingVersionChecks { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ThrowOnVersionChecks { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> EmptyVersionOutputPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Exception? ProbeException { get; set; }
        public ProcessExecutionResult ProbeResult { get; set; } = new(0, """
            {
              "streams": [
                { "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080, "r_frame_rate": "30000/1001" },
                { "codec_type": "audio", "codec_name": "aac", "channels": 2 }
              ],
              "format": { "format_name": "mov,mp4,m4a,3gp,3g2,mj2", "duration": "65.500000" }
            }
            """, string.Empty);

        public Task<ProcessExecutionResult> RunAsync(string executablePath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            ExecutedPaths.Add(executablePath);
            if (arguments.Contains("-show_entries", StringComparer.Ordinal))
            {
                if (ProbeException is not null) throw ProbeException;
                return Task.FromResult(ProbeResult);
            }

            if (arguments.Contains("-version", StringComparer.Ordinal))
            {
                if (ThrowOnVersionChecks.Contains(executablePath)) throw new IOException("candidate could not be started");
                var version = EmptyVersionOutputPaths.Contains(executablePath)
                    ? string.Empty
                    : string.Equals(executablePath, configuredFfprobe, StringComparison.OrdinalIgnoreCase)
                        ? "ffprobe version test\n"
                        : "ffprobe version local\n";
                var exitCode = FailingVersionChecks.Contains(executablePath) ? 1 : VersionExitCode;
                return Task.FromResult(new ProcessExecutionResult(exitCode, version, string.Empty));
            }

            var result = string.Equals(executablePath, configuredFfprobe, StringComparison.OrdinalIgnoreCase)
                ? new ProcessExecutionResult(VersionExitCode, "ffprobe version test\n", string.Empty)
                : string.Equals(executablePath, localFfprobe, StringComparison.OrdinalIgnoreCase)
                    ? new ProcessExecutionResult(0, "ffprobe version local\n", string.Empty)
                    : new ProcessExecutionResult(1, string.Empty, "not available");
            return Task.FromResult(result);
        }
    }
}
