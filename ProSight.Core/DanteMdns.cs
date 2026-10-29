using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ProSight;

public sealed class DanteDevice
{
    public string Name { get; set; } = "";
    public string Ip { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Model { get; set; } = "";
    public string Extra { get; set; } = "";
}

public static class DanteMdns
{
    static readonly IPAddress Mdns = IPAddress.Parse("224.0.0.251");
    const int Port = 5353;
    // ARC = control/device service; CMC = Dante Controller/monitoring service. Some devices answer one more reliably.
    static readonly string[] Services = ["_netaudio-arc._udp.local", "_netaudio-cmc._udp.local"];
    const int QueryRepeats = 3, QueryIntervalMs = 1000; // mDNS replies are jittered up to ~500 ms; repeats cover loss

    public static List<DanteDevice> LabFoh() =>
    [
        new() { Name = "FOH-CL5", Ip = "10.40.0.10", Manufacturer = "Yamaha", Model = "CL5", Extra = "96 kHz  64×64  Master" },
        new() { Name = "FOH-RIO3224-A", Ip = "10.40.0.11", Manufacturer = "Yamaha", Model = "Rio3224-D2", Extra = "96 kHz  32×16  Slave" },
        new() { Name = "FOH-RIO3224-B", Ip = "10.40.0.12", Manufacturer = "Yamaha", Model = "Rio3224-D2", Extra = "96 kHz  32×16  Slave" },
        new() { Name = "ULXD-RACK-A", Ip = "10.40.0.21", Manufacturer = "Shure", Model = "ULXD4Q", Extra = "48 kHz  4×0  Slave" },
        new() { Name = "REDNET-MP8R", Ip = "10.40.0.30", Manufacturer = "Focusrite", Model = "RedNet MP8R", Extra = "96 kHz  8×8  Slave" },
        new() { Name = "AMP-CXQ-4K8", Ip = "10.40.0.40", Manufacturer = "QSC", Model = "CX-Q 4K8", Extra = "48 kHz  0×8  Slave" },
        new() { Name = "AVIO-USB-1", Ip = "10.40.0.50", Manufacturer = "Audinate", Model = "AVIO USB", Extra = "48 kHz  2×2  Slave" },
    ];

    public static string Format(IEnumerable<DanteDevice> devices, string backend, string nic)
    {
        var list = devices.ToList();
        var lines = new List<string>
        {
            "ProSight Dante search",
            $"NIC         {nic}",
            $"Backend     {backend}",
            $"Query       PTR {string.Join(" + ", Services)}  ×{QueryRepeats}",
            "Multicast   224.0.0.251:5353",
            $"Devices     {list.Count}",
            "",
        };
        if (list.Count == 0)
        {
            lines.Add("No Dante devices on this NIC.");
            lines.Add("Put the laptop on the Dante VLAN (same L2). mDNS does not cross routers.");
            return string.Join(Environment.NewLine, lines);
        }
        foreach (var d in list)
        {
            lines.Add($"{d.Name,-18}  {d.Ip,-14}  {d.Manufacturer} {d.Model}");
            if (!string.IsNullOrWhiteSpace(d.Extra))
                lines.Add($"                  {d.Extra}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Browses Dante ARC services. <paramref name="localInterface"/> pins the multicast join/send to the selected NIC;
    /// without it the OS default route is used (often Wi-Fi on a multi-homed laptop).
    /// </summary>
    public static List<DanteDevice> Browse(IPAddress? localInterface, int milliseconds = 3200, CancellationToken token = default)
    {
        var acc = new Records();
        var query = BuildPtrQuery(Services);
        using var udp = OpenMdnsSocket();
        if (localInterface is not null)
        {
            udp.JoinMulticastGroup(Mdns, localInterface);
            udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, localInterface.GetAddressBytes());
        }
        else udp.JoinMulticastGroup(Mdns);
        udp.Client.ReceiveTimeout = 200;
        var group = new IPEndPoint(Mdns, Port);
        var started = DateTime.UtcNow;
        var deadline = started.AddMilliseconds(milliseconds);
        var remote = new IPEndPoint(IPAddress.Any, 0);
        int sent = 0;
        while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
        {
            if (sent < QueryRepeats && DateTime.UtcNow >= started.AddMilliseconds(sent * QueryIntervalMs))
            {
                udp.Send(query, query.Length, group);
                sent++;
            }
            byte[] data;
            try { data = udp.Receive(ref remote); }
            catch (SocketException) { continue; } // timeout / ICMP reset — keep waiting until deadline
            try { Parse(data, acc); }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or FormatException)
            {
                // Malformed packet from the LAN: skip it, never abort the browse.
            }
        }
        return acc.Build();
    }

    /// <summary>
    /// Preferred: share UDP 5353 with the OS responder, which also lets us hear unsolicited announcements. macOS's
    /// mDNSResponder binds with SO_REUSEPORT, so we set it too. Fallback: an ephemeral port — RFC 6762 §6.7 responders
    /// then answer by unicast to that port ("legacy unicast"), which needs no port sharing at all.
    /// </summary>
    static UdpClient OpenMdnsSocket()
    {
        var udp = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            if (OperatingSystem.IsMacOS())
                udp.Client.SetRawSocketOption(0xFFFF /* SOL_SOCKET */, 0x0200 /* SO_REUSEPORT */, BitConverter.GetBytes(1));
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
            return udp;
        }
        catch (SocketException)
        {
            udp.Dispose();
            return new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        }
    }

