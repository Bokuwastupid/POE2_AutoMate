namespace POE2_AutoMate.Core.Game;

public sealed record InventorySnapshot(
    long InventoryAddress,
    int Width,
    int Height,
    int RequestCounter,
    IReadOnlyList<InventoryItemData> Items)
{
    public int CellCount => Math.Max(0, Width * Height);
    public int OccupiedCellCount => Items.SelectMany(item => item.Cells()).Distinct().Count();
    public int FreeCellCount => Math.Max(0, CellCount - OccupiedCellCount);

    public bool IsProtected(InventoryProtectedMask mask, InventoryItemData item)
    {
        return item.Cells().Any(mask.IsProtected);
    }

    public static InventorySnapshot Empty { get; } = new(0, 12, 5, 0, []);
}

public sealed record InventoryItemData(
    long ItemAddress,
    string Metadata,
    int Rarity,
    int StackSize,
    int StartX,
    int StartY,
    int EndX,
    int EndY)
{
    public int Width => Math.Max(1, EndX - StartX + 1);
    public int Height => Math.Max(1, EndY - StartY + 1);

    public IEnumerable<int> Cells()
    {
        for (int y = StartY; y <= EndY; y++)
        {
            for (int x = StartX; x <= EndX; x++)
            {
                if (x >= 0 && y >= 0)
                {
                    yield return (y * 12) + x;
                }
            }
        }
    }
}

public sealed class InventoryProtectedMask
{
    private readonly HashSet<int> _protectedCells;

    public InventoryProtectedMask(IEnumerable<int>? protectedCells = null)
    {
        _protectedCells = protectedCells is null ? [] : [.. protectedCells.Where(cell => cell is >= 0 and < 60)];
    }

    public IReadOnlyCollection<int> Cells => _protectedCells;
    public int Count => _protectedCells.Count;

    public bool IsProtected(int cell) => _protectedCells.Contains(cell);

    public bool Toggle(int cell)
    {
        if (cell is < 0 or >= 60)
        {
            return false;
        }

        if (!_protectedCells.Add(cell))
        {
            _protectedCells.Remove(cell);
            return false;
        }

        return true;
    }
}

public sealed record UiSnapshot(IReadOnlyList<UiElementData> Elements)
{
    public IReadOnlyList<UiElementData> VisibleTextElements =>
        Elements.Where(element => element.IsVisible && !string.IsNullOrWhiteSpace(element.Text)).ToArray();
}

public sealed record UiElementData(
    long Address,
    long ParentAddress,
    bool IsVisible,
    string Text,
    float X,
    float Y,
    float Width,
    float Height);
