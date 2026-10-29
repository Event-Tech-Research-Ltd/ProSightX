using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ProSight;

/// <summary>RttMs is null when the host was only seen via ARP (it answered nothing we sent at L3/L4).</summary>
public sealed record HostResult(string Ip, double? RttMs, string Method, string Mac, string Vendor, string Hostname);

public readonly record struct ScanProgress(int Percent, string Phase);

/// <summary>Small, dependency-free, subnet-scoped host discovery helper.</summary>
public static class HostScanner
{
    static readonly int[] TcpPorts = [80, 443, 22, 445, 3389, 8080];

    public static string DefaultRange(NetworkInterface nic)
    {
        var ua = PrimaryIPv4(nic);
        if (ua is null) return "192.168.1.0/24";
        // Use the NIC's real prefix when it fits the 1,024-address cap; otherwise scan the /24 around this host.
        int prefix;
        try { prefix = ua.PrefixLength; } catch (PlatformNotSupportedException) { prefix = 24; }
        if (prefix is < 22 or > 30) prefix = 24;
        uint mask = uint.MaxValue << (32 - prefix);
        return $"{FromUInt(ToUInt(ua.Address) & mask)}/{prefix}";
    }

    public static UnicastIPAddressInformation? PrimaryIPv4(NetworkInterface nic) =>
        nic.GetIPProperties().UnicastAddresses
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
            .OrderBy(a => a.Address.GetAddressBytes() is [169, 254, ..] ? 1 : 0) // prefer non-APIPA
            .FirstOrDefault();

    public static IReadOnlyList<IPAddress> Expand(string range)
    {
        range = range.Trim();
        if (range.Contains('/', StringComparison.Ordinal))
        {
            var p = range.Split('/', 2);
            if (!TryParseDottedQuad(p[0], out var address) ||
                !int.TryParse(p[1], out var prefix) || prefix is < 1 or > 30)
                throw new ArgumentException("Use IPv4 CIDR such as 192.168.1.0/24.");
            uint ip = ToUInt(address);
            uint mask = uint.MaxValue << (32 - prefix);
            uint first = (ip & mask) + 1;
            uint last = ((ip & mask) | ~mask) - 1; // exclude the broadcast address
            return Range(first, last);
        }

        var dash = range.IndexOf('-');
        if (dash < 1) throw new ArgumentException("Use CIDR (192.168.1.0/24) or a short range (10.0.0.1-50).");
        var left = range[..dash].Trim();
        var right = range[(dash + 1)..].Trim();
        if (!TryParseDottedQuad(left, out var start))
            throw new ArgumentException("The first address is not a valid IPv4 address (a.b.c.d).");
        IPAddress end;
        // "10.0.0.1-50": a bare number is the last octet. (IPAddress.TryParse("50") succeeds as 0.0.0.50 — the old
        // code took that path, so the README's own example was always rejected as "end before start".)
        if (right.Contains('.'))
        {
            if (!TryParseDottedQuad(right, out end)) throw new ArgumentException("The range end is not a valid IPv4 address.");
        }
        else
        {
            if (!byte.TryParse(right, out var lastOctet)) throw new ArgumentException("The range end must be 0-255 or a full address.");
            var b = start.GetAddressBytes(); b[3] = lastOctet; end = new IPAddress(b);
        }
        var a = ToUInt(start); var z = ToUInt(end);
        if (z < a) throw new ArgumentException("The range end must be after the first address.");
        return Range(a, z);
    }

