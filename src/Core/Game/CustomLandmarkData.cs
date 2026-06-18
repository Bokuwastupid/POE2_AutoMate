using System.Reflection;
using System.Text.Json;

namespace POE2_AutoMate.Core.Game;

public static class CustomLandmarkData
{
    private static Dictionary<string, Dictionary<string, string>>? _data;

    public static string? TryMatch(string areaCode, string tilePath)
    {
        Dictionary<string, Dictionary<string, string>> data = Load();
        if (data.TryGetValue(areaCode, out Dictionary<string, string>? areaMap) &&
            TryMatchMap(areaMap, tilePath, out string? areaLabel))
        {
            return areaLabel;
        }

        return data.TryGetValue("*", out Dictionary<string, string>? globalMap) &&
               TryMatchMap(globalMap, tilePath, out string? globalLabel)
            ? globalLabel
            : TryFallbackLabel(tilePath);
    }

    private static Dictionary<string, Dictionary<string, string>> Load()
    {
        if (_data is not null)
        {
            return _data;
        }

        _data = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            Assembly assembly = typeof(CustomLandmarkData).Assembly;
            string? resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith("CustomLandmarks.json", StringComparison.OrdinalIgnoreCase));
            if (resourceName is null)
            {
                return _data;
            }

            using Stream? stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                return _data;
            }

            using JsonDocument document = JsonDocument.Parse(stream);
            foreach (JsonProperty areaProperty in document.RootElement.EnumerateObject())
            {
                Dictionary<string, string> tiles = new(StringComparer.OrdinalIgnoreCase);
                foreach (JsonProperty tileProperty in areaProperty.Value.EnumerateObject())
                {
                    string pattern = tileProperty.Name;
                    int colon = pattern.IndexOf(':');
                    if (colon >= 0)
                    {
                        pattern = pattern[..colon];
                    }

                    tiles[pattern.Replace(".tdtx", ".tdt", StringComparison.OrdinalIgnoreCase)] =
                        tileProperty.Value.GetString() ?? tileProperty.Name;
                }

                _data[areaProperty.Name] = tiles;
            }
        }
        catch
        {
            _data.Clear();
        }

        return _data;
    }

    private static bool TryMatchMap(Dictionary<string, string> map, string tilePath, out string? label)
    {
        foreach ((string pattern, string value) in map)
        {
            if (tilePath.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                label = value;
                return true;
            }
        }

        label = null;
        return false;
    }

    private static string? TryFallbackLabel(string tilePath)
    {
        string lower = tilePath.ToLowerInvariant();
        if (lower.Contains("boss") || lower.Contains("arena") || lower.Contains("final"))
        {
            return "Boss Landmark";
        }

        return null;
    }
}
