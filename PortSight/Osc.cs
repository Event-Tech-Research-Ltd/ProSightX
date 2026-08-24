using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PortSight;

public sealed class OscMessage
{
    public string Address { get; set; } = "/";
    public List<object> Args { get; } = [];

    public string Format()
    {
        var parts = Args.Select(a => a switch
        {
            float f => f.ToString("0.###"),
            int i => i.ToString(),
            _ => a.ToString() ?? "",
        });
        var tail = string.Join("  ", parts);
        return string.IsNullOrEmpty(tail) ? Address : $"{Address}  {tail}";
    }
}

public static class Osc
{
    public static byte[] Encode(OscMessage msg)
    {
        var parts = new List<byte[]> { OscString(msg.Address) };
        var tags = new StringBuilder(",");
        var payloads = new List<byte[]>();
        foreach (var a in msg.Args)
        {
            if (a is int i) { tags.Append('i'); payloads.Add(BeInt(i)); }
            else if (a is float f) { tags.Append('f'); payloads.Add(BeFloat(f)); }
            else { tags.Append('s'); payloads.Add(OscString(a.ToString() ?? "")); }
        }
        parts.Add(OscString(tags.ToString()));
        parts.AddRange(payloads);
        return Concat(parts);
    }

    public static OscMessage Decode(byte[] bytes)
    {
        int o = 0;
        var address = ReadString(bytes, ref o);
        var tags = ReadString(bytes, ref o);
        var msg = new OscMessage { Address = address };
        if (tags.Length == 0 || tags[0] != ',') return msg;
        foreach (var t in tags.AsSpan(1))
        {
            if (t == 'i') { msg.Args.Add(ReadInt(bytes, ref o)); }
            else if (t == 'f') { msg.Args.Add(ReadFloat(bytes, ref o)); }
            else if (t == 's') { msg.Args.Add(ReadString(bytes, ref o)); }
        }
        return msg;
    }

    public static OscMessage GyroSample()
    {
        var m = new OscMessage { Address = "/gyrosc/gyro" };
        m.Args.Add(0.12f);
        m.Args.Add(-0.48f);
        m.Args.Add(0.91f);
        return m;
    }

    public static OscMessage FreqSample()
    {
        var m = new OscMessage { Address = "/voices/3/osc/14/freq" };
        m.Args.Add(440f);
        return m;
    }

    public static void Send(string host, int port, OscMessage msg)
    {
        using var udp = new UdpClient();
        var bytes = Encode(msg);
        udp.Send(bytes, bytes.Length, host, port);
    }

    public static UdpClient Bind(int port)
    {
        var udp = new UdpClient(port);
        udp.Client.ReceiveTimeout = 500;
        return udp;
    }

    static byte[] OscString(string s)
    {
        var raw = Encoding.ASCII.GetBytes(s);
        int n = raw.Length + 1;
        int padded = n + ((4 - (n % 4)) % 4);
        var o = new byte[padded];
        Buffer.BlockCopy(raw, 0, o, 0, raw.Length);
        return o;
    }

    static string ReadString(byte[] b, ref int o)
    {
        int end = o;
        while (end < b.Length && b[end] != 0) end++;
        var s = Encoding.ASCII.GetString(b, o, Math.Max(0, end - o));
        int n = end - o + 1;
        int padded = n + ((4 - (n % 4)) % 4);
        o += padded;
        return s;
    }

    static int ReadInt(byte[] b, ref int o)
    {
        int v = (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
        o += 4;
        return v;
    }

    static float ReadFloat(byte[] b, ref int o)
    {
        if (BitConverter.IsLittleEndian)
        {
            var tmp = new[] { b[o + 3], b[o + 2], b[o + 1], b[o] };
            o += 4;
            return BitConverter.ToSingle(tmp, 0);
        }
        var v = BitConverter.ToSingle(b, o);
        o += 4;
        return v;
    }

    static byte[] BeInt(int v) =>
        [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    static byte[] BeFloat(float v)
    {
        var raw = BitConverter.GetBytes(v);
        if (BitConverter.IsLittleEndian) Array.Reverse(raw);
        return raw;
    }

    static byte[] Concat(List<byte[]> parts)
    {
        var n = parts.Sum(p => p.Length);
        var o = new byte[n];
        int i = 0;
        foreach (var p in parts) { Buffer.BlockCopy(p, 0, o, i, p.Length); i += p.Length; }
        return o;
    }
}
