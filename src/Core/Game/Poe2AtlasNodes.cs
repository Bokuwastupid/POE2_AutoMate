using POE2_AutoMate.Core.Memory;

namespace POE2_AutoMate.Core.Game;

/// <summary>
/// One live atlas node (a UiElement of the atlas-node class). Screen rect is window-local pixels via the
/// shared UiElement projection, so the chosen node can be clicked directly. Ported/condensed from
/// POE2Radar's Poe2Atlas.ReadNodes — only what is needed to pick + click a map node.
/// </summary>
public sealed record AtlasNodeData(
    long Element,
    uint Id,
    uint Content,
    byte State,
    byte Biome,
    byte Flags,
    int GridX,
    int GridY,
    string MapCode,
    string MapName,
    IReadOnlyList<string> Tags,
    bool Visible,
    float ScreenX,
    float ScreenY,
    float ScreenWidth,
    float ScreenHeight)
{
    public bool Unlocked => (Flags & 0x01) != 0;
    public bool Visited => (Flags & 0x02) != 0;
    public bool HasContent => Content != 0;
    public bool IsRealMap => MapCode.StartsWith("Map", StringComparison.Ordinal);
    public float CenterX => ScreenX + (ScreenWidth / 2f);
    public float CenterY => ScreenY + (ScreenHeight / 2f);
    public (int X, int Y) Grid => (GridX, GridY);
}

/// <summary>
/// Read-only atlas-node reader. Detects the node element class + canvas (BFS, biome-spread + size
/// heuristic) and reads each node, including its window-local screen rect for clicking. Detection is
/// cached and self-heals when the atlas panel is recreated.
/// </summary>
public sealed class Poe2AtlasNodes(ProcessReader reader)
{
    private readonly ProcessReader _reader = reader;

    private long _nodeVtable;
    private long _nodeCanvas;
    private long _currentMarker;
    private int _markerFails;
    private long _connectionsCanvas;
    private readonly Dictionary<(int X, int Y), List<(int X, int Y)>> _connections = [];

    public bool IsAtlasOpen(long inGameState)
    {
        long uiRoot = Ptr(inGameState + Poe2Offsets.InGameState.UiRoot);
        return uiRoot != 0 && AtlasPanelOpen(uiRoot);
    }

    /// <summary>Player's current atlas node grid coord (the "you are here" marker), or null.</summary>
    public (int X, int Y)? CurrentNodeGrid()
    {
        long marker = _currentMarker;
        if (marker == 0 || Ptr(marker + Poe2Offsets.UiElement.Self) != marker)
        {
            return null;
        }

        long node = Ptr(marker + Poe2Offsets.AtlasGraph.CurrentMarkerNodePtr);
        if (node == 0)
        {
            return null;
        }

        int? gx = _reader.ReadStructure<int>(node + Poe2Offsets.AtlasNode.GridPos);
        int? gy = _reader.ReadStructure<int>(node + Poe2Offsets.AtlasNode.GridPos + 4);
        return gx is null || gy is null ? null : (gx.Value, gy.Value);
    }

