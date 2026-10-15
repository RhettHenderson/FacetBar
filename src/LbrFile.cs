using System.Text;

namespace LinkbarDesktops;

/// <summary>
/// Minimal reader/writer for Linkbar's *.lbr settings files (INI format; Linkbar only reads the [Main] section).
/// </summary>
internal static class LbrFile
{
    public const string LinksDirKey = "dirlinks";
    public const string AutoHideKey = "autohide";
    private const string MainSection = "Main";

    public static Dictionary<string, string> ReadMain(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return values;

        bool inMain = false;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inMain = line[1..^1].Trim().Equals(MainSection, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            int eq = line.IndexOf('=');
            if (inMain && eq > 0)
                values[line[..eq].Trim()] = line[(eq + 1)..];
        }
        return values;
    }

    /// <summary>Replaces the [Main] section, keeping any other sections that are already in the file.</summary>
    public static void WriteMain(string path, IReadOnlyDictionary<string, string> values)
    {
        var output = new List<string> { $"[{MainSection}]" };
        output.AddRange(values.Select(kv => $"{kv.Key}={kv.Value}"));

        if (File.Exists(path))
        {
            bool keep = false;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    keep = !line[1..^1].Trim().Equals(MainSection, StringComparison.OrdinalIgnoreCase);
                    if (keep) output.Add("");
                }
                if (keep) output.Add(raw);
            }
        }

        // A BOM makes Delphi's TMemIniFile read and save the file as UTF-8, so non-ASCII paths survive.
        File.WriteAllLines(path, output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    public static bool ReadBool(string path, string key)
    {
        var values = ReadMain(path);
        return values.TryGetValue(key, out var v) && (v.Trim() == "1" || v.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Every [Main] setting except the links folder, i.e. how the bar looks and where it sits.</summary>
    public static Dictionary<string, string> ReadStyle(string path)
    {
        var values = ReadMain(path);
        values.Remove(LinksDirKey);
        return values;
    }
}
