namespace POE2_AutoMate.Core.Game;

public sealed class GameState
{
    public bool IsGameDetected { get; set; }
    public bool IsMemoryAttached { get; set; }
    public string AreaName { get; set; } = "Unknown";
    public int PlayerHealthPercent { get; set; }
    public int PlayerEnergyShieldPercent { get; set; }
    public IReadOnlyList<Entity> Entities { get; set; } = Array.Empty<Entity>();
}
