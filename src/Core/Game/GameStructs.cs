using System.Runtime.InteropServices;

namespace POE2_AutoMate.Core.Game;

[StructLayout(LayoutKind.Sequential)]
public struct StdVector
{
    public nint First;
    public nint Last;
    public nint End;
}

[StructLayout(LayoutKind.Sequential)]
public struct Vector3
{
    public float X;
    public float Y;
    public float Z;
}

[StructLayout(LayoutKind.Sequential)]
public struct Vector2
{
    public float X;
    public float Y;
}

[StructLayout(LayoutKind.Sequential)]
public struct Int2
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Explicit, Size = 0x34)]
public struct VitalStruct
{
    [FieldOffset(0x10)] public int ReservedFlat;
    [FieldOffset(0x14)] public int ReservedFraction;
    [FieldOffset(0x28)] public float Regen;
    [FieldOffset(0x2C)] public int Max;
    [FieldOffset(0x30)] public int Current;

    public readonly bool LooksValid()
    {
        if (Max <= 0 || Max > 10_000_000)
        {
            return false;
        }

        if (Current < -Max || Current > Max + 1)
        {
            return false;
        }

        return ReservedFlat >= 0 && ReservedFlat <= Max;
    }
}
