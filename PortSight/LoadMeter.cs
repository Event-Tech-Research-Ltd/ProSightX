using System.Net.NetworkInformation;

namespace PortSight;

public sealed class LoadReading
{
    public string Adapter { get; init; } = "";
    public long LinkBps { get; init; }
    public double MbpsIn { get; init; }
    public double MbpsOut { get; init; }
    public double UtilPct { get; init; }
    public double Pps { get; init; }
    public double MulticastPct { get; init; }
    public double ErrorsPerSec { get; init; }
    public double DiscardsPerSec { get; init; }
    public string Duplex { get; init; } = "";
    public string Verdict { get; init; } = "";

    public string Format()
    {
        var link = LinkBps > 0 ? $"{LinkBps / 1_000_000} Mbps" : "?";
        return string.Join(Environment.NewLine, new[]
        {
            "PortSight load",
            $"NIC         {Adapter}",
            $"Link        {link}  {Duplex}",
            $"Utilisation {UtilPct:0.0} %",
            $"In          {MbpsIn:0.00} Mbps",
            $"Out         {MbpsOut:0.00} Mbps",
            $"Packets     {Pps:0} /s",
            $"Multicast   {MulticastPct:0} % of packets",
            $"Errors      {ErrorsPerSec:0.0} /s",
            $"Discards    {DiscardsPerSec:0.0} /s",
            $"Verdict     {Verdict}",
            "",
            "This is the laptop NIC on this access port (unicast to you + flood/multicast on the VLAN), not the switch uplink.",
        });
    }
}

public static class LoadMeter
{
    public static LoadReading Sample(NetworkInterface nic, int seconds)
    {
        seconds = Math.Clamp(seconds, 1, 30);
        var a = Take(nic);
        Thread.Sleep(seconds * 1000);
        var b = Take(nic);
        double dt = Math.Max(0.5, seconds);
        double inB = Math.Max(0, b.BytesIn - a.BytesIn) / dt;
        double outB = Math.Max(0, b.BytesOut - a.BytesOut) / dt;
        double pkts = Math.Max(0, b.Pkts - a.Pkts) / dt;
        double non = Math.Max(0, b.NonUnicast - a.NonUnicast) / dt;
        double err = Math.Max(0, b.Errors - a.Errors) / dt;
        double disc = Math.Max(0, b.Discards - a.Discards) / dt;
        double link = nic.Speed > 0 ? nic.Speed : 1_000_000_000;
        double util = Math.Min(100, (inB + outB) * 8 / link * 100);
        string verdict =
            err >= 5 || disc >= 20 ? "Fault" :
            util >= 90 ? "Saturated" :
            util >= 50 ? "Busy" :
            util < 2 ? "Idle" : "Healthy";
        return new LoadReading
        {
            Adapter = nic.Name,
            LinkBps = nic.Speed,
            MbpsIn = inB * 8 / 1_000_000,
            MbpsOut = outB * 8 / 1_000_000,
            UtilPct = util,
            Pps = pkts,
            MulticastPct = pkts > 0 ? non / pkts * 100 : 0,
            ErrorsPerSec = err,
            DiscardsPerSec = disc,
            Duplex = nic.OperationalStatus.ToString(),
            Verdict = verdict,
        };
    }

    record Snap(long BytesIn, long BytesOut, long Pkts, long NonUnicast, long Errors, long Discards);

    static Snap Take(NetworkInterface nic)
    {
        try
        {
            var s = nic.GetIPStatistics();
            long v4err = 0;
            try { v4err = nic.GetIPv4Statistics().IncomingPacketsWithErrors; } catch { }
            return new Snap(
                s.BytesReceived,
                s.BytesSent,
                s.UnicastPacketsReceived + s.NonUnicastPacketsReceived + s.UnicastPacketsSent + s.NonUnicastPacketsSent,
                s.NonUnicastPacketsReceived + s.NonUnicastPacketsSent,
                s.IncomingPacketsWithErrors + s.OutgoingPacketsWithErrors + v4err,
                s.IncomingPacketsDiscarded + s.OutgoingPacketsDiscarded);
        }
        catch (NetworkInformationException)
        {
            return new Snap(0, 0, 0, 0, 0, 0);
        }
    }
}
