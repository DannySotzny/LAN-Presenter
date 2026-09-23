using System.Runtime.InteropServices;
using BeamerPresenter.Application;

namespace BeamerPresenter.App;

internal sealed partial class WindowsMonitorService : IMonitorService
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

    private static unsafe string GetFriendlyName(string deviceName)
    {
        var displayDevice = DisplayDevice.Create();
        if (!EnumDisplayDevices(deviceName, 0, ref displayDevice, 0))
        {
            return deviceName;
        }

        char* deviceString = displayDevice.DeviceString;
        var friendlyName = new string(deviceString);
        return string.IsNullOrWhiteSpace(friendlyName) ? deviceName : friendlyName;
    }

    [LibraryImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplayDevices(
        string? device,
        uint deviceNumber,
        ref DisplayDevice displayDevice,
        uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct DisplayDevice
    {
        public int Size;
        public fixed char DeviceName[32];
        public fixed char DeviceString[128];

        public int StateFlags;
        public fixed char DeviceId[128];
        public fixed char DeviceKey[128];

        public static DisplayDevice Create() => new()
        {
            Size = Marshal.SizeOf<DisplayDevice>()
        };
    }
}
