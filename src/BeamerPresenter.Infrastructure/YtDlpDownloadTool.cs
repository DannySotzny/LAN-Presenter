using System.Diagnostics;
using BeamerPresenter.Application;

namespace BeamerPresenter.Infrastructure;

internal sealed class YtDlpDownloadTool(IExternalProcessRunner processRunner, string toolsDirectory, IEnumerable<string>? executableCandidates = null) : IYouTubeDownloadTool
{
    private readonly SemaphoreSlim installGate = new(1, 1);

    public async Task<string> DownloadAsync(string videoId, string stagingDirectory, Action<YouTubeDownloadPhase> reportPhase, CancellationToken cancellationToken)
    {
        if (videoId.Length != 11 || videoId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-')))
        {
            throw new ArgumentException("Die YouTube-Video-ID ist ungültig.", nameof(videoId));
        }

        var executable = await EnsureAvailableAsync(cancellationToken);
        reportPhase(YouTubeDownloadPhase.Downloading);
        Directory.CreateDirectory(stagingDirectory);
        var arguments = BuildArguments(videoId, stagingDirectory);
        using var process = new Process
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
        if (!process.Start()) throw new InvalidOperationException("yt-dlp konnte nicht gestartet werden.");

        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        var sizeExceeded = false;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromHours(2));
        var sizeMonitor = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(limit.Token))
            {
                long currentBytes;
                try
                {
                    currentBytes = Directory.EnumerateFiles(stagingDirectory, "*", SearchOption.AllDirectories)
                    .Sum(path => new FileInfo(path).Length);
                }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                if (currentBytes <= YouTubeDownloadLimits.MaximumBytes) continue;
                sizeExceeded = true;
                await limit.CancelAsync();
                break;
            }
        }, CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            if (sizeExceeded) throw new InvalidOperationException("Das Video überschreitet die Downloadgrenze von 5 GB.");
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException("Der YouTube-Download hat das Zeitlimit überschritten.");
        }
        finally
        {
            await limit.CancelAsync();
            try { await sizeMonitor; }
            catch (OperationCanceledException) when (limit.IsCancellationRequested) { }
        }

        _ = await output;
        _ = await errors;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"yt-dlp konnte das Video nicht laden (Code {process.ExitCode}).");
        }

        var file = Directory.EnumerateFiles(stagingDirectory, "*.mp4", SearchOption.TopDirectoryOnly).SingleOrDefault()
            ?? throw new InvalidOperationException("yt-dlp hat keine MP4-Datei erzeugt.");
        var size = new FileInfo(file).Length;
        if (!YouTubeDownloadLimits.IsValidSize(size))
        {
            throw new InvalidOperationException("Die heruntergeladene Datei ist leer oder größer als 5 GB.");
        }
        return file;
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
            Path.Combine(toolsDirectory, "yt-dlp.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "yt-dlp.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey", "bin", "yt-dlp.exe")
        }.Concat(pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(path => Path.Combine(path, "yt-dlp.exe"))).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates.Where(File.Exists))
        {
            try
            {
                var result = await processRunner.RunAsync(candidate, ["--version"], TimeSpan.FromSeconds(10), cancellationToken);
                if (result.ExitCode == 0) return candidate;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { }
        }
        return null;
    }
}
