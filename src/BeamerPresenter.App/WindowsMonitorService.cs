using System.Runtime.InteropServices;
using BeamerPresenter.Application;

namespace BeamerPresenter.App;

internal sealed class WindowsMonitorService : IMonitorService
{
    public IReadOnlyList<DisplayMonitor> GetAll() => Screen.AllScreens
        .Select(screen => new DisplayMonitor(
            screen.DeviceName,
            GetFriendlyName(screen.DeviceName),
            screen.Bounds.X,
            screen.Bounds.Y,
            screen.Bounds.Width,
            screen.Bounds.Height,
            screen.Primary))
        .OrderByDescending(monitor => monitor.IsPrimary)
        .ThenBy(monitor => monitor.X)
        .ThenBy(monitor => monitor.Y)
        .ToList();

    private static string GetFriendlyName(string deviceName)
    {
        var displayDevice = DisplayDevice.Create();
        return EnumDisplayDevices(deviceName, 0, ref displayDevice, 0)
            && !string.IsNullOrWhiteSpace(displayDevice.DeviceString)
                ? displayDevice.DeviceString
                : deviceName;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(
        string? device,
        uint deviceNumber,
        ref DisplayDevice displayDevice,
        uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;

        public int StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;

        public static DisplayDevice Create() => new()
        {
            Size = Marshal.SizeOf<DisplayDevice>(),
            DeviceName = string.Empty,
            DeviceString = string.Empty,
            DeviceId = string.Empty,
            DeviceKey = string.Empty
        };
    }
}
