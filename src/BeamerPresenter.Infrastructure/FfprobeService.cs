using System.Diagnostics;
using BeamerPresenter.Application;
using Microsoft.Extensions.Logging;

namespace BeamerPresenter.Infrastructure;

internal sealed record ProcessExecutionResult(int ExitCode, string StandardOutput, string StandardError);

internal interface IExternalProcessRunner
{
    Task<ProcessExecutionResult> RunAsync(string executablePath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);
}

internal sealed class ExternalProcessRunner : IExternalProcessRunner
{
    public async Task<ProcessExecutionResult> RunAsync(string executablePath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Der Prozess '{executablePath}' konnte nicht gestartet werden.");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"Der Prozess '{executablePath}' hat das Zeitlimit überschritten.");
        }

        return new ProcessExecutionResult(process.ExitCode, await standardOutput, await standardError);
    }
}

internal sealed class FfprobeService(
    IPresenterSettingsService settingsService,
    IExternalProcessRunner processRunner,
    ILogger<FfprobeService> logger,
    string toolsDirectory) : IFfprobeService
{
    private static readonly TimeSpan ValidationTimeout = TimeSpan.FromSeconds(10);

    public async Task<FfprobeAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetAsync(cancellationToken);
        foreach (var candidate in GetCandidates(settings.FfprobePath))
        {
            try
            {
                var result = await processRunner.RunAsync(candidate, ["-version"], ValidationTimeout, cancellationToken);
                if (result.ExitCode != 0)
                {
                    continue;
                }

                var version = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                return new FfprobeAvailability(true, candidate, version ?? "ffprobe", null);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogDebug(exception, "FFprobe candidate {FfprobePath} could not be validated", candidate);
            }
        }

        return new FfprobeAvailability(false, null, null, "FFprobe wurde nicht gefunden oder konnte nicht ausgeführt werden.");
    }

    public async Task<FfprobeAvailability> InstallWithWinGetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await processRunner.RunAsync(
                "winget.exe",
                ["install", "--id", "Gyan.FFmpeg", "-e", "--source", "winget", "--accept-package-agreements", "--accept-source-agreements"],
                TimeSpan.FromMinutes(10),
                cancellationToken);
            if (result.ExitCode != 0)
            {
                return new FfprobeAvailability(false, null, null, string.IsNullOrWhiteSpace(result.StandardError) ? "WinGet konnte FFmpeg nicht installieren." : result.StandardError.Trim());
            }

            return await CheckAvailabilityAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "FFmpeg installation through WinGet failed");
            return new FfprobeAvailability(false, null, null, exception.Message);
        }
    }

    private IEnumerable<string> GetCandidates(string? configuredPath)
    {
        var candidates = new List<string?>
        {
            configuredPath,
            Path.Combine(toolsDirectory, "ffprobe.exe")
        };
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        candidates.AddRange(pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(path => Path.Combine(path, "ffprobe.exe")));
        candidates.AddRange(
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "ffprobe.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin", "ffprobe.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "ffmpeg", "bin", "ffprobe.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey", "bin", "ffprobe.exe")
        ]);

        return candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
