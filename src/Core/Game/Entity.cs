namespace POE2_AutoMate.Core.Game;

public sealed record Entity(
    long Address,
    string Name,
    EntityKind Kind,
    float X,
    float Y,
    bool IsHostile);

public enum EntityKind
{
    Unknown,
    Player,
    Monster,
    Npc,
    Chest,
    Portal,
    Item
}
