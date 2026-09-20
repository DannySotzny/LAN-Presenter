using System.ComponentModel;
using System.Runtime.InteropServices;
using BeamerPresenter.Application;

namespace BeamerPresenter.App;

internal sealed class WindowsPowerManagementService : IPowerManagementService, IDisposable
{
    private const uint SimpleReasonString = 0x1;
    private readonly object sync = new();
    private nint requestHandle;
    private bool displayRequestActive;
    private bool systemRequestActive;

    public Task ApplyAsync(bool preventDisplaySleep, bool preventSystemSleep, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            EnsureHandle();
            SetRequest(PowerRequestType.DisplayRequired, preventDisplaySleep, ref displayRequestActive);
            SetRequest(PowerRequestType.SystemRequired, preventSystemSleep, ref systemRequestActive);
        }

        return Task.CompletedTask;
    }

    public Task ReleaseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            ClearActiveRequests();
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (sync)
        {
            ClearActiveRequests();
            if (requestHandle != 0 && requestHandle != new nint(-1))
            {
                CloseHandle(requestHandle);
                requestHandle = 0;
            }
        }
    }

    private void EnsureHandle()
    {
        if (requestHandle != 0)
        {
            return;
        }

        var reason = Marshal.StringToHGlobalUni("Beamer Presenter for LAN-Parties is active");
        try
        {
            var context = new ReasonContext
            {
                Version = 0,
                Flags = SimpleReasonString,
                SimpleReasonString = reason
            };
            requestHandle = PowerCreateRequest(ref context);
            if (requestHandle == 0 || requestHandle == new nint(-1))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Der Windows-Power-Request konnte nicht erstellt werden.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(reason);
        }
    }

    private void SetRequest(PowerRequestType requestType, bool shouldBeActive, ref bool isActive)
    {
        if (shouldBeActive == isActive)
        {
            return;
        }

        var succeeded = shouldBeActive
            ? PowerSetRequest(requestHandle, requestType)
            : PowerClearRequest(requestHandle, requestType);
        if (!succeeded)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Der Windows-Power-Request konnte nicht geändert werden.");
        }

        isActive = shouldBeActive;
    }

    private void ClearActiveRequests()
    {
        if (requestHandle == 0 || requestHandle == new nint(-1))
        {
            return;
        }

        if (displayRequestActive)
        {
            PowerClearRequest(requestHandle, PowerRequestType.DisplayRequired);
            displayRequestActive = false;
        }

        if (systemRequestActive)
        {
            PowerClearRequest(requestHandle, PowerRequestType.SystemRequired);
            systemRequestActive = false;
        }
    }

    private enum PowerRequestType
    {
        DisplayRequired = 0,
        SystemRequired = 1
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public nint SimpleReasonString;
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern nint PowerCreateRequest(ref ReasonContext context);

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(nint powerRequest, PowerRequestType requestType);

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerClearRequest(nint powerRequest, PowerRequestType requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
