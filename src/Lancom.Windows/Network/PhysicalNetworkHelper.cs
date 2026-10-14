using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Lancom.Windows.Network;

public class NetworkAdapterInfo
{
    public string Name { get; set; } = string.Empty;
    public IPAddress Address { get; set; } = null!;
    public IPAddress SubnetMask { get; set; } = null!;
    public IPAddress BroadcastAddress { get; set; } = null!;
    public NetworkInterfaceType InterfaceType { get; set; }
}

public static class PhysicalNetworkHelper
{
    public static List<NetworkAdapterInfo> GetPhysicalAdapters()
    {
        var result = new List<NetworkAdapterInfo>();

        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();

            foreach (var iface in interfaces)
            {
                if (iface.OperationalStatus != OperationalStatus.Up) continue;

                // Only Wi-Fi or Ethernet
                if (iface.NetworkInterfaceType != NetworkInterfaceType.Wireless80211 &&
                    iface.NetworkInterfaceType != NetworkInterfaceType.Ethernet)
                {
                    continue;
                }

                // Filter out common virtual adapters (WSL, Hyper-V, VMware, VirtualBox, Tailscale)
                string desc = iface.Description.ToLowerInvariant();
                string name = iface.Name.ToLowerInvariant();
                if (desc.Contains("virtual") || desc.Contains("hyper-v") || desc.Contains("wsl") ||
                    desc.Contains("vmware") || desc.Contains("tailscale") || desc.Contains("wireguard") ||
                    name.Contains("veth") || name.Contains("loopback"))
                {
                    continue;
                }

                var ipProps = iface.GetIPProperties();
                foreach (var unicast in ipProps.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(unicast.Address))
                    {
                        var mask = unicast.IPv4Mask ?? IPAddress.Parse("255.255.255.0");
                        var broadcast = CalculateBroadcastAddress(unicast.Address, mask);

                        result.Add(new NetworkAdapterInfo
                        {
                            Name = iface.Name,
                            Address = unicast.Address,
                            SubnetMask = mask,
                            BroadcastAddress = broadcast,
                            InterfaceType = iface.NetworkInterfaceType
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Adapter Enumeration Error] {ex.Message}");
        }

        return result;
    }

    private static IPAddress CalculateBroadcastAddress(IPAddress address, IPAddress subnetMask)
    {
        byte[] ipBytes = address.GetAddressBytes();
        byte[] maskBytes = subnetMask.GetAddressBytes();
        byte[] broadcastBytes = new byte[ipBytes.Length];

        for (int i = 0; i < ipBytes.Length; i++)
        {
            broadcastBytes[i] = (byte)(ipBytes[i] | ~maskBytes[i]);
        }

        return new IPAddress(broadcastBytes);
    }
}
