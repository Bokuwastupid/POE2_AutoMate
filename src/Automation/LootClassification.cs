namespace POE2_AutoMate.Automation;

public static class LootClassification
{
    private static readonly (string Needle, string Name)[] CurrencyNames =
    [
        ("PerfectExalted", "Perfect Exalted Orb"),
        ("GreaterExalted", "Greater Exalted Orb"),
        ("CurrencyAddModToRare", "Exalted Orb"),
        ("ExaltedOrb", "Exalted Orb"),
        ("Divine", "Divine Orb"),
        ("OrbOfAlchemy", "Orb of Alchemy"),
        ("Alchemy", "Orb of Alchemy"),
        ("Chaos", "Chaos Orb"),
        ("Regal", "Regal Orb"),
        ("Vaal", "Vaal Orb"),
        ("Chance", "Orb of Chance"),
        ("Transmutation", "Orb of Transmutation"),
        ("Augmentation", "Orb of Augmentation"),
        ("Artificer", "Artificer's Orb"),
        ("ArmourersScrap", "Armourer's Scrap"),
        ("Armourer", "Armourer's Scrap"),
        ("Blacksmith", "Blacksmith's Whetstone"),
        ("Glassblower", "Glassblower's Bauble"),
        ("Gemcutter", "Gemcutter's Prism"),
        ("PerfectJeweller", "Perfect Jeweller's Orb"),
        ("GreaterJeweller", "Greater Jeweller's Orb"),
        ("LesserJeweller", "Lesser Jeweller's Orb"),
        ("Jeweller", "Jeweller's Orb"),
        ("CurrencyIdentification", "Scroll of Wisdom")
    ];

    public static bool IsCurrencyMetadata(string metadata) =>
        metadata.Contains("/Currency/", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("Metadata/Items/Currency", StringComparison.OrdinalIgnoreCase);

    public static bool IsGearMetadata(string metadata) =>
        metadata.Contains("/Weapons/", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("/Armours/", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("/Rings/", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("/Amulets/", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("/Belts/", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("/Jewels/", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("/Flasks/", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("/Gems/", StringComparison.OrdinalIgnoreCase);

    public static bool IsCurrencyBucket(string bucket) =>
        CurrencyNames.Any(pair => pair.Name.Equals(bucket, StringComparison.OrdinalIgnoreCase)) ||
        bucket.StartsWith("Currency:", StringComparison.OrdinalIgnoreCase);

    public static string Bucket(string metadata, int rarity)
    {
        if (TryCurrencyName(metadata, out string? currency))
        {
            return currency ?? "Currency: Unknown";
        }

        return rarity switch
        {
            3 => "Unique",
            2 => "Rare",
            1 => "Magic",
            0 => "Common",
            _ => IsCurrencyMetadata(metadata) ? $"Currency: {ReadableLastPathPart(metadata)}" : "Other"
        };
    }

    public static bool TryCurrencyName(string metadata, out string? name)
    {
        foreach ((string needle, string currencyName) in CurrencyNames)
        {
            if (metadata.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                name = currencyName;
                return true;
            }
        }

        if (IsCurrencyMetadata(metadata))
        {
            name = $"Currency: {ReadableLastPathPart(metadata)}";
            return true;
        }

        name = null;
        return false;
    }

    private static string ReadableLastPathPart(string metadata)
    {
        string last = metadata.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? metadata;
        if (last.StartsWith("Currency", StringComparison.OrdinalIgnoreCase))
        {
            last = last["Currency".Length..];
        }

        List<char> chars = [];
        for (int i = 0; i < last.Length; i++)
        {
            char c = last[i];
            if (i > 0 && char.IsUpper(c) && (char.IsLower(last[i - 1]) || (i + 1 < last.Length && char.IsLower(last[i + 1]))))
            {
                chars.Add(' ');
            }

            chars.Add(c);
        }

        string readable = new string(chars.ToArray()).Replace('_', ' ').Replace('-', ' ').Trim();
        return string.IsNullOrWhiteSpace(readable) ? "Unknown" : readable;
    }
}
