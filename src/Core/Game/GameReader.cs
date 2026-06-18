using POE2_AutoMate.Core.Memory;
using System.Text.RegularExpressions;

namespace POE2_AutoMate.Core.Game;

public sealed class GameReader : IDisposable
{
    private static readonly string[] FallbackProcessNames = ["PathOfExileSteam", "PathOfExile2"];

    private ProcessReader? _processReader;
    private AobScanner? _aobScanner;
    private readonly byte[] _mapNodeBuffer = new byte[0x30];
    private static readonly Regex ExactMonsterCounterRegex = new(@"(?<count>\d+)\s+monsters?\s+remain", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RussianMonsterCounterRegex = new(@"(?<count>\d+).{0,24}монстр", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private long _terrainCacheArea;
    private TerrainData? _terrainCache;
    private long _landmarkCacheArea;
    private IReadOnlyList<LandmarkData> _landmarkCache = [];
    private Poe2AtlasNodes? _atlasNodes;

    public bool IsConnected { get; private set; }
    public string ConnectedProcessName => _processReader?.ProcessName ?? string.Empty;
    public int? ConnectedProcessId => _processReader?.ProcessId;
    public long GameStateAddress { get; private set; }
    public long InGameStateAddress { get; private set; }
    public long GameStateTargetAddress { get; private set; }
    public long InGameStateTargetAddress { get; private set; }
    public long ModuleBaseAddress { get; private set; }
    public int ModuleSize { get; private set; }
    public string LastError { get; private set; } = string.Empty;
    public string LastInventoryError { get; private set; } = string.Empty;

    public bool Connect(string preferredProcessName = "PathOfExileSteam")
    {
        Disconnect();

        _processReader = new ProcessReader();
        foreach (string processName in CandidateProcessNames(preferredProcessName))
        {
            if (_processReader.Attach(processName))
            {
                break;
            }
        }

        if (!_processReader.IsAttached)
        {
            LastError = _processReader.LastError;
            Disconnect();
            return false;
        }

        nint baseAddress = _processReader.GetModuleBaseAddress();
        int moduleSize = _processReader.GetModuleSize();
        if (baseAddress == nint.Zero || moduleSize <= 0)
        {
            LastError = $"Could not read module metadata. {_processReader.LastError}".Trim();
            Disconnect();
            return false;
        }

        _aobScanner = new AobScanner(_processReader, baseAddress, moduleSize);
        ModuleBaseAddress = baseAddress.ToInt64();
        ModuleSize = moduleSize;
        IsConnected = true;
        LastError = string.Empty;
        return true;
    }

    public PatternScanReport ScanPatterns()
    {
        if (!IsConnected || _aobScanner is null)
        {
            return PatternScanReport.Fail("Scanner is not initialized.");
        }

        List<PatternScanHit> hits = [];
        GameStateAddress = ScanFirst(AobPatterns.GameStateRefs, hits);
        GameStateTargetAddress = GameStateAddress == 0 || _processReader is null
            ? 0
            : _processReader.ReadPointer(GameStateAddress);
        InGameStateAddress = ScanFirst(AobPatterns.InGameStateRefs, hits);
        InGameStateTargetAddress = InGameStateAddress == 0 || _processReader is null
            ? 0
            : _processReader.ReadPointer(InGameStateAddress);

        return new PatternScanReport(hits, string.Empty);
    }

    public PlayerData? GetPlayerData()
    {
        if (!IsConnected || _processReader is null)
        {
            return null;
        }

        try
        {
            if (!TryResolve(out _, out _, out long localPlayerPtr))
            {
                return null;
            }

            PlayerData data = new()
            {
                LocalPlayerAddress = localPlayerPtr
            };

            long lifeComponent = ResolveComponent(localPlayerPtr, "Life");
            if (lifeComponent != 0)
            {
                VitalStruct? health = _processReader.ReadStructure<VitalStruct>(lifeComponent + Poe2Offsets.Life.Health);
                VitalStruct? mana = _processReader.ReadStructure<VitalStruct>(lifeComponent + Poe2Offsets.Life.Mana);
                VitalStruct? energyShield = _processReader.ReadStructure<VitalStruct>(lifeComponent + Poe2Offsets.Life.EnergyShield);

                if (health is { } hp && hp.LooksValid())
                {
                    data.Health = hp.Current;
                    data.MaxHealth = Unreserved(hp);
                    data.HasVitals = true;
                }

                if (mana is { } mp && mp.LooksValid())
                {
                    data.Mana = mp.Current;
                    data.MaxMana = Unreserved(mp);
                    data.HasVitals = true;
                }

                if (energyShield is { } es && es.LooksValid())
                {
                    data.EnergyShield = Math.Max(0, es.Current);
                    data.MaxEnergyShield = Math.Max(0, Unreserved(es));
                    data.HasVitals = true;
                }
            }

            long renderComponent = ResolveComponent(localPlayerPtr, "Render");
            if (renderComponent != 0)
            {
                Vector3? position = _processReader.ReadStructure<Vector3>(renderComponent + Poe2Offsets.Render.CurrentWorldPosition);
                if (position is { } pos && IsReasonableVector(pos))
                {
                    data.X = pos.X;
                    data.Y = pos.Y;
                    data.Z = pos.Z;
                    data.HasPosition = true;
                }
            }

            return data;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return null;
        }
    }

    public GameSnapshot? ReadSnapshot()
    {
        if (!TryResolve(out long inGameState, out long areaInstance, out long localPlayer))
        {
            return null;
        }

        PlayerData? player = GetPlayerData();
        IReadOnlyList<EntityData> entities = ReadEntities(areaInstance, maxEntities: 1024);
        TerrainData? terrain = ReadTerrain(areaInstance);
        IReadOnlyList<LandmarkData> landmarks = ReadLandmarks(areaInstance);
        float[]? cameraMatrix = ReadCameraMatrix(inGameState);
        (int? mapMonsterCount, string mapMonsterText) = ReadMapMonsterCounter(inGameState);
        InventorySnapshot? inventory = ReadInventorySnapshot(areaInstance);
        UiSnapshot? ui = ReadUiSnapshot(inGameState, maxNodes: 2500);
        return new GameSnapshot(inGameState, areaInstance, localPlayer, player, entities, terrain, landmarks, cameraMatrix, mapMonsterCount, mapMonsterText, inventory, ui);
    }

    public GameSnapshot? ReadCombatSnapshot(bool includeMapMonsterCounter = false, int maxEntities = 2048)
    {
        if (!TryResolve(out long inGameState, out long areaInstance, out long localPlayer))
        {
            return null;
        }

        PlayerData? player = GetPlayerData();
        IReadOnlyList<EntityData> entities = ReadEntities(areaInstance, Math.Clamp(maxEntities, 64, 4096));
        TerrainData? terrain = ReadTerrain(areaInstance);
        IReadOnlyList<LandmarkData> landmarks = ReadLandmarks(areaInstance);
        float[]? cameraMatrix = ReadCameraMatrix(inGameState);
        (int? mapMonsterCount, string mapMonsterText) = includeMapMonsterCounter
            ? ReadMapMonsterCounter(inGameState)
            : (null, string.Empty);

        return new GameSnapshot(
            inGameState,
            areaInstance,
            localPlayer,
            player,
            entities,
            terrain,
            landmarks,
            cameraMatrix,
            mapMonsterCount,
            mapMonsterText,
            null,
            null);
    }

    public InventorySnapshot? ReadInventorySnapshot(long areaInstance = 0)
    {
        if (_processReader is null)
        {
            SetInventoryError("Process reader is not attached.");
            return null;
        }

        if (areaInstance == 0 && !TryResolve(out _, out areaInstance, out _))
        {
            SetInventoryError("Could not resolve area instance for inventory scan.");
            return null;
        }

        long serverData = Ptr(areaInstance + Poe2Offsets.AreaInstance.ServerDataPtr);
        if (serverData == 0)
        {
            SetInventoryError("ServerData pointer is not resolved.");
            return null;
        }

        long serverDataStructure = ResolveServerDataStructure(serverData);
        if (serverDataStructure == 0)
        {
            SetInventoryError("ServerDataStructure was not resolved from PlayerServerData vector.");
            return null;
        }

        if (!TryFindPlayerInventoriesVector(serverDataStructure, out StdVector vector, out int inventoryCount, out int inventoryVectorOffset))
        {
            SetInventoryError("PlayerInventories vector was not located in ServerDataStructure.");
            return null;
        }

        long first = vector.First.ToInt64();
        long last = vector.Last.ToInt64();
        long count = (last - first) / Poe2Offsets.InventoryArrayEntry.Stride;
        if (!IsPlausiblePointer(first) || count != inventoryCount || count is <= 0 or > 400)
        {
            SetInventoryError($"PlayerInventories vector looks invalid: sdStruct=0x{serverDataStructure:X}, off=0x{inventoryVectorOffset:X}, first=0x{first:X}, count={count}.");
            return null;
        }

        long inventory = 0;
        for (long i = 0; i < count; i++)
        {
            long entry = first + (i * Poe2Offsets.InventoryArrayEntry.Stride);
            int? id = _processReader.ReadStructure<int>(entry + Poe2Offsets.InventoryArrayEntry.Id);
            long candidate = Ptr(entry + Poe2Offsets.InventoryArrayEntry.InventoryPtr);
            if (id == Poe2Offsets.Inventory.MainInventoryId && candidate != 0)
            {
                inventory = candidate;
                break;
            }
        }

        if (inventory == 0)
        {
            SetInventoryError("MainInventory id=1 was not found.");
            return null;
        }

        Int2? totalBoxes = _processReader.ReadStructure<Int2>(inventory + Poe2Offsets.Inventory.TotalBoxes);
        int width = totalBoxes?.X is > 0 and <= 24 ? totalBoxes.Value.X : 12;
        int height = totalBoxes?.Y is > 0 and <= 12 ? totalBoxes.Value.Y : 5;
        int requestCounter = _processReader.ReadStructure<int>(inventory + Poe2Offsets.Inventory.RequestCounter) ?? 0;

        StdVector? itemVector = _processReader.ReadStructure<StdVector>(inventory + Poe2Offsets.Inventory.ItemList);
        if (itemVector is not { } items)
        {
            SetInventoryError("MainInventory item vector is not readable.");
            return new InventorySnapshot(inventory, width, height, requestCounter, []);
        }

        long itemFirst = items.First.ToInt64();
        long itemLast = items.Last.ToInt64();
        long itemCount = (itemLast - itemFirst) / nint.Size;
        if (!IsPlausiblePointer(itemFirst) || itemCount is < 0 or > 300)
        {
            SetInventoryError($"Inventory item vector looks invalid: first=0x{itemFirst:X}, count={itemCount}.");
            return new InventorySnapshot(inventory, width, height, requestCounter, []);
        }

        List<InventoryItemData> result = [];
        HashSet<long> seenItems = [];
        byte[]? itemPointers = _processReader.ReadMemory(itemFirst, (int)(itemCount * nint.Size));
        if (itemPointers is null)
        {
            SetInventoryError("Inventory item pointer array is not readable.");
            return new InventorySnapshot(inventory, width, height, requestCounter, []);
        }

        for (int i = 0; i + nint.Size <= itemPointers.Length; i += nint.Size)
        {
            long inventoryItem = nint.Size == 8
                ? BitConverter.ToInt64(itemPointers, i)
                : BitConverter.ToInt32(itemPointers, i);
            if (!IsPlausiblePointer(inventoryItem))
            {
                continue;
            }

            long itemEntity = Ptr(inventoryItem + Poe2Offsets.InventoryItem.ItemEntityPtr);
            if (itemEntity == 0 || !seenItems.Add(itemEntity))
            {
                continue;
            }

            Int2? start = _processReader.ReadStructure<Int2>(inventoryItem + Poe2Offsets.InventoryItem.SlotStart);
            Int2? end = _processReader.ReadStructure<Int2>(inventoryItem + Poe2Offsets.InventoryItem.SlotEnd);
            if (start is null || end is null)
            {
                continue;
            }

            int startX = Math.Clamp(start.Value.X, 0, Math.Max(0, width - 1));
            int startY = Math.Clamp(start.Value.Y, 0, Math.Max(0, height - 1));
            // SlotEnd is stored EXCLUSIVE (start + size), so a 1x1 item has End = Start + 1.
            // Subtract 1 to make it inclusive, otherwise every item over-counts a cell and the
            // computed cell center drifts to the cell boundary (off-center hover).
            int endX = Math.Clamp(end.Value.X - 1, startX, Math.Max(0, width - 1));
            int endY = Math.Clamp(end.Value.Y - 1, startY, Math.Max(0, height - 1));
            result.Add(new InventoryItemData(itemEntity, ReadMetadata(itemEntity), ReadRarity(itemEntity), 0, startX, startY, endX, endY));
        }

        LastInventoryError = string.Empty;
        LastError = string.Empty;
        return new InventorySnapshot(inventory, width, height, requestCounter, result);
    }

    public UiSnapshot? ReadUiSnapshot(long inGameState = 0, int maxNodes = 4000)
    {
        if (_processReader is null)
        {
            LastError = "Process reader is not attached.";
            return null;
        }

        if (inGameState == 0 && !TryResolve(out inGameState, out _, out _))
        {
            LastError = "Could not resolve InGameState for UI scan.";
            return null;
        }

        long root = Ptr(inGameState + Poe2Offsets.InGameState.UiRoot);
        if (root == 0)
        {
            LastError = "UI root is not resolved.";
            return null;
        }

        List<UiElementData> elements = [];
        Queue<long> queue = new();
        HashSet<long> visited = [];
        queue.Enqueue(root);

        while (queue.Count > 0 && elements.Count < maxNodes)
        {
            long node = queue.Dequeue();
            if (node == 0 || !visited.Add(node))
            {
                continue;
            }

            int flags = _processReader.ReadStructure<int>(node + Poe2Offsets.UiElement.Flags) ?? 0;
            Vector2 relative = _processReader.ReadStructure<Vector2>(node + Poe2Offsets.UiElement.RelativePosition) ?? default;
            float width = _processReader.ReadStructure<float>(node + Poe2Offsets.UiElement.SizeWidth) ?? 0;
            float height = _processReader.ReadStructure<float>(node + Poe2Offsets.UiElement.SizeHeight) ?? 0;
            long parent = Ptr(node + Poe2Offsets.UiElement.Parent);
            string text = ReadStdWString(node + Poe2Offsets.UiElement.Text);
            bool visible = (flags & (1 << Poe2Offsets.UiElement.FlagVisibleBit)) != 0;
            elements.Add(new UiElementData(node, parent, visible, text, relative.X, relative.Y, width, height));

            StdVector? children = _processReader.ReadStructure<StdVector>(node + Poe2Offsets.UiElement.Children);
            if (children is null)
            {
                continue;
            }

            long first = children.Value.First.ToInt64();
            long last = children.Value.Last.ToInt64();
            long childCount = (last - first) / nint.Size;
            if (!IsPlausiblePointer(first) || childCount is <= 0 or > 1024)
            {
                continue;
            }

            byte[]? childBytes = _processReader.ReadMemory(first, (int)(childCount * nint.Size));
            if (childBytes is null)
            {
                continue;
            }

            for (int i = 0; i + nint.Size <= childBytes.Length; i += nint.Size)
            {
                long child = nint.Size == 8
                    ? BitConverter.ToInt64(childBytes, i)
                    : BitConverter.ToInt32(childBytes, i);
                if (IsPlausiblePointer(child) && !visited.Contains(child))
                {
                    queue.Enqueue(child);
                }
            }
        }

        LastError = string.Empty;
        return new UiSnapshot(elements);
    }

    /// <summary>
    /// Detect the player backpack grid as an on-screen rectangle (window-local pixels) straight from the
    /// in-memory UiElement tree, so cell->pixel mapping no longer depends on hand-tuned fractions.
    /// Returns the best grid plus the ranked container candidates for diagnostics.
    /// </summary>
    public (GridGeometry? Grid, IReadOnlyList<UiScreenElement> Candidates) DetectInventoryGrid(
        float winW, float winH, int columns, int rows)
    {
        if (_processReader is null)
        {
            LastError = "Process reader is not attached.";
            return (null, []);
        }

        if (!TryResolve(out long inGameState, out _, out _))
        {
            LastError = "Could not resolve InGameState for inventory grid detection.";
            return (null, []);
        }

        long root = Ptr(inGameState + Poe2Offsets.InGameState.UiRoot);
        if (root == 0)
        {
            LastError = "UI root is not resolved.";
            return (null, []);
        }

        Poe2UiGeometry geometry = new(_processReader);
        GridGeometry? grid = geometry.DetectInventoryGrid(root, winW, winH, columns, rows, out List<UiScreenElement> candidates);
        LastError = string.Empty;
        return (grid, candidates);
    }

    /// <summary>
    /// Read the live atlas map nodes from memory (name, state, content, grid coord, on-screen rect) plus
    /// the player's current node grid coord. Lets map selection pick a real node by data and click its
    /// exact projected position instead of matching UI text labels. Empty when the atlas is closed.
    /// </summary>
    public (IReadOnlyList<AtlasNodeData> Nodes, (int X, int Y)? Current,
        IReadOnlyDictionary<(int X, int Y), IReadOnlyList<(int X, int Y)>> Connections) ReadAtlasNodes(float winW, float winH)
    {
        if (_processReader is null || !TryResolve(out long inGameState, out _, out _))
        {
            return ([], null, new Dictionary<(int X, int Y), IReadOnlyList<(int X, int Y)>>());
        }

        _atlasNodes ??= new Poe2AtlasNodes(_processReader);
        IReadOnlyList<AtlasNodeData> nodes = _atlasNodes.ReadNodes(inGameState, winW, winH);
        return (nodes, _atlasNodes.CurrentNodeGrid(), _atlasNodes.ConnectionsSnapshot());
    }

    /// <summary>
    /// Find a visible UI text button (e.g. "Traverse") in the full UI tree from memory and return its
    /// window-local pixel center for a precise click. Walks more of the tree than the per-tick snapshot
    /// and projects the matched element to absolute screen coordinates.
    /// </summary>
    public (float X, float Y)? FindUiButtonCenter(float winW, float winH, params string[] texts)
    {
        if (_processReader is null || !TryResolve(out long inGameState, out _, out _))
        {
            return null;
        }

        long root = Ptr(inGameState + Poe2Offsets.InGameState.UiRoot);
        if (root == 0)
        {
            return null;
        }

        Poe2UiGeometry geometry = new(_processReader);
        return geometry.FindVisibleTextCenter(root, winW, winH, texts);
    }

    public (float X, float Y)? FindNearestUiTextCenter(float winW, float winH, float targetX, float targetY, float radius)
    {
        if (_processReader is null || !TryResolve(out long inGameState, out _, out _))
        {
            return null;
        }

        long root = Ptr(inGameState + Poe2Offsets.InGameState.UiRoot);
        if (root == 0)
        {
            return null;
        }

        Poe2UiGeometry geometry = new(_processReader);
        return geometry.FindNearestVisibleTextCenter(root, winW, winH, targetX, targetY, radius);
    }

    public bool TryResolve(out long inGameState, out long areaInstance, out long localPlayer)
    {
        inGameState = 0;
        areaInstance = 0;
        localPlayer = 0;

        if (_processReader is null)
        {
            return false;
        }

        if (GameStateAddress == 0 && InGameStateAddress == 0)
        {
            ScanPatterns();
        }

        long gameState = GameStateAddress == 0 ? 0 : _processReader.ReadPointer(GameStateAddress);
        foreach (long candidate in EnumerateInGameStateCandidates(gameState))
        {
            if (TryResolveFromInGameState(candidate, out areaInstance, out localPlayer))
            {
                inGameState = candidate;
                return true;
            }
        }

        long directInGameState = InGameStateAddress == 0 ? 0 : _processReader.ReadPointer(InGameStateAddress);
        if (TryResolveFromInGameState(directInGameState, out areaInstance, out localPlayer))
        {
            inGameState = directInGameState;
            return true;
        }

        return false;
    }

    public void Disconnect()
    {
        _processReader?.Detach();
        _processReader?.Dispose();
        _processReader = null;
        _aobScanner = null;
        IsConnected = false;
        GameStateAddress = 0;
        InGameStateAddress = 0;
        GameStateTargetAddress = 0;
        InGameStateTargetAddress = 0;
        ModuleBaseAddress = 0;
        ModuleSize = 0;
        _terrainCacheArea = 0;
        _terrainCache = null;
        _atlasNodes = null;
    }

    public void Dispose()
    {
        Disconnect();
    }

    private long ScanFirst(IEnumerable<AobPattern> patterns, ICollection<PatternScanHit> hits)
    {
        if (_aobScanner is null)
        {
            return 0;
        }

        long firstAddress = 0;
        foreach (AobPattern pattern in patterns)
        {
            AobScanResult result = _aobScanner.Scan(pattern);
            long target = result.IsFound && _processReader is not null
                ? _processReader.ReadPointer(result.Address)
                : 0;
            hits.Add(new PatternScanHit(result.Description, result.Address, target, result.IsFound));

            if (firstAddress == 0 && result.IsFound)
            {
                firstAddress = result.Address;
            }
        }

        return firstAddress;
    }

    private IEnumerable<long> EnumerateInGameStateCandidates(long gameState)
    {
        if (_processReader is null || gameState == 0)
        {
            yield break;
        }

        long vectorFirst = Ptr(gameState + Poe2Offsets.GameState.CurrentStatePtr);
        if (vectorFirst != 0)
        {
            long current = Ptr(vectorFirst);
            if (current != 0)
            {
                yield return current;
            }
        }

        for (int i = 0; i < Poe2Offsets.GameState.StateSlotCount; i++)
        {
            long state = Ptr(gameState + Poe2Offsets.GameState.States + (i * Poe2Offsets.GameState.StateSlotStride));
            if (state != 0)
            {
                yield return state;
            }
        }
    }

    private bool TryResolveFromInGameState(long candidate, out long areaInstance, out long localPlayer)
    {
        areaInstance = 0;
        localPlayer = 0;

        if (_processReader is null || candidate == 0)
        {
            return false;
        }

        areaInstance = Ptr(candidate + Poe2Offsets.InGameState.AreaInstanceData);
        if (areaInstance == 0)
        {
            return false;
        }

        localPlayer = Ptr(areaInstance + Poe2Offsets.AreaInstance.LocalPlayer);
        return localPlayer != 0;
    }

    private IReadOnlyList<EntityData> ReadEntities(long areaInstance, int maxEntities)
    {
        List<EntityData> entities = [];
        if (_processReader is null || areaInstance == 0 || maxEntities <= 0)
        {
            return entities;
        }

        long head = Ptr(areaInstance + Poe2Offsets.AreaInstance.AwakeEntities);
        int? size = _processReader.ReadStructure<int>(areaInstance + Poe2Offsets.AreaInstance.AwakeEntities + 8);
        if (head == 0 || size is null or <= 0 or > 100_000)
        {
            return entities;
        }

        long root = Ptr(head + Poe2Offsets.StdMapNode.Parent);
        Queue<long> queue = new();
        HashSet<long> visited = [];
        queue.Enqueue(root);

        while (queue.Count > 0 && visited.Count < 200_000 && entities.Count < maxEntities)
        {
            long node = queue.Dequeue();
            if (node == 0 || node == head || !visited.Add(node))
            {
                continue;
            }

            byte[]? nodeData = _processReader.ReadMemory(node, _mapNodeBuffer.Length);
            if (nodeData is null || nodeData.Length < _mapNodeBuffer.Length)
            {
                continue;
            }

            if (nodeData[Poe2Offsets.StdMapNode.IsNil] != 0)
            {
                continue;
            }

            long left = BitConverter.ToInt64(nodeData, Poe2Offsets.StdMapNode.Left);
            long right = BitConverter.ToInt64(nodeData, Poe2Offsets.StdMapNode.Right);
            if (IsPlausiblePointer(left))
            {
                queue.Enqueue(left);
            }

            if (IsPlausiblePointer(right))
            {
                queue.Enqueue(right);
            }

            uint id = BitConverter.ToUInt32(nodeData, Poe2Offsets.StdMapNode.KeyId);
            if (id >= Poe2Offsets.EntityList.VisualIdThreshold)
            {
                continue;
            }

            long entity = BitConverter.ToInt64(nodeData, Poe2Offsets.StdMapNode.ValueEntityPtr);
            if (!IsPlausiblePointer(entity))
            {
                continue;
            }

            string metadata = ReadMetadata(entity);
            string category = Categorize(metadata);
            int reaction = ReadReaction(entity);
            bool isFriendly = (reaction & 0x7F) == 1;
            if (category == "Monster" && isFriendly)
            {
                category = "Other";
            }

            long render = ResolveComponent(entity, "Render");
            if (render == 0)
            {
                continue;
            }

            Vector3? position = _processReader.ReadStructure<Vector3>(render + Poe2Offsets.Render.CurrentWorldPosition);
            if (position is not { } pos || !IsReasonableVector(pos))
            {
                continue;
            }

            EntityLife life = category == "Monster"
                ? ReadEntityLife(entity)
                : EntityLife.Unknown;
            int rarity = ReadRarity(entity);
            entities.Add(new EntityData(id, entity, metadata, category, pos.X, pos.Y, pos.Z, life.HasLife, life.IsAlive, life.Current, life.Max, reaction, rarity, life.EnergyShield, life.MaxEnergyShield));
        }

        return entities;
    }

    private int ReadReaction(long entity)
    {
        if (_processReader is null || entity == 0)
        {
            return -1;
        }

        long positioned = ResolveComponent(entity, "Positioned");
        if (positioned == 0)
        {
            return -1;
        }

        return _processReader.ReadStructure<int>(positioned + Poe2Offsets.Positioned.Reaction) ?? -1;
    }

    private int ReadRarity(long entity)
    {
        if (_processReader is null || entity == 0)
        {
            return -1;
        }

        long magicProperties = ResolveComponent(entity, "ObjectMagicProperties");
        if (magicProperties == 0)
        {
            return -1;
        }

        int rarity = _processReader.ReadStructure<int>(magicProperties + Poe2Offsets.ObjectMagicProperties.Rarity) ?? -1;
        return rarity is >= 0 and <= 3 ? rarity : -1;
    }

    private EntityLife ReadEntityLife(long entity)
    {
        if (_processReader is null || entity == 0)
        {
            return EntityLife.Unknown;
        }

        long lifeComponent = ResolveComponent(entity, "Life");
        if (lifeComponent == 0)
        {
            return EntityLife.Unknown;
        }

        VitalStruct? health = _processReader.ReadStructure<VitalStruct>(lifeComponent + Poe2Offsets.Life.Health);
        if (health is not { } hp || !hp.LooksValid())
        {
            return EntityLife.Unknown;
        }

        // Energy shield is a separate vital that absorbs damage BEFORE life. Read it too so combat can tell
        // "ES is being chewed through" from "truly invulnerable / no damage landing".
        VitalStruct? es = _processReader.ReadStructure<VitalStruct>(lifeComponent + Poe2Offsets.Life.EnergyShield);
        int esCurrent = es is { } shield && shield.LooksValid() ? shield.Current : 0;
        int esMax = es is { } shield2 && shield2.LooksValid() ? shield2.Max : 0;

        return new EntityLife(true, hp.Current > 0 && hp.Max > 0, hp.Current, hp.Max, esCurrent, esMax);
    }

    private TerrainData? ReadTerrain(long areaInstance)
    {
        if (_processReader is null || areaInstance == 0)
        {
            return null;
        }

        if (_terrainCacheArea == areaInstance && _terrainCache is not null)
        {
            return _terrainCache;
        }

        if (_terrainCacheArea != areaInstance)
        {
            _terrainCacheArea = 0;
            _terrainCache = null;
        }

        long terrain = areaInstance + Poe2Offsets.AreaInstance.TerrainMetadata;
        long first = Ptr(terrain + Poe2Offsets.Terrain.GridWalkableData);
        long last = Ptr(terrain + Poe2Offsets.Terrain.GridWalkableData + nint.Size);
        int? bytesPerRow = _processReader.ReadStructure<int>(terrain + Poe2Offsets.Terrain.BytesPerRow);
        long totalBytes = last - first;

        if (first == 0 || last == 0 || bytesPerRow is null or <= 0 or > 65_536)
        {
            return null;
        }

        if (totalBytes <= 0 || totalBytes > 64L * 1024L * 1024L)
        {
            return null;
        }

        int rows = (int)(totalBytes / bytesPerRow.Value);
        int width = bytesPerRow.Value * 2;
        if (rows <= 0 || rows > 65_536 || width <= 0)
        {
            return null;
        }

        byte[]? raw = _processReader.ReadMemory(first, (int)totalBytes);
        if (raw is null || raw.Length < totalBytes)
        {
            return null;
        }

        byte[] walkable = new byte[width * rows];
        for (int y = 0; y < rows; y++)
        {
            int rowBase = y * bytesPerRow.Value;
            int outputBase = y * width;
            for (int x = 0; x < width; x++)
            {
                byte packed = raw[rowBase + (x >> 1)];
                int nibble = (x & 1) == 0 ? packed & 0x0F : packed >> 4;
                walkable[outputBase + x] = (byte)(nibble != 0 ? 1 : 0);
            }
        }

        _terrainCacheArea = areaInstance;
        _terrainCache = new TerrainData(walkable, width, rows, bytesPerRow.Value);
        return _terrainCache;
    }

    private IReadOnlyList<LandmarkData> ReadLandmarks(long areaInstance)
    {
        if (_processReader is null || areaInstance == 0)
        {
            return [];
        }

        if (_landmarkCacheArea == areaInstance)
        {
            return _landmarkCache;
        }

        _landmarkCacheArea = areaInstance;
        _landmarkCache = ScanLandmarks(areaInstance);
        return _landmarkCache;
    }

    private IReadOnlyList<LandmarkData> ScanLandmarks(long areaInstance)
    {
        if (_processReader is null)
        {
            return [];
        }

        string areaCode = ReadAreaCode(areaInstance);
        long terrain = areaInstance + Poe2Offsets.AreaInstance.TerrainMetadata;
        long first = Ptr(terrain + Poe2Offsets.Terrain.TileDetailsPtr);
        long last = Ptr(terrain + Poe2Offsets.Terrain.TileDetailsPtr + nint.Size);
        long? tilesX = _processReader.ReadStructure<long>(terrain + Poe2Offsets.Terrain.TotalTiles);
        long count = first == 0 || last == 0 ? 0 : (last - first) / Poe2Offsets.TileStructureSize;
        if (tilesX is null or <= 0 || count is <= 0 or > 1_000_000)
        {
            return [];
        }

        Dictionary<long, string?> pathCache = [];
        Dictionary<string, List<(int X, int Y)>> cellsByPath = new(StringComparer.OrdinalIgnoreCase);
        for (long i = 0; i < count; i++)
        {
            long tile = first + (i * Poe2Offsets.TileStructureSize);
            long tgtFile = Ptr(tile + Poe2Offsets.TileStructure.TgtFilePtr);
            if (tgtFile == 0)
            {
                continue;
            }

            if (!pathCache.TryGetValue(tgtFile, out string? path))
            {
                string candidate = ReadStdWString(tgtFile + Poe2Offsets.TgtFileStruct.TgtPath);
                // Keep curated landmarks (nice names) AND any boss-arena tile detected generically, so the
                // boss arena is always a navigable landmark even on maps with no curated entry.
                bool keep = CustomLandmarkData.TryMatch(areaCode, candidate) is not null || IsBossArenaTilePath(candidate);
                path = keep ? candidate : null;
                pathCache[tgtFile] = path;
            }

            if (path is null)
            {
                continue;
            }

            if (!cellsByPath.TryGetValue(path, out List<(int X, int Y)>? cells))
            {
                cells = [];
                cellsByPath[path] = cells;
            }

            cells.Add(((int)(i % tilesX.Value), (int)(i / tilesX.Value)));
        }

        List<LandmarkData> result = [];
        foreach ((string path, List<(int X, int Y)> cells) in cellsByPath)
        {
            string label = CustomLandmarkData.TryMatch(areaCode, path) ?? LandmarkName(path);
            foreach (List<(int X, int Y)> cluster in ClusterTiles(cells, gap: 2))
            {
                double sx = 0;
                double sy = 0;
                foreach ((int x, int y) in cluster)
                {
                    sx += x;
                    sy += y;
                }

                float gridX = (float)(sx / cluster.Count * Poe2Offsets.Terrain.TileGridCells);
                float gridY = (float)(sy / cluster.Count * Poe2Offsets.Terrain.TileGridCells);
                result.Add(new LandmarkData(
                    LandmarkName(path),
                    label,
                    path,
                    gridX * Poe2Offsets.WorldToGridRatio,
                    gridY * Poe2Offsets.WorldToGridRatio,
                    cluster.Count));
            }
        }

        return result;
    }

    private string ReadAreaCode(long areaInstance)
    {
        if (_processReader is null)
        {
            return string.Empty;
        }

        long info = Ptr(areaInstance + Poe2Offsets.AreaInstance.AreaInfoPtr);
        long code = Ptr(info);
        return code == 0 ? string.Empty : _processReader.ReadStringUtf16(code, 96);
    }

    private static List<List<(int X, int Y)>> ClusterTiles(List<(int X, int Y)> cells, int gap)
    {
        HashSet<(int X, int Y)> set = new(cells);
        HashSet<(int X, int Y)> visited = [];
        Queue<(int X, int Y)> queue = [];
        List<List<(int X, int Y)>> clusters = [];
        foreach ((int X, int Y) start in cells)
        {
            if (!visited.Add(start))
            {
                continue;
            }

            List<(int X, int Y)> cluster = [];
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                (int cx, int cy) = queue.Dequeue();
                cluster.Add((cx, cy));
                for (int dx = -gap; dx <= gap; dx++)
                {
                    for (int dy = -gap; dy <= gap; dy++)
                    {
                        (int X, int Y) next = (cx + dx, cy + dy);
                        if (set.Contains(next) && visited.Add(next))
                        {
                            queue.Enqueue(next);
                        }
                    }
                }
            }

            clusters.Add(cluster);
        }

        return clusters;
    }

    /// <summary>
    /// Generic boss-arena tile detector: PoE2 boss arenas consistently carry "Arena" or "Boss" in their
    /// .tdt terrain path (HagWitchArena, BossArena, MausoleumBoss_ArenaFloor, Bosstile, Chapel_boss,
    /// ArenaTop, …). This lets Boss Rush navigate to the arena on ANY map without a curated landmark entry.
    /// Note: "AreaTransitions" does NOT match ("Area", no 'n').
    /// </summary>
    private static bool IsBossArenaTilePath(string tgtPath)
    {
        if (string.IsNullOrEmpty(tgtPath) ||
            !tgtPath.Contains("/Terrain/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return tgtPath.Contains("Arena", StringComparison.OrdinalIgnoreCase) ||
               tgtPath.Contains("Boss", StringComparison.OrdinalIgnoreCase);
    }

    private static string LandmarkName(string path)
    {
        int slash = path.LastIndexOf('/');
        string name = slash >= 0 ? path[(slash + 1)..] : path;
        return name.EndsWith(".tdt", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private float[]? ReadCameraMatrix(long inGameState)
    {
        if (_processReader is null || inGameState == 0)
        {
            return null;
        }

        long camera = Ptr(inGameState + Poe2Offsets.InGameState.Camera);
        if (camera == 0)
        {
            return null;
        }

        byte[]? bytes = _processReader.ReadMemory(camera + Poe2Offsets.Camera.WorldToScreenMatrix, 64);
        if (bytes is null || bytes.Length < 64)
        {
            return null;
        }

        float[] matrix = new float[16];
        Buffer.BlockCopy(bytes, 0, matrix, 0, 64);
        return matrix;
    }

    private (int? Count, string Text) ReadMapMonsterCounter(long inGameState)
    {
        if (_processReader is null || inGameState == 0)
        {
            return (null, string.Empty);
        }

        long root = Ptr(inGameState + Poe2Offsets.InGameState.UiRoot);
        if (root == 0)
        {
            return (null, string.Empty);
        }

        Queue<long> queue = new();
        HashSet<long> visited = [];
        queue.Enqueue(root);

        while (queue.Count > 0 && visited.Count < 8_000)
        {
            long node = queue.Dequeue();
            if (node == 0 || !visited.Add(node))
            {
                continue;
            }

            string text = ReadStdWString(node + Poe2Offsets.UiElement.Text);
            if (TryParseMonsterCounterText(text, out int count, out string normalizedText))
            {
                return (count, normalizedText);
            }

            StdVector? children = _processReader.ReadStructure<StdVector>(node + Poe2Offsets.UiElement.Children);
            if (children is null)
            {
                continue;
            }

            long first = children.Value.First.ToInt64();
            long last = children.Value.Last.ToInt64();
            long childCount = (last - first) / nint.Size;
            if (!IsPlausiblePointer(first) || childCount is <= 0 or > 512)
            {
                continue;
            }

            byte[]? childBytes = _processReader.ReadMemory(first, (int)(childCount * nint.Size));
            if (childBytes is null)
            {
                continue;
            }

            for (int i = 0; i + nint.Size <= childBytes.Length; i += nint.Size)
            {
                long child = nint.Size == 8
                    ? BitConverter.ToInt64(childBytes, i)
                    : BitConverter.ToInt32(childBytes, i);
                if (IsPlausiblePointer(child) && !visited.Contains(child))
                {
                    queue.Enqueue(child);
                }
            }
        }

        return (null, string.Empty);
    }

    private static bool TryParseMonsterCounterText(string text, out int count, out string normalizedText)
    {
        count = 0;
        normalizedText = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string compact = Regex.Replace(text, @"\s+", " ").Trim();
        if (Regex.IsMatch(compact, @"more\s+than\s+50\s+monsters?\s+remain", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(compact, @"больше\s+50.{0,24}монстр", RegexOptions.IgnoreCase))
        {
            count = 51;
            normalizedText = compact;
            return true;
        }

        Match match = ExactMonsterCounterRegex.Match(compact);
        if (!match.Success)
        {
            match = RussianMonsterCounterRegex.Match(compact);
        }

        if (!match.Success || !int.TryParse(match.Groups["count"].Value, out int parsed))
        {
            return false;
        }

        count = parsed;
        normalizedText = compact;
        return true;
    }

    private long ResolveComponent(long entity, string name)
    {
        if (_processReader is null || entity == 0)
        {
            return 0;
        }

        long details = Ptr(entity + Poe2Offsets.Entity.EntityDetailsPtr);
        if (details == 0)
        {
            return 0;
        }

        long lookup = Ptr(details + Poe2Offsets.EntityDetails.ComponentLookupPtr);
        if (lookup == 0)
        {
            return 0;
        }

        StdVector? componentList = _processReader.ReadStructure<StdVector>(entity + Poe2Offsets.Entity.ComponentList);
        if (componentList is null)
        {
            return 0;
        }

        long componentCount = (componentList.Value.Last.ToInt64() - componentList.Value.First.ToInt64()) / nint.Size;
        if (componentCount is <= 0 or > 256)
        {
            return 0;
        }

        long bucketFirst = Ptr(lookup + Poe2Offsets.ComponentLookUp.NameAndIndexBucket);
        long bucketLast = Ptr(lookup + Poe2Offsets.ComponentLookUp.NameAndIndexBucket + nint.Size);
        long entryCount = (bucketLast - bucketFirst) / Poe2Offsets.ComponentLookUp.EntryStride;

        if (bucketFirst == 0 || entryCount is <= 0 or > 256)
        {
            return 0;
        }

        for (long i = 0; i < entryCount; i++)
        {
            long entry = bucketFirst + (i * Poe2Offsets.ComponentLookUp.EntryStride);
            long namePtr = Ptr(entry);
            int? index = _processReader.ReadStructure<int>(entry + nint.Size);
            if (namePtr == 0 || index is null || index < 0 || index >= componentCount)
            {
                continue;
            }

            if (!_processReader.ReadStringUtf8(namePtr, 64).Equals(name, StringComparison.Ordinal))
            {
                continue;
            }

            return Ptr(componentList.Value.First.ToInt64() + (index.Value * nint.Size));
        }

        return 0;
    }

    private string ReadMetadata(long entity)
    {
        if (_processReader is null || entity == 0)
        {
            return string.Empty;
        }

        long details = Ptr(entity + Poe2Offsets.Entity.EntityDetailsPtr);
        return details == 0 ? string.Empty : ReadStdWString(details + Poe2Offsets.EntityDetails.Name);
    }

    private string ReadStdWString(long address)
    {
        if (_processReader is null || address == 0)
        {
            return string.Empty;
        }

        int? length = _processReader.ReadStructure<int>(address + 0x10);
        if (length is null or <= 0 or > 1024)
        {
            return string.Empty;
        }

        if (length < 8)
        {
            return _processReader.ReadStringUtf16(address, length.Value);
        }

        long stringPtr = Ptr(address);
        return stringPtr == 0 ? string.Empty : _processReader.ReadStringUtf16(stringPtr, length.Value);
    }

    private long ResolveServerDataStructure(long serverData)
    {
        long TryOffset(int offset)
        {
            if (_processReader is null)
            {
                return 0;
            }

            StdVector? vector = _processReader.ReadStructure<StdVector>(serverData + offset);
            if (vector is not { } playerServerData)
            {
                return 0;
            }

            long first = playerServerData.First.ToInt64();
            long last = playerServerData.Last.ToInt64();
            long span = last - first;
            if (!IsPlausiblePointer(first) || span < nint.Size || span > 0x4000)
            {
                return 0;
            }

            long candidate = Ptr(first);
            return candidate != 0 && TryFindPlayerInventoriesVector(candidate, out _, out _, out _)
                ? candidate
                : 0;
        }

        long direct = TryOffset(Poe2Offsets.ServerData.PlayerServerData);
        if (direct != 0)
        {
            return direct;
        }

        for (int offset = 0x10; offset <= 0x200; offset += 8)
        {
            long candidate = TryOffset(offset);
            if (candidate != 0)
            {
                return candidate;
            }
        }

        return 0;
    }

    private bool TryFindPlayerInventoriesVector(long serverDataStructure, out StdVector vector, out int count, out int offset)
    {
        (int Score, StdVector Vector, int Count) ScoreOffset(int candidateOffset)
        {
            if (_processReader is null)
            {
                return (-1, default, 0);
            }

            StdVector? candidate = _processReader.ReadStructure<StdVector>(serverDataStructure + candidateOffset);
            if (candidate is not { } v)
            {
                return (-1, default, 0);
            }

            long first = v.First.ToInt64();
            long last = v.Last.ToInt64();
            long span = last - first;
            if (!IsPlausiblePointer(first) || span <= 0 || span % Poe2Offsets.InventoryArrayEntry.Stride != 0)
            {
                return (-1, default, 0);
            }

            int vectorCount = (int)(span / Poe2Offsets.InventoryArrayEntry.Stride);
            if (vectorCount is <= 0 or > 400)
            {
                return (-1, default, 0);
            }

            int good = 0;
            int check = Math.Min(vectorCount, 32);
            for (int i = 0; i < check; i++)
            {
                long entry = first + (i * Poe2Offsets.InventoryArrayEntry.Stride);
                int? id = _processReader.ReadStructure<int>(entry + Poe2Offsets.InventoryArrayEntry.Id);
                if (id is < 1 or > 200)
                {
                    continue;
                }

                long p0 = Ptr(entry + Poe2Offsets.InventoryArrayEntry.InventoryPtr);
                long p1 = Ptr(entry + Poe2Offsets.InventoryArrayEntry.InventoryPtr + 8);
                if (p0 != 0 && p1 == p0 - 0x10)
                {
                    good++;
                }
            }

            return (good, v, vectorCount);
        }

        (int score, StdVector vector, int count) preferred = ScoreOffset(Poe2Offsets.ServerData.PlayerInventories);
        if (preferred.score >= 1)
        {
            vector = preferred.vector;
            count = preferred.count;
            offset = Poe2Offsets.ServerData.PlayerInventories;
            return true;
        }

        int bestScore = -1;
        StdVector bestVector = default;
        int bestCount = 0;
        int bestOffset = -1;
        for (int candidateOffset = 0x100; candidateOffset <= 0x800; candidateOffset += 8)
        {
            (int score, StdVector candidateVector, int candidateCount) = ScoreOffset(candidateOffset);
            if (score > bestScore)
            {
                bestScore = score;
                bestVector = candidateVector;
                bestCount = candidateCount;
                bestOffset = candidateOffset;
            }
        }

        vector = bestVector;
        count = bestCount;
        offset = bestOffset;
        return bestScore >= 2;
    }

    private void SetInventoryError(string message)
    {
        LastInventoryError = message;
        LastError = message;
    }

    private long Ptr(long address)
    {
        if (_processReader is null)
        {
            return 0;
        }

        long pointer = _processReader.ReadPointer(address);
        ulong value = (ulong)pointer;
        return value < 0x10000 || value > 0x7FFFFFFFFFFF ? 0 : pointer;
    }

    private static int Unreserved(VitalStruct vital)
    {
        int reserved = (int)Math.Ceiling(vital.ReservedFraction / 10000f * vital.Max) + vital.ReservedFlat;
        return Math.Max(0, vital.Max - reserved);
    }

    private static bool IsReasonableVector(Vector3 vector)
    {
        return IsReasonableFloat(vector.X) && IsReasonableFloat(vector.Y) && IsReasonableFloat(vector.Z);
    }

    private static bool IsReasonableFloat(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value) && Math.Abs(value) < 10_000_000f;
    }

    private static bool IsPlausiblePointer(long value)
    {
        ulong pointer = (ulong)value;
        return pointer is >= 0x10000 and <= 0x7FFFFFFFFFFF;
    }

    private static string Categorize(string metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata))
        {
            return "Unknown";
        }

        if (metadata.Contains("/NPC/", StringComparison.Ordinal))
        {
            return "Npc";
        }

        if (metadata.Contains("MapDevice", StringComparison.OrdinalIgnoreCase) ||
            metadata.Contains("MappingDevice", StringComparison.OrdinalIgnoreCase) ||
            metadata.Contains("AtlasDevice", StringComparison.OrdinalIgnoreCase))
        {
            return "MapDevice";
        }

        if (metadata.Contains("Stash", StringComparison.OrdinalIgnoreCase))
        {
            return "Stash";
        }

        // Any portal: town portal, return portal, AND the endgame map-device portal (the one you click to
        // enter an activated map). Broadened so EnterPortal can find the map entrance, not just town portals.
        if (metadata.Contains("Portal", StringComparison.OrdinalIgnoreCase))
        {
            return "Portal";
        }

        if (metadata.Contains("Waypoint", StringComparison.OrdinalIgnoreCase))
        {
            return "Waypoint";
        }

        if (metadata.Contains("/Items/", StringComparison.Ordinal) ||
            metadata.Contains("/Currency/", StringComparison.Ordinal) ||
            metadata.Contains("Metadata/Items", StringComparison.Ordinal) ||
            metadata.Contains("WorldItem", StringComparison.OrdinalIgnoreCase) ||
            metadata.Contains("ItemDrop", StringComparison.OrdinalIgnoreCase))
        {
            return "Loot";
        }

        if (IsEncounter(metadata))
        {
            return "Encounter";
        }

        if (metadata.Contains("/Monsters/", StringComparison.Ordinal))
        {
            return IsNonCombat(metadata) ? "Other" : "Monster";
        }

        if (metadata.Contains("/Characters/", StringComparison.Ordinal))
        {
            return "Player";
        }

        if (metadata.Contains("/Chests", StringComparison.Ordinal))
        {
            return "Chest";
        }

        if (metadata.Contains("Transition", StringComparison.Ordinal))
        {
            return "Transition";
        }

        if (metadata.Contains("/Terrain/", StringComparison.Ordinal))
        {
            return "Object";
        }

        return "Other";
    }

    private static bool IsNonCombat(string metadata)
    {
        return metadata.Contains("MonsterMods", StringComparison.Ordinal) ||
               metadata.Contains("Expedition", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("Azmeri", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("Azmiri", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("Barrel", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("Barricade", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("Breakable", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("Crate", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("Destructible", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("Destroyable", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("Fence", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("Palisade", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("Pottery", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("Urn", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("Vase", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("Summoned", StringComparison.Ordinal) ||
               metadata.Contains("Summon", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("Minion", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("Illusion", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("Mirage", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("Clone", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("Decoy", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("Projectile", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("Projectiles", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("SkillEffect", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("SkillEffects", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("GroundEffect", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("AreaOfEffect", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("Explosion", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("Impact", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("Nova", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("Beam", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("Orb", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("Buff", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("Aura", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("/Daemon/", StringComparison.Ordinal) ||
               metadata.Contains("Invisible", StringComparison.Ordinal);
    }

    private static bool IsEncounter(string metadata)
    {
        return metadata.Contains("Expedition2/Expedition2Encounter", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("Metadata/Shrines/", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("StrongBoxes", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("Breach", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("Ritual", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("Delirium", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("Essence", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> CandidateProcessNames(string preferredProcessName)
    {
        if (!string.IsNullOrWhiteSpace(preferredProcessName))
        {
            yield return preferredProcessName;
        }

        foreach (string processName in FallbackProcessNames)
        {
            if (!processName.Equals(preferredProcessName, StringComparison.OrdinalIgnoreCase))
            {
                yield return processName;
            }
        }
    }
}

public sealed record PatternScanHit(string Description, long SlotAddress, long TargetAddress, bool IsFound);

public sealed record PatternScanReport(IReadOnlyList<PatternScanHit> Hits, string Error)
{
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public int FoundCount => Hits.Count(hit => hit.IsFound);
    public int TotalCount => Hits.Count;

    public static PatternScanReport Fail(string error)
    {
        return new PatternScanReport(Array.Empty<PatternScanHit>(), error);
    }
}

public sealed class PlayerData
{
    public long LocalPlayerAddress { get; set; }
    public bool HasVitals { get; set; }
    public bool HasPosition { get; set; }
    public int Health { get; set; }
    public int MaxHealth { get; set; }
    public int EnergyShield { get; set; }
    public int MaxEnergyShield { get; set; }
    public int Ward { get; set; }
    public int MaxWard { get; set; }
    public int Mana { get; set; }
    public int MaxMana { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
}

public sealed record GameSnapshot(
    long InGameStateAddress,
    long AreaInstanceAddress,
    long LocalPlayerAddress,
    PlayerData? Player,
    IReadOnlyList<EntityData> Entities,
    TerrainData? Terrain,
    IReadOnlyList<LandmarkData> Landmarks,
    IReadOnlyList<float>? CameraMatrix,
    int? MapMonsterCount,
    string MapMonsterCountText,
    InventorySnapshot? Inventory,
    UiSnapshot? Ui);

public sealed record EntityData(
    uint Id,
    long Address,
    string Metadata,
    string Category,
    float X,
    float Y,
    float Z,
    bool HasLife,
    bool IsAlive,
    int Health,
    int MaxHealth,
    int Reaction,
    int Rarity,
    int EnergyShield = 0,
    int MaxEnergyShield = 0)
{
    /// <summary>Total effective health pool (life + energy shield) — what actually has to be chewed
    /// through. Used so an ES target isn't mistaken for "invulnerable" while its life sits full.</summary>
    public int EffectiveHealth => Health + EnergyShield;
}

public sealed record EntityLife(bool HasLife, bool IsAlive, int Current, int Max, int EnergyShield = 0, int MaxEnergyShield = 0)
{
    public static EntityLife Unknown { get; } = new(false, true, 0, 0);
}

public sealed record TerrainData(
    byte[] Walkable,
    int Width,
    int Height,
    int BytesPerRow);

public sealed record LandmarkData(
    string Name,
    string Label,
    string TilePath,
    float X,
    float Y,
    int TileCount);
