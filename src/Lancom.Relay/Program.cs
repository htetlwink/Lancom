using System.Net;
using System.Net.Sockets;

namespace Lancom.Relay;

internal class Program
{
    private static async Task Main(string[] args)
    {
        Console.Title = "Lancom Relay - Corporate AP Isolation Bridge";
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
============================================================
   LANCOM - DEDICATED RELAY SERVER      
   Corporate Wi-Fi & AP Isolation Bypass Bridge             
============================================================");
        Console.ResetColor();

        int port = 37733;
        if (args.Length > 0 && int.TryParse(args[0], out int customPort))
        {
            port = customPort;
        }

        PrintLocalAddresses(port);

        using var relay = new RelayServer(port);
        relay.Start();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[READY] Relay Server listening on UDP port {port}");
        Console.WriteLine("[INFO] Office laptops can connect by setting Relay IP in Lancom settings.");
        Console.WriteLine("Press Ctrl+C to exit.\n");
        Console.ResetColor();

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        // Live dashboard loop
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (!cts.IsCancellationRequested && await timer.WaitForNextTickAsync(cts.Token))
        {
            double mbForwarded = relay.BytesForwarded / (1024.0 * 1024.0);
            Console.Write($"\r[STATUS] Connected Peers: {relay.ActiveClientCount,2} | Packets Forwarded: {relay.PacketsForwarded,6} | Data: {mbForwarded:F2} MB   ");
        }

        Console.WriteLine("\n[SHUTDOWN] Stopping relay server...");
        relay.Stop();
    }

    private static void PrintLocalAddresses(int port)
    {
        Console.WriteLine("[NETWORK INTERFACES]");
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                {
                    Console.WriteLine($"  -> IP: {ip}:{port}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Failed to query network interfaces: {ex.Message}");
        }
        Console.WriteLine();
    }
}
