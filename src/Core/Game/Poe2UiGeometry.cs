using POE2_AutoMate.Core.Memory;

namespace POE2_AutoMate.Core.Game;

/// <summary>
/// One UI element with its absolute on-screen rectangle (pixels, window-local), computed from the
/// in-memory UiElement tree using the GameHelper2 UiElementBaseFuncs math (parent-chain unscaled
/// position x resolution scale). Ported from POE2Radar's Poe2Runeforge geometry.
/// </summary>
public sealed record UiScreenElement(
    long Address,
    long Parent,
    bool Visible,
    string Text,
    int ChildCount,
    float ScreenX,
    float ScreenY,
    float ScreenWidth,
    float ScreenHeight)
{
    public float CenterX => ScreenX + (ScreenWidth / 2f);
    public float CenterY => ScreenY + (ScreenHeight / 2f);
    public float Area => ScreenWidth * ScreenHeight;
    public float AspectRatio => ScreenHeight <= 0f ? 0f : ScreenWidth / ScreenHeight;
}

/// <summary>A detected inventory/stash grid in window-local screen pixels.</summary>
public sealed record GridGeometry(
    float OriginX,
    float OriginY,
    float CellWidth,
    float CellHeight,
    int Columns,
    int Rows,
    string Source)
{
    public float Width => CellWidth * Columns;
    public float Height => CellHeight * Rows;

    /// <summary>Pixel center of the cell at (col,row), 0-based.</summary>
    public (float X, float Y) CellCenter(int col, int row) =>
        (OriginX + ((col + 0.5f) * CellWidth), OriginY + ((row + 0.5f) * CellHeight));

    /// <summary>Window-relative (0..1) fractions for the legacy fraction-based calibration.</summary>
    public (double Left, double Top, double Width, double Height) AsFractions(float winW, float winH) =>
        (OriginX / winW, OriginY / winH, Width / winW, Height / winH);
}

/// <summary>
/// Projects UiElements to absolute screen pixels and detects grid containers (inventory / stash).
/// All geometry is window-local (0,0 = window top-left) so it pairs with GetWindowRect offsets.
/// </summary>
public sealed class Poe2UiGeometry(ProcessReader reader)
{
    private readonly ProcessReader _reader = reader;

    /// <summary>BFS the UI tree from <paramref name="root"/>, returning every node with its screen rect.</summary>
    public List<UiScreenElement> WalkScreenElements(long root, float winW, float winH, int maxNodes = 8000)
    {
        List<UiScreenElement> result = [];
        if (root == 0 || winW <= 0 || winH <= 0)
        {
            return result;
        }

        Queue<long> queue = new();
        HashSet<long> visited = [];
        queue.Enqueue(root);

        while (queue.Count > 0 && result.Count < maxNodes)
        {
            long node = queue.Dequeue();
            if (node == 0 || !visited.Add(node))
            {
                continue;
            }

            uint flags = (uint)(_reader.ReadStructure<int>(node + Poe2Offsets.UiElement.Flags) ?? 0);
            bool visible = (flags & (1u << Poe2Offsets.UiElement.FlagVisibleBit)) != 0;
            long parent = Ptr(node + Poe2Offsets.UiElement.Parent);

            int childCount = 0;
            if (TryChildren(node, out long first, out long count))
            {
                childCount = (int)count;
                byte[]? childBytes = _reader.ReadMemory(first, (int)(count * nint.Size));
                if (childBytes is not null)
                {
                    for (int i = 0; i + nint.Size <= childBytes.Length; i += nint.Size)
                    {
                        long child = BitConverter.ToInt64(childBytes, i);
                        if (IsPlausible(child) && !visited.Contains(child))
                        {
                            queue.Enqueue(child);
                        }
                    }
                }
            }

            if (TryScreenRect(node, winW, winH, out float x, out float y, out float w, out float h))
            {
                string text = ReadStdWString(node + Poe2Offsets.UiElement.Text);
                result.Add(new UiScreenElement(node, parent, visible, text, childCount, x, y, w, h));
            }
        }

        return result;
    }

