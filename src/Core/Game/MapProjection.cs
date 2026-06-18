namespace POE2_AutoMate.Core.Game;

public static class MapProjection
{
    private const double CameraAngleRad = 38.7 * Math.PI / 180.0;
    private static readonly double CameraCos = Math.Cos(CameraAngleRad);
    private static readonly double CameraSin = Math.Sin(CameraAngleRad);

    public static (double X, double Y) GridDeltaToMapDelta(double gridDx, double gridDy, double mapScale, double gridDz = 0)
    {
        return (
            mapScale * (gridDx - gridDy) * CameraCos,
            mapScale * (gridDz - (gridDx + gridDy)) * CameraSin);
    }

    public static (double X, double Y) WorldToMapPoint(
        double worldX,
        double worldY,
        double worldZ,
        PlayerData player,
        double centerX,
        double centerY,
        double worldUnitsPerPixel,
        bool rotate180)
    {
        double scale = Poe2Offsets.WorldToGridRatio / Math.Max(1, worldUnitsPerPixel);
        double gridDx = (worldX - player.X) / Poe2Offsets.WorldToGridRatio;
        double gridDy = (worldY - player.Y) / Poe2Offsets.WorldToGridRatio;
        double gridDz = (worldZ - player.Z) / Poe2Offsets.WorldToGridRatio;
        (double dx, double dy) = GridDeltaToMapDelta(gridDx, gridDy, scale, gridDz);
        return rotate180
            ? (centerX - dx, centerY - dy)
            : (centerX + dx, centerY + dy);
    }

    public static bool TryWorldToScreen(
        IReadOnlyList<float>? matrix,
        double worldX,
        double worldY,
        double worldZ,
        double windowWidth,
        double windowHeight,
        out double screenX,
        out double screenY)
    {
        screenX = 0;
        screenY = 0;
        if (matrix is null || matrix.Count < 16 || windowWidth <= 0 || windowHeight <= 0)
        {
            return false;
        }

        double cw = (worldX * matrix[3]) + (worldY * matrix[7]) + (worldZ * matrix[11]) + matrix[15];
        if (cw <= 0.0001)
        {
            return false;
        }

        double cx = (worldX * matrix[0]) + (worldY * matrix[4]) + (worldZ * matrix[8]) + matrix[12];
        double cy = (worldX * matrix[1]) + (worldY * matrix[5]) + (worldZ * matrix[9]) + matrix[13];
        screenX = ((cx / cw / 2.0) + 0.5) * windowWidth;
        screenY = (0.5 - (cy / cw / 2.0)) * windowHeight;
        return screenX >= 0 && screenX <= windowWidth && screenY >= 0 && screenY <= windowHeight;
    }
}