    /// <summary>
    /// Phase 1 (0-90 %): ICMP then all TCP ports in parallel per host. Phase 2 (90-99 %): merge reachable ARP
    /// neighbours (on-link hosts that answer nothing still answer ARP), then MAC vendor + reverse DNS for found hosts.
    /// </summary>
    public static async Task<List<HostResult>> ScanAsync(IReadOnlyList<IPAddress> addresses, NetworkInterface? nic, IProgress<ScanProgress>? progress, CancellationToken token)
    {
        if (addresses.Count > 1024) throw new ArgumentException("Limit scans to 1,024 addresses or fewer.");
        int total = Math.Max(1, addresses.Count), done = 0;
        var onLink = nic is null ? null : OnLinkNetwork(nic);
        var found = new System.Collections.Concurrent.ConcurrentDictionary<string, HostResult>();
        // Snapshot the neighbour cache first: on macOS `arp` can't say whether an entry is fresh, so an entry only counts
        // as evidence of life if it appeared or changed while we probed.
        var arpBefore = NetPlatform.Current.ArpTable()
            .GroupBy(e => e.Address.ToString()).ToDictionary(g => g.Key, g => g.First().Mac);
        await Parallel.ForEachAsync(addresses, new ParallelOptions { MaxDegreeOfParallelism = 64, CancellationToken = token }, async (address, ct) =>
        {
            bool local = onLink is { } n && (ToUInt(address) & n.Mask) == n.Network;
            var result = await ProbeAsync(address, local, ct);
            if (result is not null) found[result.Ip] = result;
            int d = Interlocked.Increment(ref done);
            progress?.Report(new ScanProgress(d * 90 / total, $"Probed {d}/{addresses.Count}"));
        }).ConfigureAwait(false); // stay off the UI thread: the ARP read and first OUI-file load below are blocking work

        progress?.Report(new ScanProgress(90, "Checking ARP neighbours"));
        var wanted = new HashSet<string>(addresses.Select(a => a.ToString()));
        foreach (var nb in NetPlatform.Current.ArpTable())
        {
            var ip = nb.Address.ToString();
            if (!wanted.Contains(ip)) continue;
            bool fresh = nb.ConfirmedReachable || !arpBefore.TryGetValue(ip, out var oldMac) || oldMac != nb.Mac;
            found.AddOrUpdate(ip,
                _ => new HostResult(ip, null, fresh ? "arp" : "arp (cached)", nb.Mac, "", ""),
                (_, existing) => existing with { Mac = nb.Mac });
        }

        progress?.Report(new ScanProgress(93, $"Resolving names for {found.Count} host(s)"));
        var results = found.Values.Select(r => r.Mac.Length > 0 ? r with { Vendor = Oui.Lookup(r.Mac) } : r).ToList();
        using var gate = new SemaphoreSlim(32);
        var named = await Task.WhenAll(results.Select(async r =>
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try { return r with { Hostname = await Hostname(IPAddress.Parse(r.Ip), token).ConfigureAwait(false) }; }
            finally { gate.Release(); }
        })).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return [.. named.OrderBy(x => ToUInt(IPAddress.Parse(x.Ip)))];
    }

    static async Task<HostResult?> ProbeAsync(IPAddress address, bool local, CancellationToken token)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, local ? 300 : 800);
            if (reply.Status == IPStatus.Success)
                return new HostResult(address.ToString(), reply.RoundtripTime, "icmp", "", "", "");
        }
        catch (PingException) { }
        token.ThrowIfCancellationRequested();

        // All ports at once: a dead address now costs one timeout instead of six in a row.
        using var window = CancellationTokenSource.CreateLinkedTokenSource(token);
        window.CancelAfter(local ? 400 : 1000);
        var clients = TcpPorts.Select(_ => new TcpClient(AddressFamily.InterNetwork)).ToArray();
        try
        {
            var attempts = TcpPorts.Select((port, i) => AttemptAsync(clients[i], address, port, window.Token)).ToList();
            while (attempts.Count > 0)
            {
                var t = await Task.WhenAny(attempts);
                attempts.Remove(t);
                if (await t) return new HostResult(address.ToString(), stopwatch.Elapsed.TotalMilliseconds, "tcp", "", "", "");
            }
        }
        finally { foreach (var c in clients) c.Dispose(); }
        token.ThrowIfCancellationRequested();
        return null;
    }

    /// <summary>True if the host answered at all: accepted, or actively refused (a TCP RST still proves it's alive).</summary>
    static async Task<bool> AttemptAsync(TcpClient client, IPAddress address, int port, CancellationToken ct)
    {
        try { await client.ConnectAsync(address, port, ct); return true; }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused) { return true; }
        catch (SocketException) { return false; }
        catch (OperationCanceledException) { return false; }
        catch (ObjectDisposedException) { return false; }
    }

    static async Task<string> Hostname(IPAddress address, CancellationToken token)
    {
        try
        {
            var entry = await Dns.GetHostEntryAsync(address).WaitAsync(TimeSpan.FromMilliseconds(800), token);
            return entry.HostName == address.ToString() ? "" : entry.HostName;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return ""; }
    }

    static (uint Network, uint Mask)? OnLinkNetwork(NetworkInterface nic)
    {
        try
        {
            var ua = PrimaryIPv4(nic);
            if (ua is null) return null;
            int prefix = ua.PrefixLength;
            if (prefix is < 1 or > 32) return null;
            uint mask = uint.MaxValue << (32 - prefix);
            return (ToUInt(ua.Address) & mask, mask);
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException) { return null; }
    }

    static IReadOnlyList<IPAddress> Range(uint first, uint last)
    {
        if ((ulong)last - first + 1 > 1024) throw new ArgumentException("Limit scans to 1,024 addresses or fewer.");
        var list = new List<IPAddress>();
        for (var i = first; i <= last; i++) { list.Add(FromUInt(i)); if (i == uint.MaxValue) break; }
        return list;
    }

    /// <summary>Strict a.b.c.d only. IPAddress.TryParse also accepts legacy forms ("50", "10.1", hex/octal parts).</summary>
    static bool TryParseDottedQuad(string s, out IPAddress address)
    {
        address = IPAddress.None;
        var parts = s.Trim().Split('.');
        if (parts.Length != 4) return false;
        var bytes = new byte[4];
        for (int i = 0; i < 4; i++)
        {
            var part = parts[i];
            if (part.Length is < 1 or > 3 || !part.All(char.IsAsciiDigit) || !byte.TryParse(part, out bytes[i])) return false;
        }
        address = new IPAddress(bytes);
        return true;
    }

    static uint ToUInt(IPAddress address) { var b = address.GetAddressBytes(); return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3]; }
    static IPAddress FromUInt(uint value) => new([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
}