    /// <summary>
    /// Detect the player backpack grid (12x5 by default) as an on-screen rectangle. Heuristic, resolution
    /// independent: among visible elements in the lower-right region, pick the one whose aspect ratio is
    /// closest to Columns/Rows and whose size is a large, sane fraction of the window. <paramref name="candidates"/>
    /// is filled with the ranked container candidates for diagnostics.
    /// </summary>
    public GridGeometry? DetectInventoryGrid(
        long uiRoot,
        float winW,
        float winH,
        int columns,
        int rows,
        out List<UiScreenElement> candidates)
    {
        candidates = [];
        if (uiRoot == 0 || winW <= 0 || winH <= 0 || columns <= 0 || rows <= 0)
        {
            return null;
        }

        List<UiScreenElement> elements = WalkScreenElements(uiRoot, winW, winH);
        float targetRatio = (float)columns / rows;

        // A backpack container sits in the lower-right, is visible, spans a meaningful slice of the screen
        // but never the whole window, and matches the grid aspect ratio.
        candidates = elements
            .Where(e => e.Visible)
            .Where(e => e.ScreenWidth >= winW * 0.18f && e.ScreenWidth <= winW * 0.6f)
            .Where(e => e.ScreenHeight >= winH * 0.12f && e.ScreenHeight <= winH * 0.55f)
            .Where(e => e.CenterX >= winW * 0.5f && e.CenterY >= winH * 0.45f)
            .Where(e => Math.Abs(e.AspectRatio - targetRatio) <= 0.6f)
            .OrderBy(e => Math.Abs(e.AspectRatio - targetRatio))
            .Take(12)
            .ToList();

        UiScreenElement? best = candidates.FirstOrDefault();
        if (best is null)
        {
            return null;
        }

        return new GridGeometry(
            best.ScreenX,
            best.ScreenY,
            best.ScreenWidth / columns,
            best.ScreenHeight / rows,
            columns,
            rows,
            $"ui-container@0x{best.Address:X}");
    }

    /// <summary>
    /// BFS the UI tree for the first VISIBLE text element whose text contains one of <paramref name="texts"/>
    /// (case-insensitive) and whose projected rect is on-screen, returning its window-local pixel center.
    /// Projects only the matched element (cheap). Used to click memory-decoded buttons (e.g. Traverse)
    /// whose absolute screen position the generic text snapshot does not provide.
    /// </summary>
    public (float CenterX, float CenterY)? FindVisibleTextCenter(long root, float winW, float winH, string[] texts, int maxNodes = 40000)
    {
        if (root == 0 || winW <= 0 || winH <= 0 || texts.Length == 0)
        {
            return null;
        }

        Queue<long> queue = new();
        HashSet<long> visited = [];
        queue.Enqueue(root);

        while (queue.Count > 0 && visited.Count < maxNodes)
        {
            long node = queue.Dequeue();
            if (node == 0 || !visited.Add(node))
            {
                continue;
            }

            uint flags = (uint)(_reader.ReadStructure<int>(node + Poe2Offsets.UiElement.Flags) ?? 0);
            bool visible = (flags & (1u << Poe2Offsets.UiElement.FlagVisibleBit)) != 0;
            if (visible)
            {
                string text = ReadStdWString(node + Poe2Offsets.UiElement.Text);
                if (!string.IsNullOrWhiteSpace(text) &&
                    texts.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase)) &&
                    TryScreenRect(node, winW, winH, out float x, out float y, out float w, out float h))
                {
                    float cx = x + (w / 2f);
                    float cy = y + (h / 2f);
                    if (cx >= 0 && cx <= winW && cy >= 0 && cy <= winH)
                    {
                        return (cx, cy);
                    }
                }
            }

