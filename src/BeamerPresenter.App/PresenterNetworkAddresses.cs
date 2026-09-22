using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace BeamerPresenter.App;

internal static class PresenterNetworkAddresses
{
    public static IReadOnlyList<IPAddress> GetLanIpv4Addresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up &&
                              network.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Select(address => address.Address)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork &&
                              !IPAddress.IsLoopback(address) &&
                              !address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            .Distinct()
            .OrderBy(address => address.ToString(), StringComparer.Ordinal)
            .ToArray();

    public static string FormatWebUrls(int port, bool allowLanAccess, IEnumerable<IPAddress> lanAddresses)
    {
        var localUrl = $"http://localhost:{port}";
        if (!allowLanAccess)
        {
            return $"{localUrl} (nur lokal)";
        }

        var urls = lanAddresses
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
            .Distinct()
            .Select(address => $"http://{address}:{port}")
            .ToArray();
        return urls.Length == 0
            ? $"{localUrl} (keine LAN-Adresse erkannt)"
            : $"Lokal: {localUrl}{Environment.NewLine}LAN: {string.Join(" | ", urls)}";
    }
}
