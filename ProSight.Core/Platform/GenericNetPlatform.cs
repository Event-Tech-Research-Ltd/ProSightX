using System.Net.NetworkInformation;

namespace ProSight;

/// <summary>Other Unix (e.g. Linux): tshark only, no ARP or flood data. Keeps shared code runnable; not a supported target.</summary>
public sealed class GenericNetPlatform : INetPlatform
{
    public string Name => "Unix";
    public bool ListenNeedsElevatedProcess => true;

    public List<NetworkInterface> WiredNics() =>
        [.. NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet)
            .OrderBy(n => n.OperationalStatus == OperationalStatus.Up ? 0 : 1).ThenBy(n => n.Name)];

    public string FriendlyName(NetworkInterface nic) => nic.Name;
    public bool IsLikelyVirtual(NetworkInterface nic) => nic.Name.StartsWith("veth", StringComparison.Ordinal) || nic.Name.StartsWith("docker", StringComparison.Ordinal) || nic.Name.StartsWith("br-", StringComparison.Ordinal);

    public string? Capture(NetworkInterface nic, int seconds, string capturePath, List<string> diag)
    {
        var exe = ProcessRunner.Which("tshark");
        if (exe is null) { diag.Add("tshark not found."); return null; }
        var (code, output) = ProcessRunner.Run(exe, TimeSpan.FromSeconds(seconds + 30),
            "-i", nic.Name, "-c", "1", "-a", $"duration:{seconds}", "-w", capturePath, "-f", ProcessRunner.DiscoveryFilter, "-q");
        if (code == 0 && File.Exists(capturePath)) return "tshark";
        diag.Add($"tshark: exit {code}. {ProcessRunner.Tail(output)}");
        return null;
    }

    public List<ArpEntry> ArpTable() => [];
    public long? FloodInOctets(NetworkInterface nic) => null;
}
