namespace ProSight;

/// <summary>
/// MAC vendor lookup from a local OUI file — no vendor list is hard-coded, so names are only shown when backed by a
/// real registry file. Search order: oui.csv (IEEE "MA-L" CSV) or manuf next to ProSight.exe, then Wireshark's manuf.
/// </summary>
public static class Oui
{
    static readonly Lazy<(Dictionary<int, string> Map, string Source)> Db = new(Load);

    public static string Source => Db.Value.Source;

    /// <summary>~/Library/Application Support/ProSight on macOS, %APPDATA%\ProSight on Windows.</summary>
    public static string UserDataFolder => OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "ProSight")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ProSight");

    public static string Lookup(string mac)
    {
        var hex = mac.Replace(":", "").Replace("-", "");
        if (hex.Length < 6 || !int.TryParse(hex[..6], System.Globalization.NumberStyles.HexNumber, null, out var oui)) return "";
        if ((oui & 0x020000) != 0) return "(random / locally administered)";
        return Db.Value.Map.TryGetValue(oui, out var v) ? v : "";
    }

    static (Dictionary<int, string>, string) Load()
    {
        var candidates = new List<string>
        {
            // Per-user folder first: on macOS, files added inside a signed ProSight.app would break its signature.
            Path.Combine(UserDataFolder, "oui.csv"),
            Path.Combine(AppContext.BaseDirectory, "oui.csv"),
            Path.Combine(AppContext.BaseDirectory, "manuf"),
        };
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (programFiles.Length > 0) candidates.Add(Path.Combine(programFiles, "Wireshark", "manuf"));
        if (OperatingSystem.IsMacOS()) candidates.Add("/Applications/Wireshark.app/Contents/Resources/share/wireshark/manuf");
        foreach (var path in candidates)
        {
            try
            {
                if (!File.Exists(path)) continue;
                var map = path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? ParseIeeeCsv(path) : ParseManuf(path);
                if (map.Count > 0) return (map, path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return (new Dictionary<int, string>(), "");
    }

    // IEEE: Registry,Assignment,Organization Name,Organization Address   e.g. MA-L,001AA2,"Cisco Systems, Inc",...
    static Dictionary<int, string> ParseIeeeCsv(string path)
    {
        var map = new Dictionary<int, string>();
        foreach (var line in File.ReadLines(path))
        {
            if (!line.StartsWith("MA-L,", StringComparison.Ordinal)) continue;
            var rest = line[5..];
            if (rest.Length < 8 || rest[6] != ',') continue;
            if (!int.TryParse(rest[..6], System.Globalization.NumberStyles.HexNumber, null, out var oui)) continue;
            var org = rest[7..];
            string name;
            if (org.StartsWith('"')) { int q = org.IndexOf('"', 1); name = q > 0 ? org[1..q] : org.Trim('"'); }
            else { int c = org.IndexOf(','); name = c >= 0 ? org[..c] : org; }
            map.TryAdd(oui, name.Trim());
        }
        return map;
    }

    // Wireshark manuf: "00:1A:A2<TAB>Cisco<TAB>Cisco Systems, Inc". Sub-/24 (MA-M/MA-S "/28", "/36") entries skipped.
    static Dictionary<int, string> ParseManuf(string path)
    {
        var map = new Dictionary<int, string>();
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length < 9 || line[0] == '#') continue;
            var cols = line.Split('\t');
            if (cols.Length < 2 || cols[0].Contains('/')) continue;
            var hex = cols[0].Replace(":", "").Replace("-", "");
            if (hex.Length != 6 || !int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var oui)) continue;
            map.TryAdd(oui, (cols.Length >= 3 && cols[2].Length > 0 ? cols[2] : cols[1]).Trim());
        }
        return map;
    }
}
