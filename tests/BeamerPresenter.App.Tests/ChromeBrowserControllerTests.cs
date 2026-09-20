using BeamerPresenter.App;
using BeamerPresenter.Application;
using BeamerPresenter.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeamerPresenter.App.Tests;

public sealed class ChromeBrowserControllerTests
{
    [Fact]
    public async Task Controller_starts_kiosk_on_configured_monitor_and_controls_window()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "BeamerPresenter.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        var chromePath = Path.Combine(testRoot, "chrome.exe");
        await File.WriteAllTextAsync(chromePath, string.Empty);
        var settings = new PresenterSettings
        {
            ChromePath = chromePath,
            MonitorDeviceName = "\\\\.\\DISPLAY2",
            AlwaysOnTop = true
        };
        var launcher = new FakeChromeProcessLauncher();
        var windows = new RecordingChromeWindowController();
        await using var controller = new ChromeBrowserController(
            new StubSettingsService(settings),
            new StubMonitorService(),
            launcher,
            windows,
            NullLogger<ChromeBrowserController>.Instance,
            Path.Combine(testRoot, "ChromeProfile"),
            "http://127.0.0.1:8765/presenter");

        try
        {
            await controller.StartAsync();

            Assert.Equal(chromePath, launcher.ExecutablePath);
            Assert.Contains("--kiosk", launcher.Arguments);
            Assert.Contains("--no-first-run", launcher.Arguments);
            Assert.Contains("--disable-session-crashed-bubble", launcher.Arguments);
            Assert.Contains("--autoplay-policy=no-user-gesture-required", launcher.Arguments);
            Assert.Contains($"--user-data-dir={Path.Combine(testRoot, "ChromeProfile")}", launcher.Arguments);
            Assert.Equal("http://127.0.0.1:8765/presenter", launcher.Arguments[^1]);
            Assert.Equal((new IntPtr(42), "\\\\.\\DISPLAY2", true), Assert.Single(windows.Placements));
            Assert.True(await controller.IsRunningAsync());

            await controller.HideAsync();
            await controller.ShowAsync();

            Assert.Equal(new IntPtr(42), Assert.Single(windows.Minimized));
            Assert.Equal(new IntPtr(42), Assert.Single(windows.Restored));
            Assert.Equal(3, windows.Placements.Count);
            Assert.Equal([true, false, true], windows.Placements.Select(placement => placement.Topmost));

            await controller.StopAsync();
            Assert.False(await controller.IsRunningAsync());
            Assert.True(launcher.Process.CloseRequested);
            Assert.True(launcher.Process.Disposed);
        }
        finally
        {
            var allowedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BeamerPresenter.Tests"));
            var resolvedRoot = Path.GetFullPath(testRoot);
            if (resolvedRoot.StartsWith(allowedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolvedRoot))
            {
                Directory.Delete(resolvedRoot, recursive: true);
            }
        }
    }

    private sealed class StubSettingsService(PresenterSettings settings) : IPresenterSettingsService
    {
        public Task<PresenterSettings> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(settings);
        public Task SaveAsync(PresenterSettings value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetWebPasswordAsync(string password, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> VerifyWebPasswordAsync(string password, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> HasWebPasswordAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class StubMonitorService : IMonitorService
    {
        public IReadOnlyList<DisplayMonitor> GetAll() =>
            [new DisplayMonitor("\\\\.\\DISPLAY2", "EPSON PJ", 1920, 0, 1920, 1080, false)];
    }

    private sealed class FakeChromeProcessLauncher : IChromeProcessLauncher
    {
        public FakeChromeProcess Process { get; } = new();
        public string? ExecutablePath { get; private set; }
        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public IChromeProcess Start(string executablePath, IReadOnlyList<string> arguments)
        {
            ExecutablePath = executablePath;
            Arguments = arguments;
            return Process;
        }
    }

    private sealed class FakeChromeProcess : IChromeProcess
    {
        public bool HasExited { get; private set; }
        public nint MainWindowHandle => new IntPtr(42);
        public bool CloseRequested { get; private set; }
        public bool Disposed { get; private set; }
        public void Refresh() { }
        public bool CloseMainWindow() { CloseRequested = true; HasExited = true; return true; }
        public void Kill() => HasExited = true;
        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() => Disposed = true;
    }

    private sealed class RecordingChromeWindowController : IChromeWindowController
    {
        public List<(nint Handle, string DeviceName, bool Topmost)> Placements { get; } = [];
        public List<nint> Restored { get; } = [];
        public List<nint> Minimized { get; } = [];

        public void Place(nint windowHandle, DisplayMonitor monitor, bool topmost) =>
            Placements.Add((windowHandle, monitor.DeviceName, topmost));

        public void Restore(nint windowHandle) => Restored.Add(windowHandle);
        public void Minimize(nint windowHandle) => Minimized.Add(windowHandle);
    }
}
