using System.Net.NetworkInformation;

namespace ProSight;

/// <summary>Windows: tshark (Npcap) or built-in pktmon for capture; iphlpapi for ARP state and flood counters.</summary>
public sealed class WindowsNetPlatform : INetPlatform
{
    // Name/description fragments of adapters that report as Ethernet but are not a physical RJ45 port.
    static readonly string[] VirtualHints =
        ["virtual", "hyper-v", "vethernet", "vmware", "virtualbox", "bluetooth", "tap-", "wan miniport",
         "vpn", "loopback", "wintun", "wireguard", "tailscale", "zerotier", "npcap"];

    static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    public string Name => "Windows";
    public bool ListenNeedsElevatedProcess => true;

    public List<NetworkInterface> WiredNics() =>
        [.. NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet)
            .Where(n => n.OperationalStatus != OperationalStatus.NotPresent)
            .OrderBy(n => IsLikelyVirtual(n) ? 1 : 0)
            .ThenBy(n => n.OperationalStatus == OperationalStatus.Up ? 0 : 1)
            .ThenBy(n => n.Name)];

    public string FriendlyName(NetworkInterface nic) => nic.Name;

    public bool IsLikelyVirtual(NetworkInterface n)
    {
        var s = (n.Name + " " + n.Description).ToLowerInvariant();
        return VirtualHints.Any(s.Contains);
    }

    public string? Capture(NetworkInterface nic, int seconds, string capturePath, List<string> diag)
    {
        if (Tshark(nic, seconds, capturePath, diag)) return "tshark";
        if (Pktmon(seconds, Path.ChangeExtension(capturePath, ".etl"), capturePath, diag)) return "pktmon";
        return null;
    }

    public List<ArpEntry> ArpTable() =>
        [.. WindowsNative.ReachableIPv4Neighbors().Select(n => new ArpEntry(n.Address, n.Mac, ConfirmedReachable: true))];

    public long? FloodInOctets(NetworkInterface nic) =>
        WindowsNative.IndexOf(nic) is { } idx ? WindowsNative.InterfaceCounters(idx)?.InFloodOctets : null;

    static string? TsharkPath()
    {
        foreach (var c in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Wireshark", "tshark.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Wireshark", "tshark.exe"),
        })
            if (File.Exists(c)) return c;
        return ProcessRunner.Which("tshark");
    }

    static bool Tshark(NetworkInterface nic, int seconds, string pcap, List<string> diag)
    {
        var exe = TsharkPath();
        if (exe is null) return false;
        // Npcap names adapters \Device\NPF_{GUID}; NetworkInterface.Id is that GUID on Windows, so this is exact.
        // Fall back to the friendly name for older WinPcap-style installs.
        foreach (var iface in new[] { $@"\Device\NPF_{nic.Id}", nic.Name })
        {
            // -c 1: stop at the first LLDP/CDP frame (the filter admits nothing else); -a duration is the upper bound.
            var (code, output) = ProcessRunner.Run(exe, TimeSpan.FromSeconds(seconds + 30),
                "-i", iface, "-c", "1", "-a", $"duration:{seconds}", "-w", pcap, "-f", ProcessRunner.DiscoveryFilter, "-q");
            if (code == 0 && File.Exists(pcap)) return true;
            diag.Add($"tshark -i {iface}: exit {code}. {ProcessRunner.Tail(output)}");
        }
        return false;
    }

    static int Run(params string[] args) => ProcessRunner.Run("pktmon", CommandTimeout, args).Code;

    static bool Pktmon(int seconds, string etl, string pcap, List<string> diag)
    {
        if (ProcessRunner.Which("pktmon") is null) { diag.Add("pktmon: not found (needs Windows 10 1809+)."); return false; }
        Run("stop");
        Run("filter", "remove");
        // -d = data-link protocol (EtherType, decimal: 0x88CC = 35020), -m = MAC address.
        // If any filter is rejected, capture unfiltered instead: a partial filter set could exclude LLDP/CDP,
        // while an unfiltered capture is still decoded correctly (the decoder ignores everything else).
        bool filtersOk =
            Run("filter", "add", "ProSight-LLDP", "-d", "35020") == 0 &
            Run("filter", "add", "ProSight-CDP", "-m", "01-00-0C-CC-CC-CC") == 0 &
            Run("filter", "add", "ProSight-LLDPMAC", "-m", "01-80-C2-00-00-0E") == 0;
        if (!filtersOk)
        {
            Run("filter", "remove");
            diag.Add("pktmon: filter syntax rejected on this Windows build; captured unfiltered.");
        }
        try
        {
            var (code, output) = ProcessRunner.Run("pktmon", CommandTimeout, "start", "--capture", "--comp", "nics", "--pkt-size", "0", "--file-name", etl);
            if (code != 0) diag.Add($"pktmon start: exit {code}. {ProcessRunner.Tail(output)}");
            Thread.Sleep(seconds * 1000);
        }
        finally
        {
            // Always stop: a left-running pktmon session keeps capturing system-wide.
            Run("stop");
        }
        if (File.Exists(etl))
        {
            var (code, output) = ProcessRunner.Run("pktmon", TimeSpan.FromSeconds(120), "etl2pcap", etl, "--out", pcap);
            if (code != 0) diag.Add($"pktmon etl2pcap: exit {code}. {ProcessRunner.Tail(output)}");
        }
        Run("filter", "remove");
        return File.Exists(pcap);
    }
}
