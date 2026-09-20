using Microsoft.Win32;

namespace BeamerPresenter.App;

internal sealed class StartupRegistrationService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Beamer Presenter for LAN-Parties";
    private readonly string _executablePath;

    public StartupRegistrationService(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _executablePath = Path.GetFullPath(executablePath);
    }

    internal string StartupCommand => $"\"{_executablePath}\" --autostart";

    public bool IsEnabled()
    {
        using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var configuredCommand = runKey?.GetValue(ValueName) as string;
        return string.Equals(configuredCommand, StartupCommand, StringComparison.OrdinalIgnoreCase);
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            runKey.SetValue(ValueName, StartupCommand, RegistryValueKind.String);
            return;
        }

        using var existingRunKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        existingRunKey?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
