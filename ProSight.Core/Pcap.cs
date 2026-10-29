using System.Text;

namespace ProSight;

/// <summary>A captured frame plus the capture interface it arrived on (pcapng IDB), when the file records it.</summary>
public sealed record CapturedFrame(byte[] Data, int InterfaceId, string InterfaceName);

public static class Pcap
{
    public static List<CapturedFrame> Extract(string path)
    {
        var frames = new List<CapturedFrame>();
        var file = File.ReadAllBytes(path);
        if (file.Length < 24) return frames;

        uint le(int o) => (uint)(file[o] | (file[o + 1] << 8) | (file[o + 2] << 16) | (file[o + 3] << 24));
        ushort le16(int o) => (ushort)(file[o] | (file[o + 1] << 8));

        if (le(0) == 0x0A0D0D0A)
        {
            // Interface IDs are per section; names come from IDB options if_name (2) / if_description (3).
            var names = new List<string>();
            int i = 0;
            while (i + 12 <= file.Length)
            {
                uint type = le(i);
                uint blen = le(i + 4);
                if (blen < 12 || blen > int.MaxValue || i + (long)blen > file.Length) break;
                int end = i + (int)blen - 4; // trailing block length
                if (type == 0x0A0D0D0A) names.Clear();
                else if (type == 1 && i + 16 <= end) names.Add(ReadIdbName(file, i + 16, end, le16));
                else if (type == 6 && i + 28 <= end)
                {
                    int ifId = (int)le(i + 8);
                    uint caplen = le(i + 20);
                    int dataOff = i + 28;
                    if (dataOff + (long)caplen <= end)
                    {
                        var pkt = new byte[caplen];
                        Buffer.BlockCopy(file, dataOff, pkt, 0, (int)caplen);
                        frames.Add(new CapturedFrame(pkt, ifId, ifId >= 0 && ifId < names.Count ? names[ifId] : ""));
                    }
                }
                i += (int)blen;
            }
            return frames;
        }

        uint magic = le(0);
        bool swap = magic is 0xD4C3B2A1 or 0x4D3CB2A1;
        int off = 24;
        while (off + 16 <= file.Length)
        {
            uint incl = swap
                ? (uint)((file[off + 8] << 24) | (file[off + 9] << 16) | (file[off + 10] << 8) | file[off + 11])
                : le(off + 8);
            off += 16;
            if (off + (long)incl > file.Length) break;
            var pkt = new byte[incl];
            Buffer.BlockCopy(file, off, pkt, 0, (int)incl);
            frames.Add(new CapturedFrame(pkt, 0, ""));
            off += (int)incl;
        }
        return frames;
    }

    static string ReadIdbName(byte[] f, int o, int end, Func<int, ushort> le16)
    {
        string name = "", desc = "";
        while (o + 4 <= end)
        {
            int code = le16(o), len = le16(o + 2);
            o += 4;
            if (code == 0 || o + len > end) break; // opt_endofopt
            if (code == 2) name = Encoding.UTF8.GetString(f, o, len).TrimEnd('\0');
            else if (code == 3) desc = Encoding.UTF8.GetString(f, o, len).TrimEnd('\0');
            o += len + ((4 - (len % 4)) % 4);
        }
        return string.Join(" | ", new[] { name, desc }.Where(s => s.Length > 0));
    }
}
