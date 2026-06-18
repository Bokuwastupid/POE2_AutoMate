using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace POE2_AutoMate.Automation;

public sealed class LootValueTracker
{
    private readonly Dictionary<string, double> _divineValues = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastUpdateUtc = DateTime.MinValue;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public string LastError { get; private set; } = string.Empty;
    public DateTime LastUpdateUtc => _lastUpdateUtc;

    public async Task RefreshAsync(string league, string cachePath, TimeSpan maxAge, CancellationToken cancellationToken = default)
    {
        if (DateTime.UtcNow - _lastUpdateUtc < maxAge && _divineValues.Count > 0)
        {
            return;
        }

        if (TryLoadCache(cachePath, maxAge))
        {
            return;
        }

        try
        {
            string encodedLeague = Uri.EscapeDataString(string.IsNullOrWhiteSpace(league) ? "Runes of Aldur" : league);
            Uri uri = new($"https://poe.ninja/api/data/currencyoverview?league={encodedLeague}&type=Currency&game=poe2");
            PoeNinjaCurrencyResponse? response = await Http.GetFromJsonAsync<PoeNinjaCurrencyResponse>(uri, cancellationToken);
            if (response?.Lines is null || response.Lines.Count == 0)
            {
                LastError = "poe.ninja returned no currency rows.";
                return;
            }

            _divineValues.Clear();
            foreach (PoeNinjaCurrencyLine line in response.Lines)
            {
                if (string.IsNullOrWhiteSpace(line.CurrencyTypeName))
                {
                    continue;
                }

                _divineValues[line.CurrencyTypeName] = line.DivineEquivalent > 0
                    ? line.DivineEquivalent
                    : line.ChaosEquivalent > 0 && TryGetDivineChaos(response, out double divineChaos)
                        ? line.ChaosEquivalent / divineChaos
                        : 0;
            }

            if (!_divineValues.ContainsKey("Divine Orb"))
            {
                _divineValues["Divine Orb"] = 1;
            }

            _lastUpdateUtc = DateTime.UtcNow;
            SaveCache(cachePath);
            LastError = string.Empty;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            TryLoadCache(cachePath, TimeSpan.MaxValue);
        }
    }

    public LootValueSummary Summarize(IReadOnlyDictionary<string, int> counts)
    {
        double total = 0;
        foreach ((string name, int count) in counts)
        {
            total += count * ValueOfDivine(name);
        }

        return new LootValueSummary(total, _lastUpdateUtc, LastError);
    }

    public double ValueOfDivine(string name)
    {
        if (_divineValues.TryGetValue(name, out double value))
        {
            return value;
        }

        return name.Equals("Divine Orb", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }

    private bool TryLoadCache(string cachePath, TimeSpan maxAge)
    {
        try
        {
            if (!File.Exists(cachePath))
            {
                return false;
            }

            LootValueCache? cache = System.Text.Json.JsonSerializer.Deserialize<LootValueCache>(File.ReadAllText(cachePath));
            if (cache is null || DateTime.UtcNow - cache.UpdatedUtc > maxAge || cache.Values.Count == 0)
            {
                return false;
            }

            _divineValues.Clear();
            foreach ((string key, double value) in cache.Values)
            {
                _divineValues[key] = value;
            }

            _lastUpdateUtc = cache.UpdatedUtc;
            LastError = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    private void SaveCache(string cachePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        LootValueCache cache = new(_lastUpdateUtc, new Dictionary<string, double>(_divineValues, StringComparer.OrdinalIgnoreCase));
        File.WriteAllText(cachePath, System.Text.Json.JsonSerializer.Serialize(cache, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private static bool TryGetDivineChaos(PoeNinjaCurrencyResponse response, out double divineChaos)
    {
        divineChaos = response.Lines.FirstOrDefault(line =>
            line.CurrencyTypeName.Equals("Divine Orb", StringComparison.OrdinalIgnoreCase))?.ChaosEquivalent ?? 0;
        return divineChaos > 0;
    }
}

public sealed record LootValueSummary(double DivineEquivalent, DateTime LastPriceUpdateUtc, string Error);

public sealed record LootValueCache(DateTime UpdatedUtc, Dictionary<string, double> Values);

public sealed class PoeNinjaCurrencyResponse
{
    [JsonPropertyName("lines")]
    public List<PoeNinjaCurrencyLine> Lines { get; set; } = [];
}

public sealed class PoeNinjaCurrencyLine
{
    [JsonPropertyName("currencyTypeName")]
    public string CurrencyTypeName { get; set; } = string.Empty;

    [JsonPropertyName("chaosEquivalent")]
    public double ChaosEquivalent { get; set; }

    [JsonPropertyName("divineEquivalent")]
    public double DivineEquivalent { get; set; }
}
