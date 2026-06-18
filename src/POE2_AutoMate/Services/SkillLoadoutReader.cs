using System.IO;
using System.Text.RegularExpressions;

namespace POE2_AutoMate.Services;

public sealed class SkillLoadoutReader
{
    private static readonly Regex KeyValueRegex = new(@"^(?<name>[^=]+)=(?<value>.*)$", RegexOptions.Compiled);

    public SkillLoadoutSnapshot Read()
    {
        string configPath = FindConfigPath();
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
        {
            return new SkillLoadoutSnapshot(string.Empty, [], "PoE2 config file was not found.");
        }

        List<SkillBindInfo> binds = [];
        string section = string.Empty;
        foreach (string rawLine in File.ReadLines(configPath))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line.Trim('[', ']');
                continue;
            }

            if (!section.Equals("WASD_ACTION_KEYS", StringComparison.OrdinalIgnoreCase) &&
                !section.Equals("ACTION_KEYS", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Match match = KeyValueRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            string action = match.Groups["name"].Value.Trim();
            if (!action.StartsWith("use_bound_skill", StringComparison.OrdinalIgnoreCase) &&
                !action.StartsWith("use_temporary_skill", StringComparison.OrdinalIgnoreCase) &&
                !action.StartsWith("use_flask_in_slot", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string token = ToToken(match.Groups["value"].Value);
            if (!string.IsNullOrWhiteSpace(token))
            {
                binds.Add(new SkillBindInfo(section, action, token));
            }
        }

        IReadOnlyList<SkillBindInfo> preferred = binds.Any(bind => bind.Section.Equals("WASD_ACTION_KEYS", StringComparison.OrdinalIgnoreCase))
            ? binds.Where(bind => bind.Section.Equals("WASD_ACTION_KEYS", StringComparison.OrdinalIgnoreCase)).ToArray()
            : binds;
        return new SkillLoadoutSnapshot(configPath, preferred, string.Empty);
    }

    private static string FindConfigPath()
    {
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string poe2 = Path.Combine(docs, "My Games", "Path of Exile 2");
        string preferred = Path.Combine(poe2, "poe2_production_Config.ini");
        if (File.Exists(preferred))
        {
            return preferred;
        }

        return Directory.Exists(poe2)
            ? Directory.EnumerateFiles(poe2, "*Config.ini", SearchOption.TopDirectoryOnly).FirstOrDefault() ?? string.Empty
            : string.Empty;
    }

    private static string ToToken(string value)
    {
        string first = value.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        if (!int.TryParse(first, out int key) || key == 0)
        {
            return string.Empty;
        }

        return key switch
        {
            1 => "LMB",
            2 => "RMB",
            4 => "MMB",
            16 => "SHIFT",
            17 => "CTRL",
            18 => "ALT",
            >= 48 and <= 57 => ((char)key).ToString(),
            >= 65 and <= 90 => ((char)key).ToString(),
            >= 112 and <= 123 => $"F{key - 111}",
            32 => "SPACE",
            222 => "'",
            _ => $"VK{key}"
        };
    }
}

public sealed record SkillLoadoutSnapshot(string ConfigPath, IReadOnlyList<SkillBindInfo> Binds, string Error);

public sealed record SkillBindInfo(string Section, string Action, string Key);
