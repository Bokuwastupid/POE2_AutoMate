using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using POE2_AutoMate.Automation;
using POE2_AutoMate.Core.Game;

namespace POE2_AutoMate;

public partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;

    public bool RotateMap180 { get; set; } = true;
    public bool FullMapView { get; set; }
    public double WorldUnitsPerPixel { get; set; } = 12.0;
    public bool ShowTerrain { get; set; } = true;
    public bool ShowMonsters { get; set; } = true;
    public bool ShowChests { get; set; } = true;
    public bool ShowTransitions { get; set; } = true;
    public bool ShowNpcs { get; set; } = true;
    public bool ShowOther { get; set; }
    public bool ShowBotDebug { get; set; } = true;
    private CombatReport? _combatReport;

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    public void RenderSnapshot(GameSnapshot? snapshot)
    {
        OverlayCanvas.Children.Clear();

        double width = ActualWidth > 1 ? ActualWidth : Width;
        double height = ActualHeight > 1 ? ActualHeight : Height;
        DrawGrid(width, height);

        if (snapshot?.Player is not { HasPosition: true } player)
        {
            OverlayStatusText.Text = "waiting for player";
            OverlayCountText.Text = "0";
            DrawPlayer(width / 2, height / 2);
            return;
        }

        double worldUnitsPerPixel = Math.Clamp(WorldUnitsPerPixel, 4.0, 40.0);
        double centerX = width / 2.0;
        double centerY = height / 2.0;
        FullMapProjection? fullMap = FullMapView ? BuildFullMapProjection(snapshot.Terrain, width, height) : null;
        if (ShowTerrain)
        {
            if (fullMap is not null)
            {
                DrawFullTerrain(snapshot.Terrain, fullMap, width, height);
            }
            else
            {
                DrawTerrain(snapshot.Terrain, player, centerX, centerY, width, height, worldUnitsPerPixel);
            }
        }

        int visible = 0;
        foreach (EntityData entity in snapshot.Entities)
        {
            if (!ShouldDrawEntity(entity.Category))
            {
                continue;
            }

            (double x, double y) = ProjectWorld(entity.X, entity.Y, entity.Z, player, centerX, centerY, worldUnitsPerPixel, fullMap);

            if (x < -8 || y < -8 || x > width + 8 || y > height + 8)
            {
                continue;
            }

            visible++;
            DrawEntity(x, y, entity.Category);
        }

        (double playerX, double playerY) = ProjectWorld(player.X, player.Y, player.Z, player, centerX, centerY, worldUnitsPerPixel, fullMap);
        DrawPlayer(playerX, playerY);
        DrawCombatDebug(snapshot, player, centerX, centerY, worldUnitsPerPixel, fullMap);
        OverlayStatusText.Text = FullMapView ? $"full map 0x{snapshot.AreaInstanceAddress:X}" : $"area 0x{snapshot.AreaInstanceAddress:X}";
        OverlayCountText.Text = $"{visible}/{snapshot.Entities.Count}";
    }

    public void SetCombatDebug(CombatReport? report)
    {
        _combatReport = report;
        BotDebugText.Text = report is null
            ? "bot debug: idle"
            : $"{report.State}: {report.Advice} / {report.LastAction}";
    }

    private bool ShouldDrawEntity(string category)
    {
        return category switch
        {
            "Monster" => ShowMonsters,
            "Chest" => ShowChests,
            "Transition" => ShowTransitions,
            "Npc" => ShowNpcs,
            "Player" => true,
            "Encounter" => ShowOther,
            "Loot" => ShowOther,
            "Object" => ShowOther,
            "Other" => ShowOther,
            _ => ShowOther
        };
    }

    private void DrawTerrain(TerrainData? terrain, PlayerData player, double centerX, double centerY, double width, double height, double worldUnitsPerPixel)
    {
        if (terrain is null)
        {
            return;
        }

        double playerGridX = player.X / Poe2Offsets.WorldToGridRatio;
        double playerGridY = player.Y / Poe2Offsets.WorldToGridRatio;
        double pixelsPerGridCell = Poe2Offsets.WorldToGridRatio / worldUnitsPerPixel;
        const int sampleStep = 5;
        const int sampleRadius = 80;

        Brush walkableBrush = new SolidColorBrush(Color.FromArgb(88, 76, 150, 118));
        Brush walkableBorderBrush = new SolidColorBrush(Color.FromArgb(180, 134, 224, 176));
        for (int gy = -sampleRadius; gy <= sampleRadius; gy += sampleStep)
        {
            int terrainY = (int)Math.Round(playerGridY + gy);
            if (terrainY < 0 || terrainY >= terrain.Height)
            {
                continue;
            }

            for (int gx = -sampleRadius; gx <= sampleRadius; gx += sampleStep)
            {
                int terrainX = (int)Math.Round(playerGridX + gx);
                if (terrainX < 0 || terrainX >= terrain.Width)
                {
                    continue;
                }

                if (terrain.Walkable[(terrainY * terrain.Width) + terrainX] == 0)
                {
                    continue;
                }

                (double x, double y) = ProjectGridOffset(gx, gy, centerX, centerY, pixelsPerGridCell);
                if (x < -4 || y < -4 || x > width + 4 || y > height + 4)
                {
                    continue;
                }

                Rectangle cell = new()
                {
                    Width = Math.Max(2, sampleStep * pixelsPerGridCell),
                    Height = Math.Max(2, sampleStep * pixelsPerGridCell),
                    Fill = walkableBrush,
                    Stroke = walkableBorderBrush,
                    StrokeThickness = 0.8,
                    SnapsToDevicePixels = true
                };
                Canvas.SetLeft(cell, x);
                Canvas.SetTop(cell, y);
                OverlayCanvas.Children.Add(cell);
            }
        }
    }

    private void DrawFullTerrain(TerrainData? terrain, FullMapProjection projection, double width, double height)
    {
        if (terrain is null)
        {
            return;
        }

        int sampleStep = terrain.Width * terrain.Height > 180_000 ? 6 : 4;
        Brush walkableBrush = new SolidColorBrush(Color.FromArgb(82, 76, 150, 118));
        Brush walkableBorderBrush = new SolidColorBrush(Color.FromArgb(150, 134, 224, 176));
        for (int gy = 0; gy < terrain.Height; gy += sampleStep)
        {
            for (int gx = 0; gx < terrain.Width; gx += sampleStep)
            {
                if (terrain.Walkable[(gy * terrain.Width) + gx] == 0)
                {
                    continue;
                }

                (double x, double y) = ProjectFullGrid(gx, gy, projection);
                if (x < -4 || y < -4 || x > width + 4 || y > height + 4)
                {
                    continue;
                }

                double cellSize = Math.Clamp(sampleStep * projection.Scale, 1.4, 8);
                Rectangle cell = new()
                {
                    Width = cellSize,
                    Height = cellSize,
                    Fill = walkableBrush,
                    Stroke = walkableBorderBrush,
                    StrokeThickness = 0.55,
                    SnapsToDevicePixels = true
                };
                Canvas.SetLeft(cell, x);
                Canvas.SetTop(cell, y);
                OverlayCanvas.Children.Add(cell);
            }
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        nint handle = new WindowInteropHelper(this).Handle;
        int exStyle = GetWindowLong(handle, GwlExStyle);
        SetWindowLong(handle, GwlExStyle, exStyle | WsExTransparent | WsExToolWindow);
    }

    private void DrawGrid(double width, double height)
    {
        Brush gridBrush = new SolidColorBrush(Color.FromArgb(70, 55, 58, 64));
        for (double x = width / 2 % 45; x < width; x += 45)
        {
            OverlayCanvas.Children.Add(new Line { X1 = x, Y1 = 0, X2 = x, Y2 = height, Stroke = gridBrush, StrokeThickness = 1 });
        }

        for (double y = height / 2 % 45; y < height; y += 45)
        {
            OverlayCanvas.Children.Add(new Line { X1 = 0, Y1 = y, X2 = width, Y2 = y, Stroke = gridBrush, StrokeThickness = 1 });
        }
    }

    private void DrawCombatDebug(GameSnapshot snapshot, PlayerData player, double centerX, double centerY, double worldUnitsPerPixel, FullMapProjection? fullMap)
    {
        if (!ShowBotDebug || _combatReport is null)
        {
            BotDebugText.Visibility = Visibility.Collapsed;
            return;
        }

        BotDebugText.Visibility = Visibility.Visible;
        BotDebugText.Text = $"{_combatReport.State} | target={TargetLabel(_combatReport.Target)} | route={_combatReport.RouteWaypointCount} | {_combatReport.LastAction}";

        if (_combatReport.Route.Count > 1)
        {
            Polyline route = new()
            {
                Stroke = new SolidColorBrush(Color.FromArgb(230, 67, 168, 255)),
                StrokeThickness = 2.2
            };

            foreach (RoutePoint point in _combatReport.Route.Take(80))
            {
                (double x, double y) = ProjectWorld(point.X, point.Y, player.Z, player, centerX, centerY, worldUnitsPerPixel, fullMap);
                route.Points.Add(new Point(x, y));
            }

            OverlayCanvas.Children.Add(route);
        }

        if (_combatReport.Target is { } target)
        {
            (double x, double y) = ProjectWorld(target.X, target.Y, player.Z, player, centerX, centerY, worldUnitsPerPixel, fullMap);
            DrawDebugRing(x, y, 18, Brushes.OrangeRed, $"target #{target.Id} {target.Category}");
        }

        if (_combatReport.NextWaypoint is { } waypoint)
        {
            (double x, double y) = ProjectWorld(waypoint.X, waypoint.Y, player.Z, player, centerX, centerY, worldUnitsPerPixel, fullMap);
            DrawDebugRing(x, y, 11, Brushes.DeepSkyBlue, "next waypoint");
        }

        if (_combatReport.IntendedClickPoint is { } click)
        {
            (double x, double y) = ProjectWorld(click.X, click.Y, player.Z, player, centerX, centerY, worldUnitsPerPixel, fullMap);
            DrawDebugCross(x, y, Brushes.Gold);
        }
    }

    private static string TargetLabel(BotDebugTarget? target)
    {
        return target is null ? "none" : $"#{target.Id} {target.Category} {target.Distance:0}u";
    }

    private void DrawDebugRing(double x, double y, double size, Brush brush, string tooltip)
    {
        Ellipse ring = new()
        {
            Width = size,
            Height = size,
            Stroke = brush,
            StrokeThickness = 2.4,
            Fill = Brushes.Transparent,
            ToolTip = tooltip
        };
        Canvas.SetLeft(ring, x - (size / 2));
        Canvas.SetTop(ring, y - (size / 2));
        OverlayCanvas.Children.Add(ring);
    }

    private void DrawDebugCross(double x, double y, Brush brush)
    {
        const double size = 8;
        OverlayCanvas.Children.Add(new Line { X1 = x - size, Y1 = y, X2 = x + size, Y2 = y, Stroke = brush, StrokeThickness = 2 });
        OverlayCanvas.Children.Add(new Line { X1 = x, Y1 = y - size, X2 = x, Y2 = y + size, Stroke = brush, StrokeThickness = 2 });
    }

    private (double X, double Y) ProjectWorld(double worldX, double worldY, double worldZ, PlayerData player, double centerX, double centerY, double worldUnitsPerPixel, FullMapProjection? fullMap)
    {
        if (fullMap is not null)
        {
            return ProjectFullGrid(worldX / Poe2Offsets.WorldToGridRatio, worldY / Poe2Offsets.WorldToGridRatio, fullMap);
        }

        return MapProjection.WorldToMapPoint(worldX, worldY, worldZ, player, centerX, centerY, worldUnitsPerPixel, RotateMap180);
    }

    private FullMapProjection? BuildFullMapProjection(TerrainData? terrain, double width, double height)
    {
        if (terrain is null || terrain.Width <= 0 || terrain.Height <= 0 || width <= 1 || height <= 1)
        {
            return null;
        }

        (double X, double Y)[] corners =
        [
            MapProjection.GridDeltaToMapDelta(0, 0, 1),
            MapProjection.GridDeltaToMapDelta(terrain.Width, 0, 1),
            MapProjection.GridDeltaToMapDelta(0, terrain.Height, 1),
            MapProjection.GridDeltaToMapDelta(terrain.Width, terrain.Height, 1)
        ];
        double minX = corners.Min(point => point.X);
        double maxX = corners.Max(point => point.X);
        double minY = corners.Min(point => point.Y);
        double maxY = corners.Max(point => point.Y);
        const double padding = 18;
        double rangeX = Math.Max(1, maxX - minX);
        double rangeY = Math.Max(1, maxY - minY);
        double scale = Math.Min((width - (padding * 2)) / rangeX, (height - (padding * 2)) / rangeY);
        scale = Math.Clamp(scale, 0.05, 24);
        double drawW = rangeX * scale;
        double drawH = rangeY * scale;
        return new FullMapProjection(minX, minY, scale, (width - drawW) / 2, (height - drawH) / 2, width, height);
    }

    private (double X, double Y) ProjectFullGrid(double gridX, double gridY, FullMapProjection projection)
    {
        (double isoX, double isoY) = MapProjection.GridDeltaToMapDelta(gridX, gridY, 1);
        double x = projection.OffsetX + ((isoX - projection.MinIsoX) * projection.Scale);
        double y = projection.OffsetY + ((isoY - projection.MinIsoY) * projection.Scale);
        return RotateMap180 ? (projection.Width - x, projection.Height - y) : (x, y);
    }

    private (double X, double Y) ProjectGridOffset(double gridX, double gridY, double centerX, double centerY, double pixelsPerGridCell)
    {
        (double dx, double dy) = MapProjection.GridDeltaToMapDelta(gridX, gridY, pixelsPerGridCell);
        return RotateMap180
            ? (centerX - dx, centerY - dy)
            : (centerX + dx, centerY + dy);
    }

    private void DrawPlayer(double x, double y)
    {
        Ellipse dot = new()
        {
            Width = 13,
            Height = 13,
            Fill = new SolidColorBrush(Color.FromRgb(201, 164, 92)),
            Stroke = Brushes.White,
            StrokeThickness = 1.2
        };
        Canvas.SetLeft(dot, x - 6.5);
        Canvas.SetTop(dot, y - 6.5);
        OverlayCanvas.Children.Add(dot);
    }

    private void DrawEntity(double x, double y, string category)
    {
        Brush brush = category switch
        {
            "Monster" => Brushes.IndianRed,
            "Chest" => Brushes.Goldenrod,
            "Transition" => Brushes.DeepSkyBlue,
            "Npc" => Brushes.LightSkyBlue,
            "Player" => Brushes.MediumSeaGreen,
            "Encounter" => Brushes.MediumPurple,
            "Loot" => Brushes.LawnGreen,
            _ => Brushes.Gray
        };

        double size = category == "Monster" ? 6 : 5;
        Ellipse dot = new()
        {
            Width = size,
            Height = size,
            Fill = brush,
            Stroke = Brushes.Black,
            StrokeThickness = 0.5
        };
        Canvas.SetLeft(dot, x - size / 2);
        Canvas.SetTop(dot, y - size / 2);
        OverlayCanvas.Children.Add(dot);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

    private sealed record FullMapProjection(
        double MinIsoX,
        double MinIsoY,
        double Scale,
        double OffsetX,
        double OffsetY,
        double Width,
        double Height);
}
