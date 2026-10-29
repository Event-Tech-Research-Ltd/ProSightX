namespace ProSight;

public sealed class Neighbor
{
    public string Protocol { get; set; } = "";
    public string SrcMac { get; set; } = "";
    public string SwitchName { get; set; } = "";
    public string PortId { get; set; } = "";
    public string PortDescription { get; set; } = "";
    public string ChassisId { get; set; } = "";
    public string Platform { get; set; } = "";
    public string Software { get; set; } = "";
    public string NativeVlan { get; set; } = "";
    public string VoiceVlan { get; set; } = "";
    public string Mgmt { get; set; } = "";
    public string Duplex { get; set; } = "";

    public static Neighbor Merge(IEnumerable<Neighbor> items)
    {
        var n = new Neighbor();
        foreach (var o in items.OrderBy(x => x.Protocol == "LLDP" ? 0 : 1))
        {
            n.Protocol = First(n.Protocol, o.Protocol);
            n.SrcMac = First(n.SrcMac, o.SrcMac);
            n.SwitchName = First(n.SwitchName, o.SwitchName);
            n.PortId = First(n.PortId, o.PortId);
            n.PortDescription = First(n.PortDescription, o.PortDescription);
            n.ChassisId = First(n.ChassisId, o.ChassisId);
            n.Platform = First(n.Platform, o.Platform);
            n.Software = First(n.Software, o.Software);
            n.NativeVlan = First(n.NativeVlan, o.NativeVlan);
            n.VoiceVlan = First(n.VoiceVlan, o.VoiceVlan);
            n.Mgmt = First(n.Mgmt, o.Mgmt);
            n.Duplex = First(n.Duplex, o.Duplex);
        }
        return n;
    }

    static string First(string a, string b) => string.IsNullOrWhiteSpace(a) ? b : a;

    public string Format(string adapter, string backend, int frames)
    {
        var lines = new List<string>
        {
            $"ProSight {typeof(Neighbor).Assembly.GetName().Version?.ToString(3)}",
            $"Adapter     {adapter}",
            $"Backend     {backend}  frames {frames}",
        };
        if (string.IsNullOrWhiteSpace(Protocol))
        {
            lines.Add("Neighbor    none (no LLDP/CDP in the listen window)");
            lines.Add("Hint        Laptop RJ45 + Cat5/6, not Wi-Fi. Wait 30s+. Switch must send LLDP or CDP.");
            return string.Join(Environment.NewLine, lines);
        }
        lines.AddRange(new[]
        {
            $"Protocol    {Protocol}",
            $"Switch      {SwitchName}",
            $"Platform    {Platform}",
            $"Chassis     {ChassisId}",
            $"Port        {PortId}",
            $"Port desc   {PortDescription}",
            $"Native VLAN {NativeVlan}",
            $"Voice VLAN  {VoiceVlan}",
            $"Mgmt IP     {Mgmt}",
            $"Link        {Duplex}",
            $"Source MAC  {SrcMac}",
            $"Software    {Software}",
        });
        return string.Join(Environment.NewLine, lines);
    }
}
