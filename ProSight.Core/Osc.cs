using System.Net.Sockets;
using System.Text;

namespace ProSight;

public sealed class OscMessage
{
    public string Address { get; set; } = "/";
    public List<object> Args { get; } = [];

    public string Format()
    {
        var parts = Args.Select(a => a switch
        {
            float f => f.ToString("0.###"),
            double d => d.ToString("0.###"),
            int i => i.ToString(),
            long l => l.ToString(),
            bool b => b ? "true" : "false",
            null => "nil",
            byte[] blob => $"<blob {blob.Length} B>",
            _ => a.ToString() ?? "",
        });
        var tail = string.Join("  ", parts);
        return string.IsNullOrEmpty(tail) ? Address : $"{Address}  {tail}";
    }
}

public static class Osc
{
    const int MaxBundleDepth = 8;

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

    /// <summary>Decodes a UDP datagram (message or #bundle). Throws <see cref="FormatException"/> on malformed input.</summary>
    public static List<OscMessage> DecodePacket(byte[] bytes)
    {
        var list = new List<OscMessage>();
        DecodeInto(bytes, 0, bytes.Length, list, 0);
        return list;
    }

    /// <summary>Decodes a single OSC message. Throws <see cref="FormatException"/> on malformed input.</summary>
    public static OscMessage Decode(byte[] bytes) => DecodeMessage(bytes, 0, bytes.Length);

    static void DecodeInto(byte[] b, int start, int end, List<OscMessage> output, int depth)
    {
        if (depth > MaxBundleDepth) throw new FormatException("OSC bundle nesting too deep.");
        if (end - start >= 16 && b[start] == (byte)'#')
        {
            int o = start;
            if (ReadString(b, ref o, end) != "#bundle") throw new FormatException("Unknown '#' packet.");
            o += 8; // NTP time tag
            if (o > end) throw new FormatException("Truncated OSC bundle.");
            while (o < end)
            {
                int size = ReadInt(b, ref o, end);
                if (size <= 0 || size % 4 != 0 || size > end - o) throw new FormatException("Bad OSC bundle element size.");
                DecodeInto(b, o, o + size, output, depth + 1);
                o += size;
            }
            return;
        }
        output.Add(DecodeMessage(b, start, end));
    }

    static OscMessage DecodeMessage(byte[] b, int start, int end)
    {
        int o = start;
        var address = ReadString(b, ref o, end);
        if (!address.StartsWith('/')) throw new FormatException("OSC address must start with '/'.");
        var msg = new OscMessage { Address = address };
        if (o >= end) return msg; // old-style message without type tags
        var tags = ReadString(b, ref o, end);
        if (tags.Length == 0 || tags[0] != ',') return msg;
        foreach (var t in tags.AsSpan(1))
        {
            switch (t)
            {
                case 'i': msg.Args.Add(ReadInt(b, ref o, end)); break;
                case 'f': msg.Args.Add(BitConverter.Int32BitsToSingle(ReadInt(b, ref o, end))); break;
                case 's' or 'S': msg.Args.Add(ReadString(b, ref o, end)); break;
                case 'h': msg.Args.Add(ReadLong(b, ref o, end)); break;
                case 'd': msg.Args.Add(BitConverter.Int64BitsToDouble(ReadLong(b, ref o, end))); break;
                case 't': ReadLong(b, ref o, end); msg.Args.Add("<timetag>"); break;
                case 'T': msg.Args.Add(true); break;
                case 'F': msg.Args.Add(false); break;
                case 'N': msg.Args.Add(null!); break;
                case 'I': msg.Args.Add("inf"); break;
                case 'b':
                {
                    int len = ReadInt(b, ref o, end);
                    if (len < 0 || len > end - o) throw new FormatException("Bad OSC blob length.");
                    msg.Args.Add(b.AsSpan(o, len).ToArray());
                    o += len + ((4 - (len % 4)) % 4);
                    if (o > end) throw new FormatException("Truncated OSC blob.");
                    break;
                }
                default:
                    // Unknown tag: its size is unknown, so later args can't be located. Stop rather than misread.
                    msg.Args.Add($"<unsupported '{t}'>");
                    return msg;
            }
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

    static string ReadString(byte[] b, ref int o, int end)
    {
        if (o >= end) throw new FormatException("Truncated OSC string.");
        int nul = Array.IndexOf(b, (byte)0, o, end - o);
        if (nul < 0) throw new FormatException("Unterminated OSC string.");
        var s = Encoding.ASCII.GetString(b, o, nul - o);
        int n = nul - o + 1;
        o += n + ((4 - (n % 4)) % 4);
        if (o > end) throw new FormatException("OSC string padding past end.");
        return s;
    }

    static int ReadInt(byte[] b, ref int o, int end)
    {
        if (end - o < 4) throw new FormatException("Truncated OSC int32.");
        int v = (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
        o += 4;
        return v;
    }

    static long ReadLong(byte[] b, ref int o, int end)
    {
        long hi = (uint)ReadInt(b, ref o, end);
        long lo = (uint)ReadInt(b, ref o, end);
        return (hi << 32) | lo;
    }

    static byte[] BeInt(int v) =>
        [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    static byte[] BeFloat(float v) => BeInt(BitConverter.SingleToInt32Bits(v));

    static byte[] Concat(List<byte[]> parts)
    {
        var n = parts.Sum(p => p.Length);
        var o = new byte[n];
        int i = 0;
        foreach (var p in parts) { Buffer.BlockCopy(p, 0, o, i, p.Length); i += p.Length; }
        return o;
    }
}
