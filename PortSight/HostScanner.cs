using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PortSight;

public sealed record HostResult(string Ip, double RttMs, string Method, string Hostname);

/// <summary>Small, dependency-free, subnet-scoped host discovery helper.</summary>
public static class HostScanner
{
    static readonly int[] TcpPorts = [80, 443, 22, 445, 3389, 8080];

    public static string DefaultRange(NetworkInterface nic)
    {
        var address = nic.GetIPProperties().UnicastAddresses
            .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))?.Address;
        if (address is null) return "192.168.1.0/24";
        var b = address.GetAddressBytes();
        return $"{b[0]}.{b[1]}.{b[2]}.0/24";
    }

    public static IReadOnlyList<IPAddress> Expand(string range)
    {
        range = range.Trim();
        if (range.Contains('/', StringComparison.Ordinal))
        {
            var p = range.Split('/', 2);
            if (!IPAddress.TryParse(p[0], out var address) || address.AddressFamily != AddressFamily.InterNetwork ||
                !int.TryParse(p[1], out var prefix) || prefix is < 1 or > 30)
                throw new ArgumentException("Use IPv4 CIDR such as 192.168.1.0/24.");
            uint ip = ToUInt(address);
            uint mask = uint.MaxValue << (32 - prefix);
            uint first = (ip & mask) + 1;
            uint last = (ip & mask) | ~mask;
            return Range(first, last);
        }

        var dash = range.IndexOf('-');
        if (dash < 1) throw new ArgumentException("Use CIDR (192.168.1.0/24) or a short range (10.0.0.1-50).");
        var left = range[..dash].Trim();
        var right = range[(dash + 1)..].Trim();
        if (!IPAddress.TryParse(left, out var start) || start.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("The first address is not a valid IPv4 address.");
        IPAddress end;
        if (!IPAddress.TryParse(right, out end!))
        {
            if (!byte.TryParse(right, out var lastOctet)) throw new ArgumentException("The range end is not valid.");
            var b = start.GetAddressBytes(); b[3] = lastOctet; end = new IPAddress(b);
        }
        var a = ToUInt(start); var z = ToUInt(end);
        if (z < a) throw new ArgumentException("The range end must be after the first address.");
        return Range(a, z);
    }

    public static async Task<List<HostResult>> ScanAsync(string range, IProgress<int>? progress, CancellationToken token)
    {
        var addresses = Expand(range);
        if (addresses.Count > 1024) throw new ArgumentException("Limit scans to 1,024 addresses or fewer.");
        int done = 0;
        var found = new System.Collections.Concurrent.ConcurrentBag<HostResult>();
        await Parallel.ForEachAsync(addresses, new ParallelOptions { MaxDegreeOfParallelism = 64, CancellationToken = token }, async (address, ct) =>
        {
            var result = await ProbeAsync(address, ct);
            if (result is not null) found.Add(result);
            progress?.Report(Interlocked.Increment(ref done));
        });
        return found.OrderBy(x => ToUInt(IPAddress.Parse(x.Ip))).ToList();
    }

    static async Task<HostResult?> ProbeAsync(IPAddress address, CancellationToken token)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, 550);
            if (reply.Status == IPStatus.Success)
                return new HostResult(address.ToString(), reply.RoundtripTime, "icmp", await Hostname(address, token));
        }
        catch (PingException) { }

        foreach (var port in TcpPorts)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(address, port, token).AsTask().WaitAsync(TimeSpan.FromMilliseconds(350), token);
                return new HostResult(address.ToString(), stopwatch.Elapsed.TotalMilliseconds, "tcp", await Hostname(address, token));
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
            {
                // A TCP reset/refusal is still a definite response from a live IP.
                return new HostResult(address.ToString(), stopwatch.Elapsed.TotalMilliseconds, "tcp", await Hostname(address, token));
            }
            catch (SocketException) { }
            catch (TimeoutException) { }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        }
        return null;
    }

    static async Task<string> Hostname(IPAddress address, CancellationToken token)
    {
        try
        {
            var entry = await Dns.GetHostEntryAsync(address).WaitAsync(TimeSpan.FromMilliseconds(800), token);
            return entry.HostName;
        }
        catch { return ""; }
    }

    static IReadOnlyList<IPAddress> Range(uint first, uint last)
    {
        if ((ulong)last - first + 1 > 1024) throw new ArgumentException("Limit scans to 1,024 addresses or fewer.");
        var list = new List<IPAddress>();
        for (var i = first; i <= last; i++) { list.Add(FromUInt(i)); if (i == uint.MaxValue) break; }
        return list;
    }

    static uint ToUInt(IPAddress address) { var b = address.GetAddressBytes(); return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3]; }
    static IPAddress FromUInt(uint value) => new([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
}