            if (TryChildren(node, out long first, out long count))
            {
                byte[]? childBytes = _reader.ReadMemory(first, (int)(count * nint.Size));
                if (childBytes is not null)
                {
                    for (int i = 0; i + nint.Size <= childBytes.Length; i += nint.Size)
                    {
                        long child = BitConverter.ToInt64(childBytes, i);
                        if (IsPlausible(child) && !visited.Contains(child))
                        {
                            queue.Enqueue(child);
                        }
                    }
                }
            }
        }

        return null;
    }

    public (float CenterX, float CenterY)? FindNearestVisibleTextCenter(
        long root,
        float winW,
        float winH,
        float targetX,
        float targetY,
        float radius,
        int maxNodes = 40000)
    {
        if (root == 0 || winW <= 0 || winH <= 0 || radius <= 0)
        {
            return null;
        }

        Queue<long> queue = new();
        HashSet<long> visited = [];
        queue.Enqueue(root);
        UiScreenElement? best = null;
        double bestScore = double.MaxValue;
        double radiusSquared = radius * radius;

        while (queue.Count > 0 && visited.Count < maxNodes)
        {
            long node = queue.Dequeue();
            if (node == 0 || !visited.Add(node))
            {
                continue;
            }

            uint flags = (uint)(_reader.ReadStructure<int>(node + Poe2Offsets.UiElement.Flags) ?? 0);
            bool visible = (flags & (1u << Poe2Offsets.UiElement.FlagVisibleBit)) != 0;
            if (visible)
            {
                string text = ReadStdWString(node + Poe2Offsets.UiElement.Text);
                if (LooksLikeClickableLabel(text) &&
                    TryScreenRect(node, winW, winH, out float x, out float y, out float w, out float h))
                {
                    float cx = x + (w / 2f);
                    float cy = y + (h / 2f);
                    double dx = cx - targetX;
                    double dy = cy - targetY;
                    double distanceSquared = (dx * dx) + (dy * dy);
                    if (distanceSquared <= radiusSquared && cx >= 0 && cx <= winW && cy >= 0 && cy <= winH)
                    {
                        double sizePenalty = Math.Max(0, w - 420) + Math.Max(0, h - 80);
                        double score = distanceSquared + (sizePenalty * sizePenalty);
                        if (score < bestScore)
                        {
                            best = new UiScreenElement(node, Ptr(node + Poe2Offsets.UiElement.Parent), true, text, 0, x, y, w, h);
                            bestScore = score;
                        }
                    }
                }
            }

            if (TryChildren(node, out long first, out long count))
            {
                byte[]? childBytes = _reader.ReadMemory(first, (int)(count * nint.Size));
                if (childBytes is not null)
                {
                    for (int i = 0; i + nint.Size <= childBytes.Length; i += nint.Size)
                    {
                        long child = BitConverter.ToInt64(childBytes, i);
                        if (IsPlausible(child) && !visited.Contains(child))
                        {
                            queue.Enqueue(child);
                        }
                    }
                }
            }
        }

        return best is null ? null : (best.CenterX, best.CenterY);
    }

    private static bool LooksLikeClickableLabel(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim();
        return trimmed.Length is >= 2 and <= 80 &&
               !trimmed.Contains('\n') &&
               !trimmed.Contains("Life", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.Contains("Mana", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.Contains("Quest", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Absolute window-local screen rect of one element. Mirrors POE2Radar's TryScreenRect.</summary>
    public bool TryScreenRect(long element, float winW, float winH, out float x, out float y, out float w, out float h)
    {
        x = y = w = h = 0f;
        if (element == 0)
        {
            return false;
        }

        byte idx = (byte)(_reader.ReadStructure<byte>(element + Poe2Offsets.UiElement.ScaleIndex) ?? 0);
        float mul = _reader.ReadStructure<float>(element + Poe2Offsets.UiElement.LocalScaleMul) ?? 1f;
        float uw = _reader.ReadStructure<float>(element + Poe2Offsets.UiElement.SizeWidth) ?? 0f;
        float uh = _reader.ReadStructure<float>(element + Poe2Offsets.UiElement.SizeHeight) ?? 0f;
        (float sw, float sh) = ScaleValue(idx, mul, winW, winH);
        if (sw <= 0f || sh <= 0f)
        {
            return false;
        }

        (float px, float py) = UnscaledPos(element, 0, winW, winH);
        if (!float.IsFinite(px) || !float.IsFinite(py))
        {
            return false;
        }

        x = px * sw;
        y = py * sh;
        w = uw * sw;
        h = uh * sh;
        return w > 1f && h > 1f;
    }

    private static (float W, float H) ScaleValue(byte idx, float mul, float winW, float winH)
    {
        if (mul == 0f)
        {
            mul = 1f;
        }

        float v1 = winW / (float)Poe2Offsets.UiElement.BaseResW;
        float v2 = winH / (float)Poe2Offsets.UiElement.BaseResH;
        float w = mul;
        float h = mul;
        switch (idx)
        {
            case 1: w *= v1; h *= v1; break;
            case 2: w *= v2; h *= v2; break;
            case 3: w *= v1; h *= v2; break;
        }

        return (w, h);
    }

    private (float X, float Y) UnscaledPos(long element, int depth, float winW, float winH)
    {
        float lx = _reader.ReadStructure<float>(element + Poe2Offsets.UiElement.RelativePosition) ?? 0f;
        float ly = _reader.ReadStructure<float>(element + Poe2Offsets.UiElement.RelativePosition + 4) ?? 0f;
        long parent = Ptr(element + Poe2Offsets.UiElement.Parent);
        if (parent == 0 || depth >= 64)
        {
            return (lx, ly);
        }

        (float parentX, float parentY) = UnscaledPos(parent, depth + 1, winW, winH);

        uint flags = (uint)(_reader.ReadStructure<int>(element + Poe2Offsets.UiElement.Flags) ?? 0);
        if ((flags & (1u << Poe2Offsets.UiElement.FlagModifyPosBit)) != 0)
        {
            float mx = _reader.ReadStructure<float>(element + Poe2Offsets.UiElement.PositionModifier) ?? 0f;
            float my = _reader.ReadStructure<float>(element + Poe2Offsets.UiElement.PositionModifier + 4) ?? 0f;
            parentX += mx;
            parentY += my;
        }

        byte elIdx = (byte)(_reader.ReadStructure<byte>(element + Poe2Offsets.UiElement.ScaleIndex) ?? 0);
        float elMul = _reader.ReadStructure<float>(element + Poe2Offsets.UiElement.LocalScaleMul) ?? 1f;
        byte pIdx = (byte)(_reader.ReadStructure<byte>(parent + Poe2Offsets.UiElement.ScaleIndex) ?? 0);
        float pMul = _reader.ReadStructure<float>(parent + Poe2Offsets.UiElement.LocalScaleMul) ?? 1f;
        if (pIdx == elIdx && pMul == elMul)
        {
            return (parentX + lx, parentY + ly);
        }

        (float psw, float psh) = ScaleValue(pIdx, pMul, winW, winH);
        (float msw, float msh) = ScaleValue(elIdx, elMul, winW, winH);
        if (msw == 0f || msh == 0f)
        {
            return (parentX + lx, parentY + ly);
        }

        return ((parentX * psw / msw) + lx, (parentY * psh / msh) + ly);
    }

    private bool TryChildren(long element, out long first, out long count)
    {
        first = Ptr(element + Poe2Offsets.UiElement.Children);
        count = 0;
        if (first == 0)
        {
            return false;
        }

        long last = Ptr(element + Poe2Offsets.UiElement.ChildrenEnd);
        if (last < first)
        {
            return false;
        }

        count = (last - first) / nint.Size;
        return count is > 0 and <= 4000;
    }

    private string ReadStdWString(long address)
    {
        int? length = _reader.ReadStructure<int>(address + 0x10);
        if (length is null or <= 0 or > 1024)
        {
            return string.Empty;
        }

        if (length < 8)
        {
            return _reader.ReadStringUtf16(address, length.Value);
        }

        long stringPtr = Ptr(address);
        return stringPtr == 0 ? string.Empty : _reader.ReadStringUtf16(stringPtr, length.Value);
    }

    private long Ptr(long address)
    {
        long pointer = _reader.ReadPointer(address);
        return IsPlausible(pointer) ? pointer : 0;
    }

    private static bool IsPlausible(long value)
    {
        ulong pointer = (ulong)value;
        return pointer is >= 0x10000 and <= 0x7FFFFFFFFFFF;
    }
}