    /// <summary>Self-test hook: runs captured datagrams through the same parse + join as <see cref="Browse"/>.</summary>
    internal static List<DanteDevice> ParseDatagrams(IEnumerable<byte[]> datagrams)
    {
        var acc = new Records();
        foreach (var d in datagrams)
        {
            try { Parse(d, acc); }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or FormatException) { }
        }
        return acc.Build();
    }

    /// <summary>Raw mDNS records; joined only for instances actually advertised under _netaudio.</summary>
    sealed class Records
    {
        public readonly HashSet<string> Instances = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> SrvTarget = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> HostIp = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, List<KeyValuePair<string, string>>> Txt = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>One row per device name, merged across its ARC/CMC instances.</summary>
        public List<DanteDevice> Build()
        {
            var list = new List<DanteDevice>();
            foreach (var instances in Instances.GroupBy(Instance, StringComparer.OrdinalIgnoreCase))
            {
                var d = new DanteDevice { Name = instances.Key };
                var extras = new List<string>();
                foreach (var fqdn in instances)
                {
                    if (d.Ip.Length == 0 && SrvTarget.TryGetValue(fqdn, out var host) && HostIp.TryGetValue(host.TrimEnd('.'), out var ip)) d.Ip = ip;
                    if (!Txt.TryGetValue(fqdn, out var pairs)) continue;
                    foreach (var (k, v) in pairs)
                    {
                        if (k is "mf" or "manufacturer") { if (d.Manufacturer.Length == 0) d.Manufacturer = v; }
                        else if (k is "model" or "md") { if (d.Model.Length == 0) d.Model = v; }
                        else if (v.Length <= 40) extras.Add($"{k}={v}"); // shown as advertised; not interpreted
                    }
                }
                if (d.Ip.Length == 0 && HostIp.TryGetValue(instances.Key + ".local", out var guess)) d.Ip = guess;
                var extra = string.Join("  ", extras.Distinct(StringComparer.OrdinalIgnoreCase));
                d.Extra = extra.Length > 140 ? extra[..140] + "…" : extra;
                list.Add(d);
            }
            return [.. list.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)];
        }
    }

    static byte[] BuildPtrQuery(IReadOnlyList<string> names)
    {
        var ms = new MemoryStream();
        void u16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        u16(0);            // id
        u16(0);            // flags: standard query
        u16(names.Count);  // QDCOUNT
        u16(0);
        u16(0);
        u16(0);
        foreach (var name in names)
        {
            foreach (var label in name.Split('.'))
            {
                var b = Encoding.ASCII.GetBytes(label);
                ms.WriteByte((byte)b.Length);
                ms.Write(b);
            }
            ms.WriteByte(0);
            u16(12); // PTR
            u16(1);  // IN
        }
        return ms.ToArray();
    }

    static void Parse(byte[] data, Records acc)
    {
        if (data.Length < 12) return;
        int qd = (data[4] << 8) | data[5];
        int an = (data[6] << 8) | data[7];
        int ns = (data[8] << 8) | data[9];
        int ar = (data[10] << 8) | data[11];
        int o = 12;
        for (int i = 0; i < qd && o < data.Length; i++) SkipName(data, ref o);
        int total = an + ns + ar;
        for (int i = 0; i < total && o < data.Length; i++)
        {
            string owner = ReadName(data, ref o);
            if (o + 10 > data.Length) break;
            int type = (data[o] << 8) | data[o + 1];
            o += 2;
            o += 2; // class (incl. cache-flush bit)
            o += 4; // ttl
            int rdlen = (data[o] << 8) | data[o + 1];
            o += 2;
            if (o + rdlen > data.Length) break;
            int start = o;
            switch (type)
            {
                // PTR: only the services we query. Other _netaudio-* PTRs seen in unsolicited announcements (e.g.
                // per-channel services) would otherwise turn every channel into a "device" row.
                case 12 when Services.Contains(owner.TrimEnd('.'), StringComparer.OrdinalIgnoreCase):
                {
                    int tmp = o;
                    var target = ReadName(data, ref tmp);
                    if (target.Length > 0) acc.Instances.Add(target);
                    break;
                }
                case 33 when rdlen > 6: // SRV: priority, weight, port, target
                {
                    int tmp = o + 6;
                    var target = ReadName(data, ref tmp);
                    if (target.Length > 0) acc.SrvTarget[owner] = target;
                    break;
                }
                case 1 when rdlen == 4: // A
                    acc.HostIp[owner] = $"{data[o]}.{data[o + 1]}.{data[o + 2]}.{data[o + 3]}";
                    break;
                case 16: // TXT
                {
                    if (!acc.Txt.TryGetValue(owner, out var pairs)) acc.Txt[owner] = pairs = [];
                    ParseTxt(data, o, rdlen, pairs);
                    break;
                }
            }
            o = start + rdlen;
        }
    }

    static void ParseTxt(byte[] data, int o, int rdlen, List<KeyValuePair<string, string>> pairs)
    {
        int end = o + rdlen;
        while (o < end)
        {
            int len = data[o++];
            if (len <= 0 || o + len > end) break;
            var s = Encoding.ASCII.GetString(data, o, len);
            o += len;
            int eq = s.IndexOf('=');
            if (eq <= 0) continue;
            var k = s[..eq].ToLowerInvariant();
            var v = s[(eq + 1)..];
            if (!pairs.Any(x => x.Key == k)) pairs.Add(new(k, v)); // repeated announcements carry the same record
        }
    }

    static string Instance(string fqdn)
    {
        var cut = fqdn.IndexOf("._", StringComparison.Ordinal);
        return cut > 0 ? fqdn[..cut] : fqdn.Replace(".local", "", StringComparison.OrdinalIgnoreCase).Trim('.');
    }

    static void SkipName(byte[] data, ref int o)
    {
        ReadName(data, ref o);
        o += 4; // type + class
    }

    /// <summary>
    /// Reads a (possibly compressed) DNS name. Compression pointers must point strictly backwards (RFC 1035 §4.1.4),
    /// which makes pointer loops impossible; the old version recursed on self-pointers until StackOverflow — an
    /// uncatchable process crash any LAN host could trigger with one packet.
    /// </summary>
    static string ReadName(byte[] data, ref int o)
    {
        var parts = new List<string>();
        int pos = o;
        bool jumped = false;
        int hops = 0;
        while (pos < data.Length)
        {
            int len = data[pos];
            if (len == 0) { pos++; break; }
            if ((len & 0xC0) == 0xC0)
            {
                if (pos + 1 >= data.Length) { pos = data.Length; break; }
                int ptr = ((len & 0x3F) << 8) | data[pos + 1];
                if (!jumped) { o = pos + 2; jumped = true; }
                if (ptr >= pos || ++hops > 32) return string.Join('.', parts); // forward/self pointer = malformed
                pos = ptr;
                continue;
            }
            if ((len & 0xC0) != 0 || pos + 1 + len > data.Length) { pos = data.Length; break; } // reserved label types / truncated
            parts.Add(Encoding.ASCII.GetString(data, pos + 1, len));
            pos += 1 + len;
            if (parts.Count > 127) break;
        }
        if (!jumped) o = pos;
        return string.Join('.', parts);
    }
}
