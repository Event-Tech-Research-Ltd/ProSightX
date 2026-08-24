using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PortSight;

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
    const string Service = "_netaudio-arc._udp.local";

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
            "PortSight Dante search",
            $"NIC         {nic}",
            $"Backend     {backend}",
            "Query       PTR _netaudio-arc._udp.local",
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

    public static List<DanteDevice> Browse(int milliseconds = 2500)
    {
        var found = new Dictionary<string, DanteDevice>(StringComparer.OrdinalIgnoreCase);
        var query = BuildPtrQuery(Service);
        using var udp = new UdpClient();
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
        udp.JoinMulticastGroup(Mdns);
        udp.Client.ReceiveTimeout = 200;
        udp.Send(query, query.Length, new IPEndPoint(Mdns, Port));

        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        var remote = new IPEndPoint(IPAddress.Any, 0);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var data = udp.Receive(ref remote);
                Parse(data, found);
            }
            catch (SocketException)
            {
                // timeout — keep waiting until deadline
            }
        }
        return [.. found.Values.OrderBy(d => d.Name)];
    }

    static byte[] BuildPtrQuery(string name)
    {
        var ms = new MemoryStream();
        void u16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        u16(0);
        u16(0);
        u16(1);
        u16(0);
        u16(0);
        u16(0);
        foreach (var label in name.Split('.'))
        {
            var b = Encoding.ASCII.GetBytes(label);
            ms.WriteByte((byte)b.Length);
            ms.Write(b);
        }
        ms.WriteByte(0);
        u16(12); // PTR
        u16(1);  // IN
        return ms.ToArray();
    }

    static void Parse(byte[] data, Dictionary<string, DanteDevice> found)
    {
        if (data.Length < 12) return;
        int qd = (data[4] << 8) | data[5];
        int an = (data[6] << 8) | data[7];
        int ns = (data[8] << 8) | data[9];
        int ar = (data[10] << 8) | data[11];
        int o = 12;
        for (int i = 0; i < qd; i++) SkipName(data, ref o);
        int total = an + ns + ar;
        for (int i = 0; i < total && o + 10 <= data.Length; i++)
        {
            string owner = ReadName(data, ref o);
            if (o + 10 > data.Length) break;
            int type = (data[o] << 8) | data[o + 1];
            o += 2;
            o += 2; // class
            o += 4; // ttl
            int rdlen = (data[o] << 8) | data[o + 1];
            o += 2;
            if (o + rdlen > data.Length) break;
            int start = o;
            if (type == 12) // PTR
            {
                int tmp = o;
                string target = ReadName(data, ref tmp);
                var inst = Instance(target);
                if (owner.Contains("_netaudio", StringComparison.OrdinalIgnoreCase) ||
                    target.Contains("_netaudio", StringComparison.OrdinalIgnoreCase))
                {
                    var d = Get(found, inst);
                    if (string.IsNullOrEmpty(d.Name)) d.Name = inst;
                }
            }
            else if (type == 1 && rdlen >= 4) // A
            {
                var ip = $"{data[o]}.{data[o + 1]}.{data[o + 2]}.{data[o + 3]}";
                var d = Get(found, Instance(owner));
                if (string.IsNullOrEmpty(d.Ip)) d.Ip = ip;
            }
            else if (type == 16) // TXT
            {
                var d = Get(found, Instance(owner));
                ParseTxt(data, o, rdlen, d);
            }
            o = start + rdlen;
        }
    }

    static void ParseTxt(byte[] data, int o, int rdlen, DanteDevice d)
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
            if (k is "mf" or "manufacturer") d.Manufacturer = v;
            else if (k is "model" or "md") d.Model = v;
        }
    }

    static DanteDevice Get(Dictionary<string, DanteDevice> found, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) name = "(unknown)";
        if (!found.TryGetValue(name, out var d))
        {
            d = new DanteDevice { Name = name };
            found[name] = d;
        }
        return d;
    }

    static string Instance(string fqdn)
    {
        var cut = fqdn.IndexOf("._", StringComparison.Ordinal);
        return cut > 0 ? fqdn[..cut] : fqdn.Replace(".local", "", StringComparison.OrdinalIgnoreCase).Trim('.');
    }

    static void SkipName(byte[] data, ref int o)
    {
        ReadName(data, ref o);
        if (o + 4 <= data.Length) o += 4; // type + class
    }

    static string ReadName(byte[] data, ref int o)
    {
        var parts = new List<string>();
        int jumped = 0;
        int guard = 0;
        while (o < data.Length && guard++ < 32)
        {
            int len = data[o];
            if (len == 0) { o++; break; }
            if ((len & 0xC0) == 0xC0)
            {
                if (o + 1 >= data.Length) break;
                int ptr = ((len & 0x3F) << 8) | data[o + 1];
                o += 2;
                if (jumped++ == 0) { /* stay */ }
                int save = o;
                o = ptr;
                var rest = ReadName(data, ref o);
                if (rest.Length > 0) parts.Add(rest);
                o = save;
                break;
            }
            o++;
            if (o + len > data.Length) break;
            parts.Add(Encoding.ASCII.GetString(data, o, len));
            o += len;
        }
        return string.Join('.', parts);
    }
}
