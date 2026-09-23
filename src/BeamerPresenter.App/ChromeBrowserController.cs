using System.Diagnostics;
using System.Runtime.InteropServices;
using BeamerPresenter.Application;
using BeamerPresenter.Domain;
using Microsoft.Extensions.Logging;

namespace BeamerPresenter.App;

internal interface IChromeProcess : IDisposable
{
    bool HasExited { get; }
    nint MainWindowHandle { get; }
    void Refresh();
    bool CloseMainWindow();
    void Kill();
    Task WaitForExitAsync(CancellationToken cancellationToken);
}

internal interface IChromeProcessLauncher
{
    IChromeProcess Start(string executablePath, IReadOnlyList<string> arguments);
}

internal interface IChromeWindowController
{
    void Place(nint windowHandle, DisplayMonitor monitor, bool topmost);
    void Restore(nint windowHandle);
    void Minimize(nint windowHandle);
    bool IsTopmost(nint windowHandle);
}

internal sealed class ChromeBrowserController(
    IPresenterSettingsService settingsService,
    IMonitorService monitorService,
    IChromeProcessLauncher processLauncher,
    IChromeWindowController windowController,
    ILogger<ChromeBrowserController> logger,
    string profileDirectory,
    string presenterUrl) : IBrowserController, IAsyncDisposable
{
    private static readonly TimeSpan WindowStartupTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan GracefulShutdownTimeout = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim gate = new(1, 1);
    private IChromeProcess? process;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (process is { HasExited: false })
            {
                await ShowCoreAsync(cancellationToken);
                return;
            }

            process?.Dispose();
            process = null;
            var settings = await settingsService.GetAsync(cancellationToken);
            var monitor = ResolveMonitor(settings);
            var chromePath = ResolveChromePath(settings.ChromePath)
                ?? throw new FileNotFoundException("Google Chrome wurde nicht gefunden. Bitte den Chrome-Pfad in den Einstellungen auswählen.");
            Directory.CreateDirectory(profileDirectory);
            process = processLauncher.Start(chromePath, BuildArguments(profileDirectory, presenterUrl));
            var windowHandle = await WaitForWindowAsync(process, cancellationToken);
            windowController.Place(windowHandle, monitor, settings.AlwaysOnTop);
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Chrome kiosk started on {MonitorDeviceName}", monitor.DeviceName);
            }
        }
        catch
        {
            if (process is not null)
            {
                await StopCoreAsync(CancellationToken.None);
            }

            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ShowAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await ShowCoreAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task HideAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (process is not { HasExited: false })
            {
                return;
            }

            var windowHandle = await WaitForWindowAsync(process, cancellationToken);
            var settings = await settingsService.GetAsync(cancellationToken);
            windowController.Place(windowHandle, ResolveMonitor(settings), topmost: false);
            windowController.Minimize(windowHandle);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> IsRunningAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return process is { HasExited: false };
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> IsTopmostAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (process is not { HasExited: false })
            {
                return false;
            }

            var windowHandle = await WaitForWindowAsync(process, cancellationToken);
            return windowController.IsTopmost(windowHandle);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        gate.Dispose();
    }

    internal static IReadOnlyList<string> BuildArguments(string profilePath, string url) =>
    [
        "--kiosk",
        "--no-first-run",
        "--disable-session-crashed-bubble",
        "--autoplay-policy=no-user-gesture-required",
        $"--user-data-dir={profilePath}",
        url
    ];

    private async Task ShowCoreAsync(CancellationToken cancellationToken)
    {
        if (process is not { HasExited: false })
        {
            throw new InvalidOperationException("Chrome läuft nicht.");
        }

        var settings = await settingsService.GetAsync(cancellationToken);
        var monitor = ResolveMonitor(settings);
        var windowHandle = await WaitForWindowAsync(process, cancellationToken);
        windowController.Restore(windowHandle);
        windowController.Place(windowHandle, monitor, settings.AlwaysOnTop);
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        var current = process;
        process = null;
        if (current is null)
        {
            return;
        }

        try
        {
            if (!current.HasExited)
            {
                current.CloseMainWindow();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(GracefulShutdownTimeout);
                try
                {
                    await current.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    current.Kill();
                    await current.WaitForExitAsync(cancellationToken);
                }
            }
        }
        finally
        {
            current.Dispose();
        }
    }

    private DisplayMonitor ResolveMonitor(PresenterSettings settings)
    {
        var monitor = monitorService.GetAll().FirstOrDefault(candidate =>
            string.Equals(candidate.DeviceName, settings.MonitorDeviceName, StringComparison.OrdinalIgnoreCase));
        return monitor ?? throw new InvalidOperationException("Der konfigurierte Zielmonitor ist nicht verfügbar.");
    }

    private static async Task<nint> WaitForWindowAsync(IChromeProcess chromeProcess, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + WindowStartupTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (chromeProcess.HasExited)
            {
                throw new InvalidOperationException("Chrome wurde beendet, bevor das Kioskfenster verfügbar war.");
            }

            chromeProcess.Refresh();
            if (chromeProcess.MainWindowHandle != 0)
            {
                return chromeProcess.MainWindowHandle;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        throw new TimeoutException("Das Chrome-Kioskfenster wurde nicht rechtzeitig gefunden.");
    }

    private static string? ResolveChromePath(string? configuredPath)
    {
        var candidates = new[]
        {
            configuredPath,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe")
        };
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    }
}

