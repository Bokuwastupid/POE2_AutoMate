using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace POE2_AutoMate.Services;

public static class GameWindowLocator
{
    public static Rect? TryGetWindowRect(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return null;
        }

        using Process? process = Process.GetProcessesByName(processName)
            .OrderBy(candidate => candidate.Id)
            .FirstOrDefault();

        if (process is null || process.MainWindowHandle == nint.Zero)
        {
            return null;
        }

        if (!GetWindowRect(process.MainWindowHandle, out NativeRect rect))
        {
            return null;
        }

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        return width <= 0 || height <= 0 ? null : new Rect(rect.Left, rect.Top, width, height);
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeRect
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;
    }
}
