using System.Diagnostics;
using BeamerPresenter.Application;

namespace BeamerPresenter.Infrastructure;

internal sealed class YtDlpDownloadTool(
    IExternalProcessRunner processRunner,
    string toolsDirectory,
    IEnumerable<string>? executableCandidates = null,
    IYtDlpDownloadProcessLauncher? downloadProcessLauncher = null) : IYouTubeDownloadTool
{
    private const string ExecutableName = "yt-dlp.exe";
    private readonly SemaphoreSlim installGate = new(1, 1);
    private readonly IYtDlpDownloadProcessLauncher processLauncher = downloadProcessLauncher ?? new YtDlpDownloadProcessLauncher();

    public async Task<string> DownloadAsync(string videoId, string stagingDirectory, Action<YouTubeDownloadPhase> reportPhase, CancellationToken cancellationToken)
    {
        ValidateVideoId(videoId);
        var executable = await EnsureAvailableAsync(cancellationToken);
        reportPhase(YouTubeDownloadPhase.Downloading);
        Directory.CreateDirectory(stagingDirectory);
        var arguments = BuildArguments(videoId, stagingDirectory);
        using var process = processLauncher.Start(executable, arguments);

        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        await WaitForDownloadAsync(process, stagingDirectory, cancellationToken);
        _ = await output;
        _ = await errors;
        EnsureSuccessfulExit(process.ExitCode);

        var file = Directory.EnumerateFiles(stagingDirectory, "*.mp4", SearchOption.TopDirectoryOnly).SingleOrDefault()
            ?? throw new InvalidOperationException("yt-dlp hat keine MP4-Datei erzeugt.");
        EnsureValidDownloadedFile(file);
        return file;
    }

    private static void ValidateVideoId(string videoId)
    {
        if (videoId.Length != 11 || videoId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-')))
        {
            throw new ArgumentException("Die YouTube-Video-ID ist ungültig.", nameof(videoId));
        }
    }

    private static async Task WaitForDownloadAsync(IYtDlpDownloadProcess process, string stagingDirectory, CancellationToken cancellationToken)
    {
        var sizeExceeded = 0;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromHours(2));
        var sizeMonitor = MonitorDownloadSizeAsync(stagingDirectory, limit, () => Interlocked.Exchange(ref sizeExceeded, 1));
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested)
        {
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync(CancellationToken.None);
            ThrowDownloadCancellation(Volatile.Read(ref sizeExceeded) != 0, cancellationToken);
        }
        finally
        {
            await limit.CancelAsync();
            await sizeMonitor;
        }
    }

    private static async Task MonitorDownloadSizeAsync(string stagingDirectory, CancellationTokenSource limit, Action onLimitReached)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (true)
        {
            bool hasNextTick;
            try
            {
                hasNextTick = await timer.WaitForNextTickAsync(limit.Token);
            }
            catch (OperationCanceledException) when (limit.IsCancellationRequested)
            {
                break;
            }

            if (!hasNextTick) break;
            long currentBytes;
            try
            {
                currentBytes = Directory.EnumerateFiles(stagingDirectory, "*", SearchOption.AllDirectories)
                    .Sum(path => new FileInfo(path).Length);
            }
            catch (IOException)
            {
                // yt-dlp may be creating, moving, or removing a file during this snapshot.
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                // A transient access denial should not stop the download monitor.
                continue;
            }

            if (currentBytes <= YouTubeDownloadLimits.MaximumBytes) continue;
            onLimitReached();
            await limit.CancelAsync();
            return;
        }
    }

    private static void ThrowDownloadCancellation(bool sizeExceeded, CancellationToken cancellationToken)
    {
        if (sizeExceeded) throw new InvalidOperationException("Das Video überschreitet die Downloadgrenze von 5 GB.");
        cancellationToken.ThrowIfCancellationRequested();
        throw new TimeoutException("Der YouTube-Download hat das Zeitlimit überschritten.");
    }

    private static void EnsureSuccessfulExit(int exitCode)
    {
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"yt-dlp konnte das Video nicht laden (Code {exitCode}).");
        }
    }

    private static void EnsureValidDownloadedFile(string file)
    {
        var size = new FileInfo(file).Length;
        if (!YouTubeDownloadLimits.IsValidSize(size))
        {
            throw new InvalidOperationException("Die heruntergeladene Datei ist leer oder größer als 5 GB.");
        }
    }

    internal static IReadOnlyList<string> BuildArguments(string videoId, string stagingDirectory) =>
    [
        "--ignore-config", "--no-plugin-dirs", "--no-playlist", "--no-overwrites", "--quiet", "--no-warnings",
        "--max-filesize", "5G",
        "--format", "bv*[height<=1080][vcodec^=avc1]+ba[acodec^=mp4a]/b[height<=1080][ext=mp4][vcodec^=avc1][acodec^=mp4a]",
        "--merge-output-format", "mp4", "--paths", stagingDirectory,
        "--output", $"YouTube-{videoId}.%(ext)s", "--", $"https://www.youtube.com/watch?v={videoId}"
    ];

    internal async Task<string> EnsureAvailableAsync(CancellationToken cancellationToken)
    {
        await installGate.WaitAsync(cancellationToken);
        try
        {
            var existing = await FindExecutableAsync(cancellationToken);
            if (existing is not null) return existing;
            ProcessExecutionResult install;
            try
            {
                install = await processRunner.RunAsync("winget.exe",
                    ["install", "--id", "yt-dlp.yt-dlp", "-e", "--source", "winget", "--accept-package-agreements", "--accept-source-agreements"],
                    TimeSpan.FromMinutes(15), cancellationToken);
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                throw new InvalidOperationException("WinGet ist nicht verfügbar. yt-dlp konnte nicht installiert werden.", exception);
            }
            if (install.ExitCode != 0)
            {
                throw new InvalidOperationException($"yt-dlp konnte nicht installiert werden (Code {install.ExitCode}).");
            }
            return await FindExecutableAsync(cancellationToken)
                ?? throw new InvalidOperationException("yt-dlp wurde installiert, ist aber nicht ausführbar.");
        }
        finally
        {
            installGate.Release();
        }
    }

    private async Task<string?> FindExecutableAsync(CancellationToken cancellationToken)
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var candidates = executableCandidates ?? new[]
        {
            Path.Combine(toolsDirectory, ExecutableName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", ExecutableName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey", "bin", ExecutableName)
        }.Concat(pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(path => Path.Combine(path, ExecutableName))).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates.Where(File.Exists))
        {
            try
            {
                var result = await processRunner.RunAsync(candidate, ["--version"], TimeSpan.FromSeconds(10), cancellationToken);
                if (result.ExitCode == 0) return candidate;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                // A candidate can be present but broken; keep trying the next trusted location.
                continue;
            }
        }
        return null;
    }
}

internal interface IYtDlpDownloadProcess : IDisposable
{
    StreamReader StandardOutput { get; }
    StreamReader StandardError { get; }
    int ExitCode { get; }
    bool HasExited { get; }
    Task WaitForExitAsync(CancellationToken cancellationToken);
    void Kill();
}

internal interface IYtDlpDownloadProcessLauncher
{
    IYtDlpDownloadProcess Start(string executable, IReadOnlyList<string> arguments);
}

internal sealed class YtDlpDownloadProcessLauncher : IYtDlpDownloadProcessLauncher
{
    public IYtDlpDownloadProcess Start(string executable, IReadOnlyList<string> arguments)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (process.Start()) return new RunningYtDlpDownloadProcess(process);
        process.Dispose();
        throw new InvalidOperationException("yt-dlp konnte nicht gestartet werden.");
    }

    private sealed class RunningYtDlpDownloadProcess(Process process) : IYtDlpDownloadProcess
    {
        public StreamReader StandardOutput => process.StandardOutput;
        public StreamReader StandardError => process.StandardError;
        public int ExitCode => process.ExitCode;
        public bool HasExited => process.HasExited;
        public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);
        public void Kill() => process.Kill(entireProcessTree: true);
        public void Dispose() => process.Dispose();
    }
}