    public IReadOnlyDictionary<(int X, int Y), IReadOnlyList<(int X, int Y)>> ConnectionsSnapshot()
    {
        return _connections.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<(int X, int Y)>)[.. pair.Value]);
    }

    /// <summary>
    /// Read the live atlas nodes with their screen rectangles. Returns empty when the atlas is closed or
    /// the node class can't be located.
    /// </summary>
    public List<AtlasNodeData> ReadNodes(long inGameState, float winW, float winH)
    {
        List<AtlasNodeData> nodes = [];
        long uiRoot = Ptr(inGameState + Poe2Offsets.InGameState.UiRoot);
        if (uiRoot == 0)
        {
            return nodes;
        }

        // The cached fast path skips the full BFS — but the "you are here" marker is only (re)found during a
        // full DetectNodeClass. When the marker has gone stale/missing, force a re-detect (bounded by
        // _markerFails so a genuinely-unfindable marker doesn't re-BFS forever) so CurrentNodeGrid keeps
        // resolving even though the atlas opens at the last pan position rather than centred on the player.
        bool markerValid = _currentMarker != 0 && Ptr(_currentMarker + Poe2Offsets.UiElement.Self) == _currentMarker;
        if (markerValid)
        {
            _markerFails = 0;
        }

        bool needMarkerRefresh = !markerValid && _markerFails < 3;
        if (_nodeCanvas != 0 && _nodeVtable != 0 &&
            Ptr(_nodeCanvas + Poe2Offsets.UiElement.Self) == _nodeCanvas &&
            !needMarkerRefresh)
        {
            if (ReadCanvasNodes(_nodeCanvas, winW, winH, nodes))
            {
                return nodes;
            }
        }

        if (!AtlasPanelOpen(uiRoot))
        {
            return nodes;
        }

        if (!DetectNodeClass(uiRoot))
        {
            return nodes;
        }

        _markerFails = _currentMarker == 0 ? _markerFails + 1 : 0;
        ReadCanvasNodes(_nodeCanvas, winW, winH, nodes);
        return nodes;
    }

    private bool ReadCanvasNodes(long canvas, float winW, float winH, List<AtlasNodeData> outNodes)
    {
        long first = Ptr(canvas + Poe2Offsets.UiElement.Children);
        long last = Ptr(canvas + Poe2Offsets.UiElement.ChildrenEnd);
        if (first == 0 || last < first)
        {
            Invalidate();
            return false;
        }

        long count = (last - first) / 8;
        if (count is <= 0 or > 20000)
        {
            Invalidate();
            return false;
        }

        // First pass: read raw node fields including relative position (+0x118) and the per-node scale
        // (+0x130). Atlas nodes use their OWN projection (uniform scale, pan baked into relPos, canvas at
        // screen origin) — NOT the generic parent-chain math — so we collect, derive the shared zoom from
        // the median node scale, then project: screen = relPos * (winH/1600 * zoom). Ported from POE2Radar
        // RadarApp.AtlasProjection.
        List<RawNode> raw = [];
        for (long i = 0; i < count; i++)
        {
            long el = Ptr(first + (i * 8));
            if (el == 0 || Ptr(el) != _nodeVtable)
            {
                continue;
            }

            uint id = (uint)(_reader.ReadStructure<int>(el + Poe2Offsets.AtlasNode.MapNodeId) ?? 0);
            uint content = (uint)(_reader.ReadStructure<int>(el + Poe2Offsets.AtlasNode.Content) ?? 0);
            byte state = _reader.ReadStructure<byte>(el + Poe2Offsets.AtlasNode.State) ?? 0;
            byte biome = _reader.ReadStructure<byte>(el + Poe2Offsets.AtlasNode.Biome) ?? 0;
            byte flags = _reader.ReadStructure<byte>(el + Poe2Offsets.AtlasNode.Flags) ?? 0;
            int gridX = _reader.ReadStructure<int>(el + Poe2Offsets.AtlasNode.GridPos) ?? 0;
            int gridY = _reader.ReadStructure<int>(el + Poe2Offsets.AtlasNode.GridPos + 4) ?? 0;
            uint uiFlags = (uint)(_reader.ReadStructure<int>(el + Poe2Offsets.UiElement.Flags) ?? 0);
            bool visible = ((uiFlags >> Poe2Offsets.UiElement.FlagVisibleBit) & 1) != 0;
            float relX = _reader.ReadStructure<float>(el + Poe2Offsets.UiElement.RelativePosition) ?? 0f;
            float relY = _reader.ReadStructure<float>(el + Poe2Offsets.UiElement.RelativePosition + 4) ?? 0f;
            float w = _reader.ReadStructure<float>(el + Poe2Offsets.UiElement.SizeWidth) ?? 0f;
            float h = _reader.ReadStructure<float>(el + Poe2Offsets.UiElement.SizeHeight) ?? 0f;
            float scale = _reader.ReadStructure<float>(el + Poe2Offsets.UiElement.LocalScaleMul) ?? 0f;

            raw.Add(new RawNode(el, id, content, state, biome, flags, gridX, gridY, visible, relX, relY, w, h, scale));
        }

        if (raw.Count < 8)
        {
            Invalidate();
            return false;
        }

        float[] scales = [.. raw.Select(r => r.Scale).Where(s => s > 0.01f).OrderBy(s => s)];
        float zoom = scales.Length > 0 ? scales[scales.Length / 2] : 0.85f;
        float factor = (winH / 1600f) * zoom;

        foreach (RawNode r in raw)
        {
            (string code, string name, IReadOnlyList<string> tags) = ResolveTags(r.El);
            outNodes.Add(new AtlasNodeData(
                r.El, r.Id, r.Content, r.State, r.Biome, r.Flags, r.GridX, r.GridY, code, name, tags, r.Visible,
                r.RelX * factor, r.RelY * factor, r.W * factor, r.H * factor));
        }

        EnsureConnections();
        return true;
    }

    private readonly record struct RawNode(
        long El, uint Id, uint Content, byte State, byte Biome, byte Flags, int GridX, int GridY,
        bool Visible, float RelX, float RelY, float W, float H, float Scale);

    private (string Code, string Name, IReadOnlyList<string> Tags) ResolveTags(long el)
    {
        string code = string.Empty;
        string name = string.Empty;
        long mapRow = Ptr(el + 0x300);
        if (mapRow != 0)
        {
            long w = Ptr(mapRow);
            string direct = w != 0 ? _reader.ReadStringUtf16(w, 64) : string.Empty;
            if (direct.StartsWith("Map", StringComparison.Ordinal))
            {
                code = direct;
            }
            else if (w != 0)
            {
                long idPtr = Ptr(w);
                code = idPtr != 0 ? _reader.ReadStringUtf16(idPtr, 64) : string.Empty;
                long namePtr = Ptr(w + Poe2Offsets.AtlasNode.MapRowName);
                name = namePtr != 0 ? _reader.ReadStringUtf16(namePtr, 64) : string.Empty;
            }
        }

        List<string> tags = [];
        long row = Ptr(el + 0x310);
        if (row != 0)
        {
            long contentRow = Ptr(row + 0x38);
            if (contentRow != 0)
            {
                long np = Ptr(contentRow + 0x30);
                string nm = np != 0 ? _reader.ReadStringUtf16(np, 64) : string.Empty;
                if (LooksLikeName(nm))
                {
                    tags.Add(nm.Trim());
                }
            }
        }

        return (code, name, tags);
    }

    private bool AtlasPanelOpen(long uiRoot)
    {
        long first = Ptr(uiRoot + Poe2Offsets.UiElement.Children);
        if (first == 0)
        {
            return false;
        }

        long panel = Ptr(first + (Poe2Offsets.AtlasPanel.UiRootChildIndex * 8));
        if (panel == 0 || Ptr(panel + Poe2Offsets.UiElement.Self) != panel)
        {
            return false;
        }

        uint flags = (uint)(_reader.ReadStructure<int>(panel + Poe2Offsets.UiElement.Flags) ?? 0);
        return ((flags >> Poe2Offsets.UiElement.FlagVisibleBit) & 1) != 0;
    }

    private bool DetectNodeClass(long uiRoot)
    {
        long root = Ptr(uiRoot + Poe2Offsets.UiElement.Parent);
        if (root == 0)
        {
            root = uiRoot;
        }

        Queue<long> queue = new();
        HashSet<long> visited = [];
        Dictionary<long, List<long>> byVtable = [];
        queue.Enqueue(root);

        while (queue.Count > 0 && visited.Count < 200000)
        {
            long el = queue.Dequeue();
            if (el == 0 || !visited.Add(el) || Ptr(el + Poe2Offsets.UiElement.Self) != el)
            {
                continue;
            }

            long vt = Ptr(el);
            if (vt != 0)
            {
                if (!byVtable.TryGetValue(vt, out List<long>? list))
                {
                    list = [];
                    byVtable[vt] = list;
                }

                list.Add(el);
            }

            long first = Ptr(el + Poe2Offsets.UiElement.Children);
            long lastEnd = Ptr(el + Poe2Offsets.UiElement.ChildrenEnd);
            if (first != 0 && lastEnd >= first)
            {
                long n = (lastEnd - first) / 8;
                if (n is > 0 and <= 16384)
                {
                    for (long k = 0; k < n; k++)
                    {
                        queue.Enqueue(Ptr(first + (k * 8)));
                    }
                }
            }
        }

        long bestVt = 0;
        int bestCount = 0;
        int bestBiomes = 0;
        long fallbackVt = 0;
        int fallbackBiomes = 0;
        foreach ((long vt, List<long> list) in byVtable)
        {
            if (list.Count < 50)
            {
                continue;
            }

            HashSet<int> biomes = [];
            Dictionary<int, int> widths = [];
            foreach (long el in list.Take(400))
            {
                byte? b = _reader.ReadStructure<byte>(el + Poe2Offsets.AtlasNode.Biome);
                if (b is >= 1 and <= 12)
                {
                    biomes.Add(b.Value);
                }

                float? width = _reader.ReadStructure<float>(el + Poe2Offsets.UiElement.SizeWidth);
                if (width is not null)
                {
                    int iw = (int)width.Value;
                    widths[iw] = widths.GetValueOrDefault(iw) + 1;
                }
            }

            if (biomes.Count > fallbackBiomes)
            {
                fallbackBiomes = biomes.Count;
                fallbackVt = vt;
            }

            int modalW = widths.Count == 0 ? 0 : widths.OrderByDescending(k => k.Value).First().Key;
            if (modalW is >= 28 and <= 56 && biomes.Count >= 3 && list.Count > bestCount)
            {
                bestCount = list.Count;
                bestVt = vt;
                bestBiomes = biomes.Count;
            }
        }

        if (bestVt == 0)
        {
            bestVt = fallbackVt;
            bestBiomes = fallbackBiomes;
        }

        if (bestVt == 0 || bestBiomes < 3)
        {
            return false;
        }

        _nodeVtable = bestVt;

        Dictionary<long, int> parentCount = [];
        foreach (long el in byVtable[bestVt])
        {
            long p = Ptr(el + Poe2Offsets.UiElement.Parent);
            if (p != 0)
            {
                parentCount[p] = parentCount.GetValueOrDefault(p) + 1;
            }
        }

        if (parentCount.Count == 0)
        {
            return false;
        }

        _nodeCanvas = parentCount.OrderByDescending(k => k.Value).First().Key;

        _currentMarker = 0;
        HashSet<long> nodeSet = [.. byVtable[bestVt]];
        foreach (long el in byVtable.Values.SelectMany(v => v))
        {
            if (nodeSet.Contains(el))
            {
                continue;
            }

            long p = Ptr(el + 0x300);
            if (p != 0 && nodeSet.Contains(p))
            {
                _currentMarker = el;
                break;
            }
        }

        return _nodeCanvas != 0;
    }

    /// <summary>
    /// Read the node canvas's connection-edge vector (<see cref="Poe2Offsets.AtlasGraph.ConnectionsVec"/>)
    /// once per canvas and build the bidirectional adjacency by grid coord. Each 20-byte edge is
    /// { int unknown; StdTuple2D&lt;int&gt; source@+0x04; StdTuple2D&lt;int&gt; target@+0x0C }. Cached;
    /// rebuilt only when the canvas changes. Ported from POE2Radar Poe2Atlas.EnsureGraph.
    /// </summary>
    private void EnsureConnections()
    {
        if (_nodeCanvas == 0 || _connectionsCanvas == _nodeCanvas)
        {
            return;
        }

        _connections.Clear();
        _connectionsCanvas = _nodeCanvas;

        long begin = Ptr(_nodeCanvas + Poe2Offsets.AtlasGraph.ConnectionsVec);
        long end = Ptr(_nodeCanvas + Poe2Offsets.AtlasGraph.ConnectionsVec + 8);
        if (begin == 0 || end < begin)
        {
            return;
        }

        long bytes = end - begin;
        if (bytes <= 0 || bytes % Poe2Offsets.AtlasGraph.EdgeStride != 0)
        {
            return;
        }

        int count = (int)(bytes / Poe2Offsets.AtlasGraph.EdgeStride);
        if (count is <= 0 or > 200000)
        {
            return;
        }

        byte[]? buf = _reader.ReadMemory(begin, count * Poe2Offsets.AtlasGraph.EdgeStride);
        if (buf is null || buf.Length < count * Poe2Offsets.AtlasGraph.EdgeStride)
        {
            return;
        }

        for (int i = 0; i < count; i++)
        {
            int o = i * Poe2Offsets.AtlasGraph.EdgeStride;
            int sx = BitConverter.ToInt32(buf, o + Poe2Offsets.AtlasGraph.EdgeSourceOff);
            int sy = BitConverter.ToInt32(buf, o + Poe2Offsets.AtlasGraph.EdgeSourceOff + 4);
            int dx = BitConverter.ToInt32(buf, o + Poe2Offsets.AtlasGraph.EdgeTargetOff);
            int dy = BitConverter.ToInt32(buf, o + Poe2Offsets.AtlasGraph.EdgeTargetOff + 4);
            if (sx == dx && sy == dy)
            {
                continue;
            }

            AddEdge((sx, sy), (dx, dy));
            AddEdge((dx, dy), (sx, sy));
        }
    }

    private void AddEdge((int X, int Y) a, (int X, int Y) b)
    {
        if (!_connections.TryGetValue(a, out List<(int X, int Y)>? list))
        {
            list = [];
            _connections[a] = list;
        }

        if (!list.Contains(b))
        {
            list.Add(b);
        }
    }

    private void Invalidate()
    {
        _nodeCanvas = 0;
        _nodeVtable = 0;
        _currentMarker = 0;
        _markerFails = 0;
        _connectionsCanvas = 0;
        _connections.Clear();
    }

    private static bool LooksLikeName(string s) => s.Length is >= 3 and <= 64 && s[0] is >= ' ' and < (char)0x7f;

    private long Ptr(long address)
    {
        long pointer = _reader.ReadPointer(address);
        ulong value = (ulong)pointer;
        return value is >= 0x10000 and <= 0x7FFFFFFFFFFF ? pointer : 0;
    }
}
