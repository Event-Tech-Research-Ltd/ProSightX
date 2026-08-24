using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Principal;

namespace PortSight;

public sealed record ListenResult(string Backend, int FrameCount, List<Neighbor> Neighbors);

public static class CaptureEngine
{
    public static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static List<NetworkInterface> EthernetNics() =>
        [.. NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet
                or NetworkInterfaceType.GigabitEthernet)
            .Where(n => n.OperationalStatus != OperationalStatus.NotPresent)
            .OrderBy(n => n.Name)];

    public static string DisplayName(NetworkInterface n)
    {
        var mac = string.Join(":", n.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));
        var speed = n.Speed > 0 ? $"{n.Speed / 1_000_000} Mbps" : "?";
        return $"{n.Name}  |  {n.OperationalStatus}  |  {speed}  |  {mac}";
    }

    public static ListenResult Listen(string adapterName, int seconds)
    {
        var work = Path.Combine(Path.GetTempPath(), "portsight-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var pcap = Path.Combine(work, "capture.pcapng");
        var etl = Path.Combine(work, "capture.etl");

        string backend;
        if (Tshark(adapterName, seconds, pcap)) backend = "tshark";
        else if (Pktmon(seconds, etl, pcap)) backend = "pktmon";
        else
            throw new InvalidOperationException(
                "No capture backend. Install Wireshark (tshark) or use Windows 10 1809+ (pktmon) and run as Administrator.");

        var frames = File.Exists(pcap) ? Pcap.Extract(pcap) : [];
        var neighbors = frames
            .Select(Decoder.Decode)
            .Where(n => n is not null)
            .Cast<Neighbor>()
            .ToList();
        return new ListenResult(backend, frames.Count, neighbors);
    }

    static string? TsharkPath()
    {
        foreach (var c in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Wireshark", "tshark.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Wireshark", "tshark.exe"),
        })
            if (File.Exists(c)) return c;
        return Which("tshark");
    }

    static bool Tshark(string name, int seconds, string pcap)
    {
        var exe = TsharkPath();
        if (exe is null) return false;
        var filter = "ether proto 0x88cc or ether dst 01:00:0c:cc:cc:cc or ether dst 01:80:c2:00:00:0e";
        var p = Run(exe, $"-i \"{name}\" -a duration:{seconds} -w \"{pcap}\" -f \"{filter}\" -q");
        return p == 0 && File.Exists(pcap);
    }

    static bool Pktmon(int seconds, string etl, string pcap)
    {
        if (Which("pktmon") is null) return false;
        Run("pktmon", "stop", ignore: true);
        Run("pktmon", "filter remove", ignore: true);
        Run("pktmon", "filter add -e 0x88CC", ignore: true);
        Run("pktmon", "filter add -d 01-00-0C-CC-CC-CC", ignore: true);
        Run("pktmon", "filter add -d 01-80-C2-00-00-0E", ignore: true);
        Run("pktmon", $"start --capture --comp nics --pkt-size 0 --file-name \"{etl}\"", ignore: true);
        Thread.Sleep(seconds * 1000);
        Run("pktmon", "stop", ignore: true);
        if (File.Exists(etl))
        {
            Run("pktmon", $"etl2pcap \"{etl}\" -o \"{pcap}\"", ignore: true);
            if (!File.Exists(pcap))
                Run("pktmon", $"etl2pcap \"{etl}\" --out \"{pcap}\"", ignore: true);
        }
        Run("pktmon", "filter remove", ignore: true);
        return File.Exists(pcap);
    }

    static string? Which(string name)
    {
        var ext = Path.GetExtension(name).Length == 0 ? ".exe" : "";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var p = Path.Combine(dir, name + ext);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    static int Run(string file, string args, bool ignore = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return -1;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch
        {
            if (ignore) return -1;
            throw;
        }
    }
}
