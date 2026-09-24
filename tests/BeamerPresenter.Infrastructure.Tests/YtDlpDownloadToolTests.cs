using System.Text;
using BeamerPresenter.Application;
using BeamerPresenter.Infrastructure;

namespace BeamerPresenter.Infrastructure.Tests;

public sealed class YtDlpDownloadToolTests
{
    private const string VideoId = "M7lc1UVf-VE";

    [Fact]
    public async Task Download_runs_with_restricted_arguments_and_returns_a_nonempty_mp4()
    {
        var root = Path.Combine(Path.GetTempPath(), "PresenterYtDlpTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var executable = Path.Combine(root, "yt-dlp.exe");
            File.WriteAllText(executable, "test");
            var process = new RecordingDownloadProcess();
            var launcher = new RecordingDownloadProcessLauncher(arguments =>
            {
                var staging = arguments[Array.IndexOf(arguments.ToArray(), "--paths") + 1];
                File.WriteAllBytes(Path.Combine(staging, $"YouTube-{VideoId}.mp4"), [1, 2, 3]);
                return process;
            });
            var tool = new YtDlpDownloadTool(new RecordingRunner(executable), root, [executable], launcher);
            var phases = new List<YouTubeDownloadPhase>();

            var downloaded = await tool.DownloadAsync(VideoId, Path.Combine(root, "staging"), phases.Add, CancellationToken.None);

            Assert.Equal([YouTubeDownloadPhase.Downloading], phases);
            Assert.Equal(Path.Combine(root, "staging", $"YouTube-{VideoId}.mp4"), downloaded);
            Assert.Equal(executable, launcher.Executable);
            Assert.Contains("--ignore-config", launcher.Arguments);
            Assert.Contains("--no-playlist", launcher.Arguments);
            Assert.Equal($"https://www.youtube.com/watch?v={VideoId}", launcher.Arguments[^1]);
            Assert.True(process.Disposed);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Download_failure_and_missing_or_empty_outputs_are_rejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "PresenterYtDlpTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var executable = Path.Combine(root, "yt-dlp.exe");
            File.WriteAllText(executable, "test");

            var failedProcess = new RecordingDownloadProcess(exitCode: 42);
            var failedTool = new YtDlpDownloadTool(new RecordingRunner(executable), root, [executable],
                new RecordingDownloadProcessLauncher(_ => failedProcess));
            var failed = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                failedTool.DownloadAsync(VideoId, Path.Combine(root, "failed"), _ => { }, CancellationToken.None));
            Assert.Contains("Code 42", failed.Message);

            var missingTool = new YtDlpDownloadTool(new RecordingRunner(executable), root, [executable],
                new RecordingDownloadProcessLauncher(_ => new RecordingDownloadProcess()));
            var missing = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                missingTool.DownloadAsync(VideoId, Path.Combine(root, "missing"), _ => { }, CancellationToken.None));
            Assert.Contains("keine MP4-Datei", missing.Message);

