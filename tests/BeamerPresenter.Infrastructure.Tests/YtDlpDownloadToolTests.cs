using BeamerPresenter.Infrastructure;

namespace BeamerPresenter.Infrastructure.Tests;

public sealed class YtDlpDownloadToolTests
{
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

    private sealed class RecordingRunner(string executable) : IExternalProcessRunner
    {
        public int InstallExitCode { get; set; }
        public List<(string Path, IReadOnlyList<string> Arguments)> Calls { get; } = [];

        public Task<ProcessExecutionResult> RunAsync(string executablePath, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls.Add((executablePath, arguments));
            if (executablePath == "winget.exe" && InstallExitCode == 0) File.WriteAllText(executable, "test");
            return Task.FromResult(new ProcessExecutionResult(executablePath == "winget.exe" ? InstallExitCode : 0,
                executablePath == "winget.exe" ? "" : "test-version", ""));
        }
    }
}