internal sealed class ChromeProcessLauncher : IChromeProcessLauncher
{
    public IChromeProcess Start(string executablePath, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(executablePath) { UseShellExecute = false };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Google Chrome konnte nicht gestartet werden.");
        return new ChromeProcess(process);
    }

    private sealed class ChromeProcess(Process process) : IChromeProcess
    {
        public bool HasExited => process.HasExited;
        public nint MainWindowHandle => process.MainWindowHandle;
        public void Refresh() => process.Refresh();
        public bool CloseMainWindow() => process.CloseMainWindow();
        public void Kill() => process.Kill(entireProcessTree: true);
        public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);
        public void Dispose() => process.Dispose();
    }
}

internal sealed partial class ChromeWindowController : IChromeWindowController
{
    private const int ExtendedWindowStyle = -20;
    private const long TopmostWindowStyle = 0x00000008L;
    private const uint ShowWindow = 0x0040;
    private static readonly nint Topmost = new(-1);
    private static readonly nint NotTopmost = new(-2);

    public void Place(nint windowHandle, DisplayMonitor monitor, bool topmost)
    {
        EnsureWindowHandle(windowHandle);
        if (!SetWindowPos(
                windowHandle,
                topmost ? Topmost : NotTopmost,
                monitor.X,
                monitor.Y,
                monitor.Width,
                monitor.Height,
                ShowWindow))
        {
            throw new InvalidOperationException($"Das Chrome-Fenster konnte nicht auf {monitor.DeviceName} positioniert werden.");
        }
    }

    public void Restore(nint windowHandle)
    {
        EnsureWindowHandle(windowHandle);
        ShowWindowAsync(windowHandle, 9);
    }

    public void Minimize(nint windowHandle)
    {
        EnsureWindowHandle(windowHandle);
        ShowWindowAsync(windowHandle, 6);
    }

    public bool IsTopmost(nint windowHandle)
    {
        EnsureWindowHandle(windowHandle);
        return (GetWindowLongPtr(windowHandle, ExtendedWindowStyle).ToInt64() & TopmostWindowStyle) != 0;
    }

    private static void EnsureWindowHandle(nint windowHandle)
    {
        if (windowHandle == 0)
        {
            throw new ArgumentException("Ein gültiges Chrome-Fensterhandle ist erforderlich.", nameof(windowHandle));
        }
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint windowHandle, nint insertAfter, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindowAsync(nint windowHandle, int command);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static partial nint GetWindowLongPtr(nint windowHandle, int index);
}
