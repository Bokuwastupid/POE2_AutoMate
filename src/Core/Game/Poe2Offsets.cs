namespace POE2_AutoMate.Core.Game;

public static class Poe2Offsets
{
    public const float WorldToGridRatio = 250f / 23f;

    public static class GameState
    {
        public const int CurrentStatePtr = 0x08;
        public const int States = 0x48;
        public const int StateSlotStride = 0x10;
        public const int StateSlotCount = 12;
    }

    public static class InGameState
    {
        public const int AreaInstanceData = 0x290;
        public const int UiRoot = 0x2F0;
        public const int WorldData = 0x310;
        public const int Camera = 0x368;
    }

    public static class AreaInstance
    {
        public const int AreaInfoPtr = 0x0A0;
        public const int ServerDataPtr = 0x580;
        public const int LocalPlayer = 0x5A0;
        public const int AwakeEntities = 0x6C0;
        public const int SleepingEntities = 0x6D0;
        public const int TerrainMetadata = 0x8A0;
    }

    public static class ServerData
    {
        public const int PlayerServerData = 0x48;
        public const int PlayerInventories = 0x320;
    }

    public static class InventoryArrayEntry
    {
        public const int Id = 0x00;
        public const int InventoryPtr = 0x08;
        public const int Stride = 0x18;
    }

    public static class Inventory
    {
        public const int TotalBoxes = 0x150;
        public const int ItemList = 0x170;
        public const int RequestCounter = 0x1E8;
        public const int MainInventoryId = 1;
    }

    public static class InventoryItem
    {
        public const int ItemEntityPtr = 0x00;
        public const int SlotStart = 0x08;
        public const int SlotEnd = 0x10;
        public const int Stride = 0x18;
    }

    public static class Terrain
    {
        public const int TotalTiles = 0x18;
        public const int TileDetailsPtr = 0x28;
        public const int GridWalkableData = 0xD0;
        public const int GridLandscapeData = 0xE8;
        public const int GridLayer3 = 0x100;
        public const int GridLayer4 = 0x118;
        public const int BytesPerRow = 0x130;
        public const int TileGridCells = 23;
    }

    public const int TileStructureSize = 0x38;

    public static class TileStructure
    {
        public const int TgtFilePtr = 0x08;
    }

    public static class TgtFileStruct
    {
        public const int TgtPath = 0x08;
    }

    public static class Camera
    {
        public const int WorldToScreenMatrix = 0x1A0;
        public const int Zoom = 0x528;
    }

    public static class UiElement
    {
        public const int Self = 0x08;
        public const int Children = 0x10;
        public const int ChildrenEnd = 0x18;
        public const int PositionModifier = 0xF0;   // StdTuple2D<float>; added to parent pos when Flags bit 0x0A set
        public const int Parent = 0xB8;
        public const int RelativePosition = 0x118;
        public const int LocalScaleMul = 0x130;     // float local scale multiplier
        public const int Flags = 0x180;
        public const int FlagModifyPosBit = 0x0A;   // when set, PositionModifier (+0xF0) is added to the parent pos
        public const int FlagVisibleBit = 0x0B;
        public const int ScaleIndex = 0x18A;        // byte; selects which axis scale(s) apply (1=v1,2=v2,3=v1xv2)
        public const int SizeWidth = 0x288;
        public const int SizeHeight = 0x28C;
        public const int Text = 0x390;
        public const double BaseResW = 2560.0;
        public const double BaseResH = 1600.0;
    }

    public static class EntityList
    {
        public const int StdMapSize = 0x10;
        public const uint VisualIdThreshold = 0x40000000;
    }

    public static class StdMapNode
    {
        public const int Left = 0x00;
        public const int Parent = 0x08;
        public const int Right = 0x10;
        public const int IsNil = 0x19;
        public const int KeyId = 0x20;
        public const int ValueEntityPtr = 0x28;
    }

    public static class Entity
    {
        public const int EntityDetailsPtr = 0x08;
        public const int ComponentList = 0x10;
        public const int Id = 0x80;
        public const int IsValid = 0x84;
    }

    public static class EntityDetails
    {
        public const int Name = 0x08;
        public const int ComponentLookupPtr = 0x28;
    }

    public static class ComponentLookUp
    {
        public const int NameAndIndexBucket = 0x28;
        public const int EntryStride = 0x10;
    }

    public static class Life
    {
        public const int Owner = 0x008;
        public const int Health = 0x1B0;
        public const int Mana = 0x208;
        public const int EnergyShield = 0x248;
    }

    public static class Vital
    {
        public const int ReservedFlat = 0x10;
        public const int Regen = 0x28;
        public const int Max = 0x2C;
        public const int Current = 0x30;
    }

    public static class Render
    {
        public const int CurrentWorldPosition = 0x138;
    }

    public static class Positioned
    {
        public const int Reaction = 0x1E0;
    }

    public static class ObjectMagicProperties
    {
        public const int Rarity = 0x144;
    }

    /// <summary>Atlas map-node UiElement fields (community/GH2 dump, validated live in POE2Radar 2026-06).</summary>
    public static class AtlasNode
    {
        public const int MapNodeId = 0x300; // u32 distinct per node; also → EndgameMaps row
        public const int Content = 0x310;   // u32 content (0 = none / rolled-content row ptr)
        public const int GridPos = 0x320;   // StdTuple2D<int> atlas grid coord (X,Y)
        public const int State = 0x32C;     // u8 state (seen = 1 on loaded nodes)
        public const int Biome = 0x32E;     // u8 biome index (0..12)
        public const int Flags = 0x32F;     // u8: bit0 unlocked, bit1 visited
        public const int Completion = 0x339; // u8 per-node completion id
        public const int MapRowName = 0x08; // WorldAreas row +0x08 → localized map name
    }

    public static class AtlasGraph
    {
        public const int ConnectionsVec = 0x5A8;
        public const int EdgeStride = 20;
        public const int EdgeSourceOff = 0x04;
        public const int EdgeTargetOff = 0x0C;
        public const int CurrentMarkerNodePtr = 0x300;
    }

    /// <summary>The persistent atlas panel: a direct UiRoot child at a stable index; its visible bit gates open.</summary>
    public static class AtlasPanel
    {
        public const int UiRootChildIndex = 22; // live 2026-06-08, stable across cold restart
    }
}
