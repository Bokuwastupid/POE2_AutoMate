using POE2_AutoMate.Core.Game;

namespace POE2_AutoMate.Automation;

public sealed class RoutePlanner
{
    private static readonly (int X, int Y)[] Directions =
    [
        (1, 0), (-1, 0), (0, 1), (0, -1),
        (1, 1), (1, -1), (-1, 1), (-1, -1)
    ];

    public IReadOnlyList<RoutePoint> BuildRoute(GameSnapshot snapshot, EntityData target, int searchRadiusCells)
    {
        return BuildRouteToPoint(snapshot, target.X, target.Y, searchRadiusCells);
    }

    public IReadOnlyList<RoutePoint> BuildRouteToPoint(GameSnapshot snapshot, float worldX, float worldY, int searchRadiusCells)
    {
        if (snapshot.Player is not { HasPosition: true } player)
        {
            return [];
        }

        if (snapshot.Terrain is null)
        {
            return [WorldPoint(worldX, worldY)];
        }

        int startX = ToGrid(player.X);
        int startY = ToGrid(player.Y);
        int targetX = ToGrid(worldX);
        int targetY = ToGrid(worldY);

        if (!TryFindWalkable(snapshot.Terrain, startX, startY, 12, out GridPoint start) ||
            !TryFindWalkable(snapshot.Terrain, targetX, targetY, 18, out GridPoint end))
        {
            return [];
        }

        int radius = Math.Max(32, searchRadiusCells);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            IReadOnlyList<RoutePoint> route = TryBuildRoute(snapshot.Terrain, start, end, radius, allowPartial: attempt == 2);
            if (route.Count > 0)
            {
                return route;
            }

            radius *= 2;
        }

        return [];
    }

    private static IReadOnlyList<RoutePoint> TryBuildRoute(
        TerrainData terrain,
        GridPoint start,
        GridPoint end,
        int searchRadiusCells,
        bool allowPartial)
    {
        int minX = Math.Max(0, Math.Min(start.X, end.X) - searchRadiusCells);
        int maxX = Math.Min(terrain.Width - 1, Math.Max(start.X, end.X) + searchRadiusCells);
        int minY = Math.Max(0, Math.Min(start.Y, end.Y) - searchRadiusCells);
        int maxY = Math.Min(terrain.Height - 1, Math.Max(start.Y, end.Y) + searchRadiusCells);

        PriorityQueue<GridPoint, double> open = new();
        Dictionary<long, GridPoint> cameFrom = [];
        Dictionary<long, double> costSoFar = [];
        long startKey = Key(start.X, start.Y);
        long endKey = Key(end.X, end.Y);
        GridPoint best = start;
        double bestHeuristic = Heuristic(start, end);

        open.Enqueue(start, 0);
        costSoFar[startKey] = 0;

        int expanded = 0;
        int maxExpanded = allowPartial ? 90_000 : 45_000;
        while (open.Count > 0 && expanded < maxExpanded)
        {
            GridPoint current = open.Dequeue();
            expanded++;

            double currentHeuristic = Heuristic(current, end);
            if (currentHeuristic < bestHeuristic)
            {
                best = current;
                bestHeuristic = currentHeuristic;
            }

            if (Key(current.X, current.Y) == endKey)
            {
                return ToRoute(cameFrom, current);
            }

            foreach ((int dx, int dy) in Directions)
            {
                int nextX = current.X + dx;
                int nextY = current.Y + dy;
                if (nextX < minX || nextX > maxX || nextY < minY || nextY > maxY)
                {
                    continue;
                }

                if (!IsWalkable(terrain, nextX, nextY))
                {
                    continue;
                }

                if (dx != 0 &&
                    dy != 0 &&
                    (!IsWalkable(terrain, current.X + dx, current.Y) ||
                     !IsWalkable(terrain, current.X, current.Y + dy)))
                {
                    continue;
                }

                double moveCost = dx == 0 || dy == 0 ? 1.0 : 1.414;
                double newCost = costSoFar[Key(current.X, current.Y)] + moveCost;
                long nextKey = Key(nextX, nextY);
                if (costSoFar.TryGetValue(nextKey, out double oldCost) && newCost >= oldCost)
                {
                    continue;
                }

                GridPoint next = new(nextX, nextY);
                costSoFar[nextKey] = newCost;
                cameFrom[nextKey] = current;
                double priority = newCost + Heuristic(next, end);
                open.Enqueue(next, priority);
            }
        }

        if (!allowPartial || Key(best.X, best.Y) == startKey)
        {
            return [];
        }

        return ToRoute(cameFrom, best);
    }

    private static IEnumerable<GridPoint> Reconstruct(Dictionary<long, GridPoint> cameFrom, GridPoint end)
    {
        List<GridPoint> path = [end];
        GridPoint current = end;
        while (cameFrom.TryGetValue(Key(current.X, current.Y), out GridPoint previous))
        {
            current = previous;
            path.Add(current);
        }

        path.Reverse();
        return path;
    }

    private static bool TryFindWalkable(TerrainData terrain, int originX, int originY, int radius, out GridPoint point)
    {
        for (int r = 0; r <= radius; r++)
        {
            for (int y = originY - r; y <= originY + r; y++)
            {
                for (int x = originX - r; x <= originX + r; x++)
                {
                    if (IsWalkable(terrain, x, y))
                    {
                        point = new GridPoint(x, y);
                        return true;
                    }
                }
            }
        }

        point = default;
        return false;
    }

    private static bool IsWalkable(TerrainData terrain, int x, int y)
    {
        return x >= 0 &&
               y >= 0 &&
               x < terrain.Width &&
               y < terrain.Height &&
               terrain.Walkable[(y * terrain.Width) + x] != 0;
    }

    private static int ToGrid(float world) => (int)Math.Round(world / Poe2Offsets.WorldToGridRatio);
    private static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;
    private static double Heuristic(GridPoint a, GridPoint b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
    private static RoutePoint WorldPoint(double x, double y) => new((float)x, (float)y);
    private static IReadOnlyList<RoutePoint> ToRoute(Dictionary<long, GridPoint> cameFrom, GridPoint end) =>
        Reconstruct(cameFrom, end)
            .Where((_, index) => index % 4 == 0)
            .Take(120)
            .Select(point => WorldPoint(point.X * Poe2Offsets.WorldToGridRatio, point.Y * Poe2Offsets.WorldToGridRatio))
            .ToArray();

    private readonly record struct GridPoint(int X, int Y);
}

public readonly record struct RoutePoint(float X, float Y);
