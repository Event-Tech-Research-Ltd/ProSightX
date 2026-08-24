using System.Text;

namespace PortSight;

public static class Decoder
{
    static readonly byte[] LldpDst = [0x01, 0x80, 0xC2, 0x00, 0x00, 0x0E];
    static readonly byte[] CdpDst = [0x01, 0x00, 0x0C, 0xCC, 0xCC, 0xCC];

    public static bool IsLldp(byte[] p) =>
        p.Length >= 14 && (Starts(p, LldpDst) || EtherType(p) == 0x88CC);

    public static bool IsCdp(byte[] p) =>
        p.Length >= 22 && Starts(p, CdpDst);

    public static Neighbor? Decode(byte[] p)
    {
        if (p.Length < 14) return null;
        var n = new Neighbor { SrcMac = Mac(p, 6) };
        int payload = PayloadOffset(p, out int et);
        if (IsLldp(p) || et == 0x88CC)
        {
            n.Protocol = "LLDP";
            DecodeLldp(p, payload, n);
            return n;
        }
        if (IsCdp(p))
        {
            n.Protocol = "CDP";
            int snap = payload;
            if (p.Length > snap + 8 && p[snap] == 0xAA) snap += 8;
            DecodeCdp(p, snap, n);
            return n;
        }
        return null;
    }

    static int PayloadOffset(byte[] p, out int et)
    {
        et = Be16(p, 12);
        if (et == 0x8100 && p.Length >= 18)
        {
            et = Be16(p, 16);
            return 18;
        }
        return 14;
    }

    static void DecodeLldp(byte[] p, int o, Neighbor n)
    {
        while (o + 2 <= p.Length)
        {
            int head = Be16(p, o);
            int type = (head >> 9) & 0x7F;
            int len = head & 0x1FF;
            o += 2;
            if (type == 0) break;
            if (o + len > p.Length) break;
            if (type == 1 && len >= 1)
            {
                int st = p[o];
                n.ChassisId = st == 4 && len >= 7 ? Mac(p, o + 1) : Ascii(p, o + 1, len - 1);
            }
            else if (type == 2 && len >= 1) n.PortId = Ascii(p, o + 1, len - 1);
            else if (type == 4) n.PortDescription = Ascii(p, o, len);
            else if (type == 5) n.SwitchName = Ascii(p, o, len);
            else if (type == 6) n.Software = Ascii(p, o, len);
            else if (type == 8 && len >= 6 && p[o + 1] == 1)
                n.Mgmt = $"{p[o + 2]}.{p[o + 3]}.{p[o + 4]}.{p[o + 5]}";
            else if (type == 127 && len >= 4)
            {
                int oui = (p[o] << 16) | (p[o + 1] << 8) | p[o + 2];
                int st = p[o + 3];
                if (oui == 0x0080C2 && st == 1 && len >= 6)
                    n.NativeVlan = Be16(p, o + 4).ToString();
                if (oui == 0x0012BB && st == 2 && len >= 8)
                {
                    int v = (p[o + 4] << 24) | (p[o + 5] << 16) | (p[o + 6] << 8) | p[o + 7];
                    if (((v >> 24) & 0xFF) == 1) n.VoiceVlan = ((v >> 9) & 0xFFF).ToString();
                }
                if (oui == 0x00120F && st == 1 && len >= 9)
                {
                    int mau = Be16(p, o + 7);
                    n.Duplex = mau switch
                    {
                        0x001E => "1000BASE-T FD",
                        0x0010 => "100BASE-TX FD",
                        0x0028 => "10GBASE-T",
                        _ => $"MAU 0x{mau:X}",
                    };
                }
                if (oui == 0x0012BB && st == 10) n.Platform = Ascii(p, o + 4, len - 4);
            }
            o += len;
        }
    }

    static void DecodeCdp(byte[] p, int o, Neighbor n)
    {
        if (o + 4 > p.Length) return;
        o += 4;
        while (o + 4 <= p.Length)
        {
            int type = Be16(p, o);
            int len = Be16(p, o + 2);
            if (len < 4 || o + len > p.Length) break;
            int v = o + 4;
            int vl = len - 4;
            if (type == 1) n.SwitchName = Ascii(p, v, vl);
            else if (type == 3) n.PortId = Ascii(p, v, vl);
            else if (type == 5) n.Software = Ascii(p, v, vl);
            else if (type == 6) n.Platform = Ascii(p, v, vl);
            else if (type == 0x0A && vl >= 2) n.NativeVlan = Be16(p, v).ToString();
            else if (type == 0x0B && vl >= 1) n.Duplex = p[v] == 1 ? "Full" : "Half";
            else if ((type == 0x0E || type == 0x0F) && vl >= 2)
                n.VoiceVlan = Be16(p, v + vl - 2).ToString();
            o += len;
        }
    }

    static bool Starts(byte[] p, byte[] prefix)
    {
        if (p.Length < prefix.Length) return false;
        for (int i = 0; i < prefix.Length; i++)
            if (p[i] != prefix[i]) return false;
        return true;
    }

    static int EtherType(byte[] p) => Be16(p, 12);
    static int Be16(byte[] p, int o) => (p[o] << 8) | p[o + 1];

    public static string Mac(byte[] p, int o) =>
        $"{p[o]:X2}:{p[o + 1]:X2}:{p[o + 2]:X2}:{p[o + 3]:X2}:{p[o + 4]:X2}:{p[o + 5]:X2}";

    public static string Ascii(byte[] p, int o, int n)
    {
        int end = Math.Min(p.Length, o + n);
        var sb = new StringBuilder();
        for (int i = o; i < end; i++)
        {
            byte b = p[i];
            if (b == 0) break;
            sb.Append(b is >= 32 and < 127 ? (char)b : '.');
        }
        return sb.ToString();
    }
}
