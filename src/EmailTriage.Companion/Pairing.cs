using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using QRCoder;

namespace EmailTriage.Companion;

/// <summary>
/// The pairing link the phone scans: where to find this PC, the token to
/// send, and the certificate fingerprint to pin. Shown as a QR code, and as
/// text to paste for when the camera is not an option.
/// </summary>
public static class Pairing
{
    public const string Scheme = "emailtriage";

    public static string Link(IEnumerable<IPAddress> hosts, int port, CompanionIdentity identity, string pcName)
    {
        var h = string.Join(",", hosts.Select(a => a.ToString()));
        return $"{Scheme}://pair?h={Uri.EscapeDataString(h)}&p={port}" +
               $"&t={Uri.EscapeDataString(identity.Token)}&f={identity.Fingerprint}" +
               $"&n={Uri.EscapeDataString(pcName)}";
    }

    /// <summary>The link as a PNG QR code, for the pairing window to show.</summary>
    public static byte[] QrPng(string link, int pixelsPerModule = 8)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(link, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(pixelsPerModule);
    }

    /// <summary>
    /// This PC's addresses on private networks - the ones a phone on the same
    /// Wi-Fi can reach - Wi-Fi and Ethernet first, virtual adapters last.
    /// </summary>
    public static IReadOnlyList<IPAddress> LocalAddresses()
    {
        var found = new List<(IPAddress Address, int Rank)>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            var rank = nic.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Wireless80211 => 0,
                NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet => 1,
                _ => 2,
            };
            var description = nic.Description + " " + nic.Name;
            if (description.Contains("virtual", StringComparison.OrdinalIgnoreCase)
                || description.Contains("hyper-v", StringComparison.OrdinalIgnoreCase)
                || description.Contains("vpn", StringComparison.OrdinalIgnoreCase)
                || description.Contains("vethernet", StringComparison.OrdinalIgnoreCase)) rank = 3;

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (!LocalNetwork.IsPrivate(address) || IPAddress.IsLoopback(address)) continue;
                found.Add((address, rank));
            }
        }

        return found.OrderBy(f => f.Rank).Select(f => f.Address).Distinct().ToList();
    }
}

/// <summary>Which callers count as "on the same network".</summary>
public static class LocalNetwork
{
    /// <summary>
    /// Loopback, the RFC 1918 ranges, link-local, and IPv6 unique-local and
    /// link-local. Anything else is the internet, however it got here.
    /// </summary>
    public static bool IsPrivate(IPAddress? address)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return address.IsIPv6LinkLocal || (b[0] & 0xFE) == 0xFC;
        }

        return false;
    }
}
