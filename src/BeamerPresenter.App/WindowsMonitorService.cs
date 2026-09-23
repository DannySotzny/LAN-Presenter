using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
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

    private static string GetFriendlyName(string deviceName)
    {
        var displayDevice = DisplayDevice.Create();
        if (!EnumDisplayDevices(deviceName, 0, ref displayDevice, 0))
        {
            return deviceName;
        }

        Span<char> deviceString = displayDevice.DeviceString;
        var terminator = deviceString.IndexOf('\0');
        var friendlyName = new string(deviceString[..(terminator < 0 ? deviceString.Length : terminator)]);
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
    private struct DisplayDevice
    {
        public int Size;
        public DeviceNameBuffer DeviceName;
        public DeviceStringBuffer DeviceString;

        public int StateFlags;
        public DeviceIdBuffer DeviceId;
        public DeviceKeyBuffer DeviceKey;

        public static DisplayDevice Create() => new()
        {
            Size = Unsafe.SizeOf<DisplayDevice>()
        };
    }

    [InlineArray(32)]
    private struct DeviceNameBuffer
    {
        [SuppressMessage("Major Code Smell", "S1144:Unused private types or members should be removed", Justification = "InlineArray backing storage is consumed by compiler-generated indexing.")]
        private char element0;
    }

    [InlineArray(128)]
    private struct DeviceStringBuffer
    {
        [SuppressMessage("Major Code Smell", "S1144:Unused private types or members should be removed", Justification = "InlineArray backing storage is consumed by compiler-generated indexing.")]
        private char element0;
    }

    [InlineArray(128)]
    private struct DeviceIdBuffer
    {
        [SuppressMessage("Major Code Smell", "S1144:Unused private types or members should be removed", Justification = "InlineArray backing storage is consumed by compiler-generated indexing.")]
        private char element0;
    }

    [InlineArray(128)]
    private struct DeviceKeyBuffer
    {
        [SuppressMessage("Major Code Smell", "S1144:Unused private types or members should be removed", Justification = "InlineArray backing storage is consumed by compiler-generated indexing.")]
        private char element0;
    }
}
