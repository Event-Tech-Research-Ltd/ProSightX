using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;

namespace ProSight;

/// <summary>
/// macOS (Apple Silicon and Intel). Capture: tshark if Wireshark's ChmodBPF lets this user read /dev/bpf*, otherwise the
/// built-in /usr/sbin/tcpdump run as root through the standard admin-password prompt (osascript), only when Listen runs.
/// Adapter names come from `networksetup -listallhardwareports`, because .NET reports Wi-Fi as Ethernet on macOS.
/// </summary>
public sealed partial class MacNetPlatform : INetPlatform
{
    // BSD interfaces that are never a physical RJ45 port.
    static readonly string[] VirtualPrefixes = ["lo", "gif", "stf", "bridge", "awdl", "llw", "utun", "ap", "anpi", "vmenet", "ipsec", "ppp"];
    static readonly string[] VirtualPorts = ["thunderbolt bridge", "bluetooth", "vpn", "iphone usb", "ipad usb"];

    readonly Lazy<Dictionary<string, string>> _ports = new(LoadHardwarePorts);

    public string Name => "macOS";
    public bool ListenNeedsElevatedProcess => false; // asks for the admin password itself, per capture

    public List<NetworkInterface> WiredNics()
    {
        var ports = _ports.Value;
        return [.. NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet)
            .Where(n => !IsWifi(n, ports))
            .Where(n => !VirtualPrefixes.Any(p => n.Name.StartsWith(p, StringComparison.Ordinal) && n.Name.Length > p.Length && char.IsAsciiDigit(n.Name[p.Length])))
            .OrderBy(n => IsLikelyVirtual(n) ? 1 : 0)
            .ThenBy(n => n.OperationalStatus == OperationalStatus.Up ? 0 : 1)
            .ThenBy(n => n.Name, StringComparer.Ordinal)];
    }

    public string FriendlyName(NetworkInterface nic) =>
        _ports.Value.TryGetValue(nic.Name, out var port) ? $"{port} ({nic.Name})" : nic.Name;

    public bool IsLikelyVirtual(NetworkInterface nic)
    {
        if (!_ports.Value.TryGetValue(nic.Name, out var port)) return true; // not a hardware port macOS knows about
        var p = port.ToLowerInvariant();
        return VirtualPorts.Any(p.Contains);
    }

    static bool IsWifi(NetworkInterface n, Dictionary<string, string> ports) =>
        n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
        (ports.TryGetValue(n.Name, out var port) && (port.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) || port.Contains("AirPort", StringComparison.OrdinalIgnoreCase)));

    /// <summary>BSD device (en7) → hardware port name ("USB 10/100/1000 LAN"). Empty if networksetup is unavailable.</summary>
    static Dictionary<string, string> LoadHardwarePorts()
    {
        var (code, output) = ProcessRunner.Run("/usr/sbin/networksetup", TimeSpan.FromSeconds(10), "-listallhardwareports");
        return code == 0 ? ParseHardwarePorts(output) : new Dictionary<string, string>(StringComparer.Ordinal);
    }

    internal static Dictionary<string, string> ParseHardwarePorts(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        string? port = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Hardware Port:", StringComparison.Ordinal)) port = line["Hardware Port:".Length..].Trim();
            else if (line.StartsWith("Device:", StringComparison.Ordinal) && port is not null)
            {
                var dev = line["Device:".Length..].Trim();
                if (dev.Length > 0) map[dev] = port;
                port = null;
            }
        }
        return map;
    }

    // ---------- capture ----------

    public string? Capture(NetworkInterface nic, int seconds, string capturePath, List<string> diag)
    {
        if (Tshark(nic, seconds, capturePath, diag)) return "tshark";
        if (Tcpdump(nic, seconds, capturePath, diag)) return "tcpdump";
        return null;
    }

    static string? TsharkPath()
    {
        foreach (var c in new[] { "/Applications/Wireshark.app/Contents/MacOS/tshark", "/opt/homebrew/bin/tshark", "/usr/local/bin/tshark" })
            if (File.Exists(c)) return c;
        return ProcessRunner.Which("tshark");
    }

    /// <summary>Works without a password only when Wireshark's ChmodBPF has granted this user access to /dev/bpf*.</summary>
    static bool Tshark(NetworkInterface nic, int seconds, string pcap, List<string> diag)
    {
        var exe = TsharkPath();
        if (exe is null) return false;
        var (code, output) = ProcessRunner.Run(exe, TimeSpan.FromSeconds(seconds + 30),
            "-i", nic.Name, "-c", "1", "-a", $"duration:{seconds}", "-w", pcap, "-f", ProcessRunner.DiscoveryFilter, "-q");
        if (code == 0 && File.Exists(pcap)) return true;
        diag.Add($"tshark -i {nic.Name}: exit {code}. {ProcessRunner.Tail(output)}" +
                 (output.Contains("ermission", StringComparison.Ordinal) ? " (ChmodBPF not installed — falling back to tcpdump with your admin password)" : ""));
        return false;
    }

    /// <summary>
    /// Runs tcpdump as root. tcpdump has no duration flag, so the root shell starts a timer that stops it; the timer
    /// lives inside that root shell, so a capture always ends even if ProSight itself is killed while waiting.
    /// The timer sends SIGTERM, not SIGINT: a non-interactive shell starts background jobs with SIGINT ignored (POSIX),
    /// so `kill -INT` would never stop a capture that hears nothing.
    /// </summary>
    static bool Tcpdump(NetworkInterface nic, int seconds, string pcap, List<string> diag)
    {
        const string tcpdump = "/usr/sbin/tcpdump";
        if (!File.Exists(tcpdump)) { diag.Add("tcpdump: /usr/sbin/tcpdump not found."); return false; }
        if (!IsSafeInterfaceName(nic.Name)) { diag.Add($"tcpdump: refusing unexpected interface name '{nic.Name}'."); return false; }
        var log = Path.ChangeExtension(pcap, ".log");
        var script = BuildTcpdumpScript(tcpdump, nic.Name, seconds, pcap, log);

        (int Code, string Output) r = Environment.IsPrivilegedProcess
            ? ProcessRunner.Run("/bin/sh", TimeSpan.FromSeconds(seconds + 30), "-c", script)
            : ProcessRunner.Run("/usr/bin/osascript", TimeSpan.FromSeconds(seconds + 180), // + time to type the password
                "-e", BuildAdminAppleScript(script, "ProSight needs administrator access to listen for switch announcements (LLDP/CDP) on " + nic.Name + "."));

        string tcpdumpLog = "";
        try { if (File.Exists(log)) tcpdumpLog = File.ReadAllText(log); } catch (IOException) { }
        if (r.Output.Contains("-128", StringComparison.Ordinal) || r.Output.Contains("User canceled", StringComparison.OrdinalIgnoreCase))
        {
            diag.Add("tcpdump: cancelled at the password prompt.");
            return false;
        }
        if (r.Code != 0) diag.Add($"tcpdump: exit {r.Code}. {ProcessRunner.Tail(r.Output)}");
        if (!File.Exists(pcap)) { diag.Add($"tcpdump wrote no capture. {ProcessRunner.Tail(tcpdumpLog)}"); return false; }
        return true;
    }

    // BSD interface names are short [a-z]+[0-9]+; anything else is refused before it reaches a root shell.
    internal static bool IsSafeInterfaceName(string name) => SafeIfName().IsMatch(name);

    [GeneratedRegex("^[a-z]{1,8}[0-9]{1,4}$")]
    private static partial Regex SafeIfName();

    internal static string ShellQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    internal static string BuildTcpdumpScript(string tcpdump, string iface, int seconds, string pcap, string log) =>
        $"{tcpdump} -i {ShellQuote(iface)} -c 1 -U -w {ShellQuote(pcap)} {ShellQuote(ProcessRunner.DiscoveryFilter)} >{ShellQuote(log)} 2>&1 & P=$!; " +
        $"( sleep {seconds}; kill -TERM $P ) >/dev/null 2>&1 & W=$!; " +
        "wait $P; kill $W >/dev/null 2>&1; " +
        $"chmod 644 {ShellQuote(pcap)} {ShellQuote(log)} >/dev/null 2>&1; exit 0";

    /// <summary>AppleScript string literal: backslash and double quote are the only characters that need escaping.</summary>
    internal static string AppleScriptString(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    internal static string BuildAdminAppleScript(string shellScript, string prompt) =>
        $"do shell script {AppleScriptString(shellScript)} with prompt {AppleScriptString(prompt)} with administrator privileges";

    // ---------- ARP ----------

    public List<ArpEntry> ArpTable()
    {
        var (code, output) = ProcessRunner.Run("/usr/sbin/arp", TimeSpan.FromSeconds(10), "-an");
        return code == 0 ? ParseArp(output) : [];
    }

    // "? (10.40.0.12) at 0:1d:c1:2:3:4 on en7 ifscope [ethernet]"
    [GeneratedRegex(@"\((\d{1,3}(?:\.\d{1,3}){3})\) at ([0-9a-fA-F]{1,2}(?::[0-9a-fA-F]{1,2}){5}) on (\S+)(.*)$")]
    private static partial Regex ArpLine();

    /// <summary>
    /// macOS doesn't expose neighbour state through `arp`, so entries are reported unconfirmed; the scanner treats an
    /// entry as live only if it appeared (or changed) during the scan. Own addresses ("permanent") and incomplete
    /// entries are skipped.
    /// </summary>
    internal static List<ArpEntry> ParseArp(string text)
    {
        var list = new List<ArpEntry>();
        foreach (var raw in text.Split('\n'))
        {
            var m = ArpLine().Match(raw.Trim());
            if (!m.Success || m.Groups[4].Value.Contains("permanent", StringComparison.Ordinal)) continue;
            if (!IPAddress.TryParse(m.Groups[1].Value, out var ip)) continue;
            var octets = m.Groups[2].Value.Split(':').Select(o => byte.Parse(o, NumberStyles.HexNumber)).ToArray();
            if (octets.All(b => b == 0xFF) || octets.All(b => b == 0) || (octets[0] & 0x01) != 0) continue; // broadcast/empty/multicast
            list.Add(new ArpEntry(ip, string.Join(":", octets.Select(b => b.ToString("X2"))), ConfirmedReachable: false));
        }
        return list;
    }

    // macOS exposes multicast *packet* counts (ifi_imcasts) but not multicast byte counts, so Flood in shows n/a.
    public long? FloodInOctets(NetworkInterface nic) => null;
}
