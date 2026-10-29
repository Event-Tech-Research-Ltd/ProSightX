using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;

namespace ProSight;

/// <summary>Note is non-empty when the result needs a caveat (e.g. pktmon saw discovery on several adapters).</summary>
public sealed record ListenResult(string Backend, int FrameCount, List<Neighbor> Neighbors, string Note);

/// <summary>Front door used by both UIs. OS-specific work is delegated to <see cref="NetPlatform.Current"/>.</summary>
public static class CaptureEngine
{
    /// <summary>Elevated on Windows / running as root on macOS.</summary>
    public static bool IsAdmin() => Environment.IsPrivilegedProcess;

    public static List<NetworkInterface> EthernetNics() => NetPlatform.Current.WiredNics();

    public static bool IsLikelyVirtual(NetworkInterface n) => NetPlatform.Current.IsLikelyVirtual(n);

    public static string DisplayName(NetworkInterface n)
    {
        var mac = string.Join(":", n.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));
        long speed = 0;
        try { speed = n.Speed; } catch (PlatformNotSupportedException) { }
        var speedText = speed > 0 ? $"{speed / 1_000_000} Mbps" : "?";
        return $"{NetPlatform.Current.FriendlyName(n)}  |  {n.OperationalStatus}  |  {speedText}  |  {mac}";
    }

    /// <summary>Windows only: restart this app elevated. Returns false if the user declined UAC or the OS isn't Windows.</summary>
    public static bool RelaunchElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        var exe = Environment.ProcessPath;
        if (exe is null) return false;
        try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" }); return true; }
        catch (Win32Exception) { return false; }
    }

    public static ListenResult Listen(NetworkInterface nic, int seconds)
    {
        var work = Path.Combine(Path.GetTempPath(), "prosight-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var capture = Path.Combine(work, "capture.pcapng"); // either format; Pcap.Extract sniffs the magic number
            var diag = new List<string>();
            var backend = NetPlatform.Current.Capture(nic, seconds, capture, diag)
                ?? throw new InvalidOperationException(
                    $"No capture backend worked on {NetPlatform.Current.Name}." +
                    (diag.Count > 0 ? Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, diag) : ""));

            var frames = File.Exists(capture) ? Pcap.Extract(capture) : [];
            var decoded = frames
                .Select(f => (Frame: f, Neighbor: Decoder.Decode(f.Data)))
                .Where(x => x.Neighbor is not null)
                .ToList();

            string note = "";
            var interfaces = decoded.Select(x => x.Frame.InterfaceId).Distinct().ToList();
            if (interfaces.Count > 1)
            {
                // A capture that spans several NICs (pktmon) — only trust frames attributable to the selected one.
                var mine = decoded.Where(x => MatchesNic(x.Frame.InterfaceName, nic)).ToList();
                if (mine.Count > 0)
                {
                    note = $"{backend} saw LLDP/CDP on {interfaces.Count} adapters; showing only frames from {nic.Name}.";
                    decoded = mine;
                }
                else
                {
                    note = $"WARNING: {backend} saw LLDP/CDP on {interfaces.Count} different adapters and could not tell which one is " +
                           $"{nic.Name}. This result may describe another adapter's switch port (e.g. a dock). " +
                           "Install Wireshark (tshark) for per-adapter capture, or unplug other wired adapters and listen again.";
                }
            }
            if (diag.Count > 0 && note.Length == 0) note = string.Join(Environment.NewLine, diag);
            return new ListenResult(backend, frames.Count, decoded.Select(x => x.Neighbor!).ToList(), note);
        }
        finally
        {
            // Captures can contain other hosts' traffic; never leave them in the temp folder.
            try { Directory.Delete(work, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    static bool MatchesNic(string captureName, NetworkInterface nic) =>
        captureName.Length > 0 &&
        new[] { nic.Description, nic.Name, nic.Id }.Any(k => k.Length > 0 && captureName.Contains(k, StringComparison.OrdinalIgnoreCase));
}
