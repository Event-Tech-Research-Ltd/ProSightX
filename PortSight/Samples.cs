namespace PortSight;

public static class Samples
{
    public static byte[] CiscoAccessVoice()
    {
        var src = Mac("00:1A:A2:4C:10:18");
        var lldp = Concat(
            Mac("01:80:C2:00:00:0E"), src, U16(0x88CC),
            LldpTlv(1, Concat(new byte[] { 4 }, src)),
            LldpTlv(2, Concat(new byte[] { 5 }, Ascii("Gi1/0/24"))),
            LldpTlv(3, U16(120)),
            LldpTlv(4, Ascii("GigabitEthernet1/0/24")),
            LldpTlv(5, Ascii("core-sw-01.lab.local")),
            LldpTlv(6, Ascii("Cisco IOS Software [Cupertino], Catalyst L3 Switch Software (CAT9K_IOSXE), Version 17.9.4a")),
            LldpTlv(7, Concat(U16(0x0014), U16(0x0014))),
            LldpTlv(8, Concat(new byte[] { 5, 1, 10, 10, 10, 1, 2, 0, 0, 0, 1, 0 })),
            LldpTlv(127, Concat(new byte[] { 0x00, 0x80, 0xC2, 1 }, U16(20))),
            LldpTlv(127, Concat(new byte[] { 0x00, 0x80, 0xC2, 3 }, U16(20), new byte[] { 4 }, Ascii("DATA"))),
            LldpTlv(127, Concat(new byte[] { 0x00, 0x80, 0xC2, 3 }, U16(30), new byte[] { 5 }, Ascii("VOICE"))),
            LldpTlv(127, Concat(new byte[] { 0x00, 0x12, 0x0F, 1, 0x03 }, U16(0x0C00), U16(0x001E))),
            MedVoice(30),
            LldpTlv(127, Concat(new byte[] { 0x00, 0x12, 0xBB, 10 }, Ascii("C9300-48P"))),
            LldpTlv(0, []));
        return lldp;
    }

    static byte[] MedVoice(int vlan)
    {
        int app = 1, tagged = 1, pri = 5, dscp = 46;
        int v = (app << 24) | (tagged << 22) | ((vlan & 0xFFF) << 9) | ((pri & 7) << 6) | (dscp & 0x3F);
        return LldpTlv(127, [
            0x00, 0x12, 0xBB, 2,
            (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v,
        ]);
    }

    static byte[] LldpTlv(int type, byte[] value)
    {
        int len = value.Length;
        return Concat(U16(((type & 0x7F) << 9) | (len & 0x1FF)), value);
    }

    static byte[] Mac(string s) =>
        [.. s.Split(':').Select(h => Convert.ToByte(h, 16))];

    static byte[] Ascii(string s) => System.Text.Encoding.ASCII.GetBytes(s);
    static byte[] U16(int n) => [(byte)(n >> 8), (byte)n];

    static byte[] Concat(params byte[][] parts)
    {
        var n = parts.Sum(p => p.Length);
        var o = new byte[n];
        int i = 0;
        foreach (var p in parts) { Buffer.BlockCopy(p, 0, o, i, p.Length); i += p.Length; }
        return o;
    }
}
