namespace PortSight;

public static class Pcap
{
    public static List<byte[]> Extract(string path)
    {
        var frames = new List<byte[]>();
        var file = File.ReadAllBytes(path);
        if (file.Length < 24) return frames;

        uint le(int o) => (uint)(file[o] | (file[o + 1] << 8) | (file[o + 2] << 16) | (file[o + 3] << 24));

        if (le(0) == 0x0A0D0D0A)
        {
            int i = 0;
            while (i + 8 <= file.Length)
            {
                uint type = le(i);
                uint blen = le(i + 4);
                if (blen < 12 || i + blen > file.Length) break;
                if (type == 6)
                {
                    uint caplen = le(i + 20);
                    int dataOff = i + 28;
                    if (dataOff + caplen <= i + blen)
                    {
                        var pkt = new byte[caplen];
                        Buffer.BlockCopy(file, dataOff, pkt, 0, (int)caplen);
                        frames.Add(pkt);
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
            if (off + incl > file.Length) break;
            var pkt = new byte[incl];
            Buffer.BlockCopy(file, off, pkt, 0, (int)incl);
            frames.Add(pkt);
            off += (int)incl;
        }
        return frames;
    }
}
