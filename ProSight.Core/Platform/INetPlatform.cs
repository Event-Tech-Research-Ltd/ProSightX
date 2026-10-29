using System.Net;
using System.Net.NetworkInformation;

namespace ProSight;

/// <summary>An IPv4 → MAC neighbour. ConfirmedReachable = the OS says it answered recently (not just a cached entry).</summary>
public sealed record ArpEntry(IPAddress Address, string Mac, bool ConfirmedReachable);

/// <summary>Everything ProSight needs that differs per operating system. Parsers and decoders stay shared.</summary>
public interface INetPlatform
{
    string Name { get; }

    /// <summary>True when Listen only works if the whole process is already elevated (Windows: relaunch as admin).
    /// False when the platform asks for credentials itself at capture time (macOS: admin-password prompt).</summary>
    bool ListenNeedsElevatedProcess { get; }

    /// <summary>Wired, Ethernet-like adapters, physical and connected first.</summary>
    List<NetworkInterface> WiredNics();

    /// <summary>What the user calls the adapter ("Ethernet", "USB 10/100/1000 LAN (en7)").</summary>
    string FriendlyName(NetworkInterface nic);

    bool IsLikelyVirtual(NetworkInterface nic);

    /// <summary>Captures LLDP/CDP frames on <paramref name="nic"/> into <paramref name="capturePath"/> (pcap or pcapng).
    /// Returns the backend used, or null if every backend failed (reasons appended to <paramref name="diag"/>).</summary>
    string? Capture(NetworkInterface nic, int seconds, string capturePath, List<string> diag);

    /// <summary>Current IPv4 neighbour (ARP) table. Never throws; empty when unavailable.</summary>
    List<ArpEntry> ArpTable();

    /// <summary>Cumulative multicast+broadcast bytes received, or null when the OS doesn't expose it.</summary>
    long? FloodInOctets(NetworkInterface nic);
}

public static class NetPlatform
{
    public static INetPlatform Current { get; } =
        OperatingSystem.IsWindows() ? new WindowsNetPlatform() :
        OperatingSystem.IsMacOS() ? new MacNetPlatform() :
        new GenericNetPlatform();
}