            var duplicateLauncher = new RecordingDownloadProcessLauncher(arguments =>
            {
                var staging = arguments[Array.IndexOf(arguments.ToArray(), "--paths") + 1];
                File.WriteAllBytes(Path.Combine(staging, "first.mp4"), [1]);
                File.WriteAllBytes(Path.Combine(staging, "second.mp4"), [2]);
                return new RecordingDownloadProcess();
            });
            var duplicateTool = new YtDlpDownloadTool(new RecordingRunner(executable), root, [executable], duplicateLauncher);
            var duplicate = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                duplicateTool.DownloadAsync(VideoId, Path.Combine(root, "duplicate"), _ => { }, CancellationToken.None));
            Assert.Contains("mehrere MP4-Dateien", duplicate.Message);

            var emptyLauncher = new RecordingDownloadProcessLauncher(arguments =>
            {
                var staging = arguments[Array.IndexOf(arguments.ToArray(), "--paths") + 1];
                File.WriteAllBytes(Path.Combine(staging, "empty.mp4"), []);
                return new RecordingDownloadProcess();
            });
            var emptyTool = new YtDlpDownloadTool(new RecordingRunner(executable), root, [executable], emptyLauncher);
            var empty = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                emptyTool.DownloadAsync(VideoId, Path.Combine(root, "empty"), _ => { }, CancellationToken.None));
            Assert.Contains("leer", empty.Message);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Cancelling_download_kills_the_process_and_propagates_cancellation()
    {
        var root = Path.Combine(Path.GetTempPath(), "PresenterYtDlpTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var executable = Path.Combine(root, "yt-dlp.exe");
            File.WriteAllText(executable, "test");
            var process = new RecordingDownloadProcess(blockUntilCancelled: true);
            var launcher = new RecordingDownloadProcessLauncher(_ => process);
            var tool = new YtDlpDownloadTool(new RecordingRunner(executable), root, [executable], launcher);
            using var cancellation = new CancellationTokenSource();
            var download = tool.DownloadAsync(VideoId, Path.Combine(root, "staging"), _ => { }, cancellation.Token);

            await launcher.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
            Assert.True(process.KillCalled);
            Assert.True(process.Disposed);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Download_timeout_kills_the_process_and_reports_a_timeout()
    {
        var root = Path.Combine(Path.GetTempPath(), "PresenterYtDlpTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var executable = Path.Combine(root, "yt-dlp.exe");
            File.WriteAllText(executable, "test");
            var process = new RecordingDownloadProcess(blockUntilCancelled: true);
            var launcher = new RecordingDownloadProcessLauncher(_ => process);
            var tool = new YtDlpDownloadTool(new RecordingRunner(executable), root, [executable], launcher,
                TimeSpan.FromMilliseconds(25));

            var error = await Assert.ThrowsAsync<TimeoutException>(() =>
                tool.DownloadAsync(VideoId, Path.Combine(root, "staging"), _ => { }, CancellationToken.None));

            Assert.Contains("Zeitlimit", error.Message);
            Assert.True(process.KillCalled);
            Assert.True(process.Disposed);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Download_size_limit_stops_the_process_before_an_oversized_file_is_published()
    {
        var root = Path.Combine(Path.GetTempPath(), "PresenterYtDlpTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var executable = Path.Combine(root, "yt-dlp.exe");
            File.WriteAllText(executable, "test");
            var process = new RecordingDownloadProcess(blockUntilCancelled: true);
            var launcher = new RecordingDownloadProcessLauncher(_ => process);
            var tool = new YtDlpDownloadTool(new RecordingRunner(executable), root, [executable], launcher,
                getStagingDirectorySize: _ => YouTubeDownloadLimits.MaximumBytes + 1);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                tool.DownloadAsync(VideoId, Path.Combine(root, "staging"), _ => { }, CancellationToken.None));

            Assert.Contains("5 GB", error.Message);
            Assert.True(process.KillCalled);
            Assert.True(process.Disposed);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Download_size_monitor_retries_transient_file_access_failures()
    {
        var root = Path.Combine(Path.GetTempPath(), "PresenterYtDlpTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var executable = Path.Combine(root, "yt-dlp.exe");
            File.WriteAllText(executable, "test");
            var process = new RecordingDownloadProcess(blockUntilCancelled: true);
            var launcher = new RecordingDownloadProcessLauncher(arguments =>
            {
                var staging = arguments[Array.IndexOf(arguments.ToArray(), "--paths") + 1];
                File.WriteAllBytes(Path.Combine(staging, $"YouTube-{VideoId}.mp4"), [1, 2, 3]);
                return process;
            });
            var checks = 0;
            long GetStagingSize(string _)
            {
                switch (Interlocked.Increment(ref checks))
                {
                    case 1: throw new IOException("File is being moved.");
                    case 2: throw new UnauthorizedAccessException("Folder is temporarily locked.");
                    default:
                        process.Complete();
                        return 3;
                }
            }
            var tool = new YtDlpDownloadTool(new RecordingRunner(executable), root, [executable], launcher,
                getStagingDirectorySize: GetStagingSize);

            var downloaded = await tool.DownloadAsync(VideoId, Path.Combine(root, "staging"), _ => { }, CancellationToken.None);

            Assert.True(File.Exists(downloaded));
            Assert.Equal(3, Volatile.Read(ref checks));
            Assert.False(process.KillCalled);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task System_process_launcher_preserves_argument_boundaries_and_waits_for_exit()
    {
        var executable = Environment.GetEnvironmentVariable("COMSPEC")
            ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var launcher = new YtDlpDownloadProcessLauncher();

        using var process = launcher.Start(executable, ["/d", "/c", "echo", "yt-dlp argument boundary"]);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(CancellationToken.None);

        Assert.Equal(0, process.ExitCode);
        Assert.True(process.HasExited);
        Assert.Equal("\"yt-dlp argument boundary\"", (await output).Trim());
        Assert.Empty(await error);
    }

    [Fact]
    public void System_process_launcher_releases_process_when_start_fails()
    {
        var executable = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.exe");
        var launcher = new YtDlpDownloadProcessLauncher();

        Assert.ThrowsAny<System.ComponentModel.Win32Exception>(() => launcher.Start(executable, []));
    }

    [Theory]
    [InlineData("")]
    [InlineData("too-short")]
    [InlineData("../escape___")]
    public async Task Invalid_video_identity_never_reaches_external_downloader(string videoId)
    {
        var root = Path.Combine(Path.GetTempPath(), "PresenterYtDlpTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var executable = Path.Combine(root, "yt-dlp.exe");
            var runner = new RecordingRunner(executable);
            var tool = new YtDlpDownloadTool(runner, root, [executable]);
            var staging = Path.Combine(root, "staging");

            await Assert.ThrowsAsync<ArgumentException>(() =>
                tool.DownloadAsync(videoId, staging, _ => { }, CancellationToken.None));

            Assert.Empty(runner.Calls);
            Assert.False(Directory.Exists(staging));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Missing_tool_is_installed_by_exact_winget_id_and_verified()
    {
        var root = Path.Combine(Path.GetTempPath(), "PresenterYtDlpTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var executable = Path.Combine(root, "yt-dlp.exe");
        try
        {
            var runner = new RecordingRunner(executable);
            var tool = new YtDlpDownloadTool(runner, root, [executable]);

            Assert.Equal(executable, await tool.EnsureAvailableAsync(CancellationToken.None));
            Assert.Equal(2, runner.Calls.Count);
            Assert.Equal("winget.exe", runner.Calls[0].Path);
            Assert.Equal(["install", "--id", "yt-dlp.yt-dlp", "-e", "--source", "winget",
                "--accept-package-agreements", "--accept-source-agreements"], runner.Calls[0].Arguments);
            Assert.Equal(executable, runner.Calls[1].Path);
            Assert.Equal(["--version"], runner.Calls[1].Arguments);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Failed_install_reports_error_without_running_download()
    {
        var root = Path.Combine(Path.GetTempPath(), "PresenterYtDlpTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var executable = Path.Combine(root, "yt-dlp.exe");
            var runner = new RecordingRunner(executable) { InstallExitCode = 42 };
            var tool = new YtDlpDownloadTool(runner, root, [executable]);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => tool.EnsureAvailableAsync(CancellationToken.None));
            Assert.Contains("Code 42", error.Message);
            Assert.Single(runner.Calls);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Broken_downloader_candidate_is_skipped_for_the_next_verified_executable()
    {
        var root = Path.Combine(Path.GetTempPath(), "PresenterYtDlpTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var broken = Path.Combine(root, "broken.exe");
            var working = Path.Combine(root, "working.exe");
            File.WriteAllText(broken, "not executable");
            File.WriteAllText(working, "executable");
            var runner = new RecordingRunner(working);
            runner.FailingVersionChecks.Add(broken);
            var tool = new YtDlpDownloadTool(runner, root, [broken, working]);

            Assert.Equal(working, await tool.EnsureAvailableAsync(CancellationToken.None));

            Assert.Equal([broken, working], runner.Calls.Select(call => call.Path));
            Assert.All(runner.Calls, call => Assert.Equal(["--version"], call.Arguments));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Successful_install_is_rejected_when_no_executable_can_be_verified()
    {
        var root = Path.Combine(Path.GetTempPath(), "PresenterYtDlpTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var executable = Path.Combine(root, "yt-dlp.exe");
            var runner = new RecordingRunner(executable) { CreateExecutableOnInstall = false };
            var tool = new YtDlpDownloadTool(runner, root, [executable]);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => tool.EnsureAvailableAsync(CancellationToken.None));

            Assert.Contains("nicht ausführbar", error.Message);
            Assert.Single(runner.Calls);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Download_arguments_restrict_source_size_format_and_configuration()
    {
        var args = YtDlpDownloadTool.BuildArguments("M7lc1UVf-VE", @"C:\staging");
        Assert.Contains("--ignore-config", args);
        Assert.Contains("--no-plugin-dirs", args);
        Assert.Contains("--no-playlist", args);
        Assert.Equal("5G", args[Array.IndexOf(args.ToArray(), "--max-filesize") + 1]);
        Assert.Contains("height<=1080", args[Array.IndexOf(args.ToArray(), "--format") + 1]);
        Assert.Contains("avc1", args[Array.IndexOf(args.ToArray(), "--format") + 1]);
        Assert.Contains("mp4a", args[Array.IndexOf(args.ToArray(), "--format") + 1]);
        Assert.Equal("https://www.youtube.com/watch?v=M7lc1UVf-VE", args[^1]);
    }

    private sealed class RecordingDownloadProcessLauncher(Func<IReadOnlyList<string>, RecordingDownloadProcess> createProcess)
        : IYtDlpDownloadProcessLauncher
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? Executable { get; private set; }
        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public IYtDlpDownloadProcess Start(string executable, IReadOnlyList<string> arguments)
        {
            Executable = executable;
            Arguments = arguments;
            var process = createProcess(arguments);
            Started.TrySetResult();
            return process;
        }
    }

    private sealed class RecordingDownloadProcess : IYtDlpDownloadProcess
    {
        private readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ExitCode { get; }
        public bool HasExited { get; private set; }
        public bool KillCalled { get; private set; }
        public bool Disposed { get; private set; }
        public StreamReader StandardOutput => CreateReader();
        public StreamReader StandardError => CreateReader();

        public RecordingDownloadProcess(int exitCode = 0, bool blockUntilCancelled = false)
        {
            ExitCode = exitCode;
            HasExited = !blockUntilCancelled;
            if (!blockUntilCancelled) exited.TrySetResult();
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken) => exited.Task.WaitAsync(cancellationToken);

        public void Kill()
        {
            KillCalled = true;
            HasExited = true;
            exited.TrySetResult();
        }

        public void Complete()
        {
            HasExited = true;
            exited.TrySetResult();
        }

        public void Dispose() => Disposed = true;

        private static StreamReader CreateReader() =>
            new(new MemoryStream(Encoding.UTF8.GetBytes(string.Empty)));
    }

    private sealed class RecordingRunner(string executable) : IExternalProcessRunner
    {
        public int InstallExitCode { get; set; }
        public bool CreateExecutableOnInstall { get; set; } = true;
        public HashSet<string> FailingVersionChecks { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(string Path, IReadOnlyList<string> Arguments)> Calls { get; } = [];

        public Task<ProcessExecutionResult> RunAsync(string executablePath, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls.Add((executablePath, arguments));
            if (executablePath == "winget.exe" && InstallExitCode == 0 && CreateExecutableOnInstall) File.WriteAllText(executable, "test");
            var exitCode = executablePath == "winget.exe" ? InstallExitCode : FailingVersionChecks.Contains(executablePath) ? 1 : 0;
            return Task.FromResult(new ProcessExecutionResult(exitCode,
                executablePath == "winget.exe" ? "" : "test-version", ""));
        }
    }
}
