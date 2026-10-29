using System.Diagnostics;
using System.Net.NetworkInformation;

namespace ProSight;

public sealed class LoadReading
{
    public string Adapter { get; init; } = "";
    public long LinkBps { get; init; }
    public double MbpsIn { get; init; }
    public double MbpsOut { get; init; }
    public double UtilPct { get; init; }
    public double Pps { get; init; }
    public double MulticastPct { get; init; }
    /// <summary>Multicast + broadcast receive rate. Null when the OS counters are unavailable or fail their self-check.</summary>
    public double? FloodMbpsIn { get; init; }
    public double ErrorsPerSec { get; init; }
    public double DiscardsPerSec { get; init; }
    public string Status { get; init; } = "";
    public string Verdict { get; init; } = "";
    /// <summary>Cabling/link advice independent of load, e.g. a gigabit drop that negotiated 100 Mbps.</summary>
    public string Advisory { get; init; } = "";

    public string Format()
    {
        var link = LinkBps > 0 ? $"{LinkBps / 1_000_000} Mbps" : "?";
        var lines = new List<string>
        {
            "ProSight load",
            $"NIC         {Adapter}",
            $"Link        {link}  ({Status})",
            $"Utilisation {UtilPct:0.0} %",
            $"In          {MbpsIn:0.00} Mbps",
            $"Out         {MbpsOut:0.00} Mbps",
            $"Flood in    {(FloodMbpsIn is { } f ? $"{f:0.00} Mbps multicast+broadcast" : "n/a")}",
            $"Packets     {Pps:0} /s",
            $"Multicast   {MulticastPct:0} % of packets",
            $"Errors      {ErrorsPerSec:0.0} /s",
            $"Discards    {DiscardsPerSec:0.0} /s",
            $"Verdict     {Verdict}",
        };
        if (Advisory.Length > 0) lines.Add($"Advice      {Advisory}");
        lines.Add("");
        lines.Add("This is the laptop NIC on this access port (unicast to you + flood/multicast on the VLAN), not the switch uplink.");
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>Point-in-time NIC counters. Two snapshots of the same NIC make a reading.</summary>
public sealed record LoadSnapshot(string NicId, long Timestamp, long BytesIn, long BytesOut, long Pkts, long NonUnicast,
    long Errors, long Discards, long? FloodIn);

public static class LoadMeter
{
    const long Gigabit = 1_000_000_000;

    /// <summary>One-shot reading over <paramref name="seconds"/>.</summary>
    public static LoadReading Sample(NetworkInterface nic, int seconds)
    {
        seconds = Math.Clamp(seconds, 1, 30);
        var a = Snapshot(nic);
        Thread.Sleep(seconds * 1000);
        return Compute(nic, a, Snapshot(nic));
    }

    public static LoadSnapshot Snapshot(NetworkInterface nic)
    {
        long ts = Stopwatch.GetTimestamp();
        long? flood = NetPlatform.Current.FloodInOctets(nic);
        try
        {
            var s = nic.GetIPStatistics();
            // Some counters throw PlatformNotSupportedException on macOS; a missing counter reads as 0 rather than
            // failing the whole reading.
            static long Safe(Func<long> read) { try { return read(); } catch (PlatformNotSupportedException) { return 0; } }
            return new LoadSnapshot(nic.Id, ts,
                Safe(() => s.BytesReceived),
                Safe(() => s.BytesSent),
                Safe(() => s.UnicastPacketsReceived) + Safe(() => s.NonUnicastPacketsReceived) + Safe(() => s.UnicastPacketsSent) + Safe(() => s.NonUnicastPacketsSent),
                Safe(() => s.NonUnicastPacketsReceived) + Safe(() => s.NonUnicastPacketsSent),
                Safe(() => s.IncomingPacketsWithErrors) + Safe(() => s.OutgoingPacketsWithErrors),
                Safe(() => s.IncomingPacketsDiscarded) + Safe(() => s.OutgoingPacketsDiscarded),
                flood);
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            return new LoadSnapshot(nic.Id, ts, 0, 0, 0, 0, 0, 0, null);
        }
    }

    /// <summary>Rates between two consecutive snapshots — continuous when called with each tick's previous snapshot.</summary>
    public static LoadReading Compute(NetworkInterface nic, LoadSnapshot a, LoadSnapshot b)
    {
        double dt = Math.Max(0.2, Stopwatch.GetElapsedTime(a.Timestamp, b.Timestamp).TotalSeconds);
        double rate(long x, long y) => Math.Max(0, y - x) / dt; // counter reset/wrap → 0, never negative
        double inB = rate(a.BytesIn, b.BytesIn), outB = rate(a.BytesOut, b.BytesOut);
        double pkts = rate(a.Pkts, b.Pkts), non = rate(a.NonUnicast, b.NonUnicast);
        double err = rate(a.Errors, b.Errors), disc = rate(a.Discards, b.Discards);
        double? flood = a.FloodIn is { } fa && b.FloodIn is { } fb ? rate(fa, fb) * 8 / 1_000_000 : null;

        // NetworkInterface objects snapshot Speed/OperationalStatus at enumeration; re-query so renegotiation/unplug shows.
        var live = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(x => x.Id == nic.Id) ?? nic;
        long speed = 0;
        try { speed = live.Speed; } catch (PlatformNotSupportedException) { }
        double link = speed > 0 ? speed : Gigabit;
        double util = Math.Min(100, (inB + outB) * 8 / link * 100);
        bool up = live.OperationalStatus == OperationalStatus.Up;

        // A healthy copper link shows zero errors, so any error is worth flagging. Discards can be normal at a low rate.
        string verdict =
            !up ? "Link down" :
            err > 0 ? "Errors on link — check cable/patch/port" :
            disc >= 20 ? "High discards" :
            util >= 90 ? "Saturated" :
            util >= 50 ? "Busy" :
            util < 2 ? "Idle" : "Healthy";

        return new LoadReading
        {
            Adapter = live.Name,
            LinkBps = speed,
            MbpsIn = inB * 8 / 1_000_000,
            MbpsOut = outB * 8 / 1_000_000,
            UtilPct = util,
            Pps = pkts,
            MulticastPct = pkts > 0 ? non / pkts * 100 : 0,
            FloodMbpsIn = flood,
            ErrorsPerSec = err,
            DiscardsPerSec = disc,
            Status = live.OperationalStatus.ToString(),
            Verdict = verdict,
            Advisory = LinkAdvisory(speed, up),
        };
    }

    static string LinkAdvisory(long speed, bool up)
    {
        if (!up || speed <= 0) return "";
        if (speed < Gigabit && speed >= 10_000_000)
            return $"Link negotiated {speed / 1_000_000} Mbps. If this drop and switch port are gigabit, suspect a broken/" +
                   "unterminated pair (1000BASE-T needs all 4 pairs; 100BASE-TX runs on 2), a damaged patch lead, or a port speed limit.";
        return "";
    }
}
