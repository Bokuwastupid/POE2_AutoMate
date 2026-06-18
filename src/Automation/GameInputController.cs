using System.Diagnostics;
using System.Runtime.InteropServices;
using POE2_AutoMate.Core.Game;

namespace POE2_AutoMate.Automation;

public sealed class GameInputController
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventKeyDown = 0x0000;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventRightDown = 0x0008;
    private const uint MouseEventRightUp = 0x0010;
    private const ushort VirtualKeyControl = 0x11;
    private const ushort VirtualKeyC = 0x43;
    private const uint ClipboardUnicodeText = 13;
    private readonly HashSet<ushort> _heldKeys = [];
    private string _heldProcessName = string.Empty;

    // Process-wide action-rate gate. PoE servers disconnect a client that sends too many action packets
    // (move/attack/use) per second — it's a SERVER rate limit, not synthetic-input detection, so the only
    // real fix is staying under a human-plausible action rate. Shared across every GameInputController
    // instance (AutoMapper + CombatBot) so the whole process honours one budget.
    private static readonly object ActionGate = new();
    private static readonly Random ActionJitter = new();
    private static DateTime _lastActionUtc = DateTime.MinValue;

    /// <summary>Minimum milliseconds between any two game actions (clicks/keys that the server sees). ~90ms
    /// ≈ 11 actions/s, comfortably under PoE's disconnect threshold. 0 disables the gate.</summary>
    public int MinActionIntervalMs { get; set; } = 90;

    /// <summary>Extra random 0..N ms added to each gap so timing is not perfectly periodic.</summary>
    public int ActionJitterMs { get; set; } = 30;

    public string LastError { get; private set; } = string.Empty;

    /// <summary>Block until at least <see cref="MinActionIntervalMs"/> (+ jitter) has passed since the last
    /// action anywhere in the process. Call immediately before a SendInput that produces a game action.</summary>
    private void GateAction()
    {
        if (MinActionIntervalMs <= 0)
        {
            return;
        }

        lock (ActionGate)
        {
            int jitter = ActionJitterMs > 0 ? ActionJitter.Next(0, ActionJitterMs) : 0;
            TimeSpan minGap = TimeSpan.FromMilliseconds(MinActionIntervalMs + jitter);
            TimeSpan elapsed = DateTime.UtcNow - _lastActionUtc;
            TimeSpan wait = minGap - elapsed;
            if (wait > TimeSpan.Zero && wait < TimeSpan.FromSeconds(1))
            {
                Thread.Sleep(wait);
            }

            _lastActionUtc = DateTime.UtcNow;
        }
    }

    public bool PressKey(string processName, string keyToken)
    {
        if (TryParseMouseButton(keyToken, out bool rightButton))
        {
            return ClickCurrentMousePosition(processName, rightButton);
        }

        if (!TryParseVirtualKey(keyToken, out ushort virtualKey))
        {
            LastError = $"Unsupported key token '{keyToken}'.";
            return false;
        }

        if (!TryGetProcessWindow(processName, out nint handle))
        {
            return false;
        }

        SetForegroundWindow(handle);
        Thread.Sleep(20);

        Input[] inputs =
        [
            Input.Key(virtualKey, 0),
            Input.Key(virtualKey, KeyEventKeyUp)
        ];

        GateAction();
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent != inputs.Length)
        {
            LastError = $"SendInput sent {sent}/{inputs.Length} event(s).";
            return false;
        }

        LastError = string.Empty;
        return true;
    }

    public bool CopyHoveredText(string processName, out string text, int waitMs = 120)
    {
        text = string.Empty;
        if (!TryGetProcessWindow(processName, out nint handle))
        {
            return false;
        }

        SetForegroundWindow(handle);
        Thread.Sleep(20);

        if (!TryClearClipboard(handle, out string clipboardError))
        {
            LastError = clipboardError;
            return false;
        }

        Input[] inputs =
        [
            Input.Key(VirtualKeyControl, KeyEventKeyDown),
            Input.Key(VirtualKeyC, KeyEventKeyDown),
            Input.Key(VirtualKeyC, KeyEventKeyUp),
            Input.Key(VirtualKeyControl, KeyEventKeyUp)
        ];

        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent != inputs.Length)
        {
            LastError = $"SendInput Ctrl+C sent {sent}/{inputs.Length} event(s).";
            return false;
        }

        Thread.Sleep(Math.Clamp(waitMs, 50, 500));
        if (!TryReadClipboardText(out text, out clipboardError))
        {
            LastError = clipboardError;
            return false;
        }

        LastError = string.Empty;
        return true;
    }

    private bool ClickCurrentMousePosition(string processName, bool rightButton)
    {
        if (!TryGetProcessWindow(processName, out nint handle))
        {
            return false;
        }

        SetForegroundWindow(handle);
        Thread.Sleep(20);

        Input[] inputs =
        [
            Input.Mouse(rightButton ? MouseEventRightDown : MouseEventLeftDown),
            Input.Mouse(rightButton ? MouseEventRightUp : MouseEventLeftUp)
        ];

        GateAction();
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent != inputs.Length)
        {
            LastError = $"SendInput mouse sent {sent}/{inputs.Length} event(s).";
            return false;
        }

        LastError = string.Empty;
        return true;
    }

    public bool ClickDirection(string processName, double directionX, double directionY, double distancePixels)
    {
        if (!TryGetProcessWindow(processName, out nint handle))
        {
            return false;
        }

        if (!GetWindowRect(handle, out NativeRect rect))
        {
            LastError = "Could not read game window rectangle.";
            return false;
        }

        double length = Math.Sqrt((directionX * directionX) + (directionY * directionY));
        if (length < 0.001)
        {
            LastError = "Movement direction is too small.";
            return false;
        }

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            LastError = "Game window rectangle is invalid.";
            return false;
        }

        int centerX = rect.Left + (width / 2);
        int centerY = rect.Top + (height / 2);
        int targetX = centerX + (int)Math.Round(directionX / length * distancePixels);
        int targetY = centerY + (int)Math.Round(directionY / length * distancePixels);

        SetForegroundWindow(handle);
        Thread.Sleep(20);
        SetCursorPos(targetX, targetY);
        Thread.Sleep(10);

        Input[] inputs =
        [
            Input.Mouse(MouseEventLeftDown),
            Input.Mouse(MouseEventLeftUp)
        ];

        GateAction();
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent != inputs.Length)
        {
            LastError = $"SendInput mouse sent {sent}/{inputs.Length} event(s).";
            return false;
        }

        LastError = string.Empty;
        return true;
    }

    public bool ClickWindowRelativePoint(string processName, double relativeX, double relativeY, bool rightButton = false, bool ctrl = false)
    {
        if (!TryGetProcessWindow(processName, out nint handle))
        {
            return false;
        }

        if (!GetWindowRect(handle, out NativeRect rect))
        {
            LastError = "Could not read game window rectangle.";
            return false;
        }

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            LastError = "Game window rectangle is invalid.";
            return false;
        }

        double clampedX = Math.Clamp(relativeX, 0, 1);
        double clampedY = Math.Clamp(relativeY, 0, 1);
        return ClickWindowPoint(handle, rect.Left + (clampedX * width), rect.Top + (clampedY * height), rightButton, ctrl);
    }

    public bool MoveWindowRelativePoint(string processName, double relativeX, double relativeY)
    {
        if (!TryGetProcessWindow(processName, out nint handle))
        {
            return false;
        }

        if (!GetWindowRect(handle, out NativeRect rect))
        {
            LastError = "Could not read game window rectangle.";
            return false;
        }

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            LastError = "Game window rectangle is invalid.";
            return false;
        }

        double clampedX = Math.Clamp(relativeX, 0, 1);
        double clampedY = Math.Clamp(relativeY, 0, 1);
        SetForegroundWindow(handle);
        Thread.Sleep(10);
        SetCursorPos(rect.Left + (int)Math.Round(clampedX * width), rect.Top + (int)Math.Round(clampedY * height));
        LastError = string.Empty;
        return true;
    }

    public bool ClickWindowPixelPoint(string processName, double x, double y, bool rightButton = false, bool ctrl = false)
    {
        if (!TryGetProcessWindow(processName, out nint handle))
        {
            return false;
        }

        if (!GetWindowRect(handle, out NativeRect rect))
        {
            LastError = "Could not read game window rectangle.";
            return false;
        }

        return ClickWindowPoint(handle, rect.Left + x, rect.Top + y, rightButton, ctrl);
    }

    public bool ClickWorldPoint(
        string processName,
        double worldX,
        double worldY,
        double worldZ,
        IReadOnlyList<float>? cameraMatrix,
        double screenOffsetX = 0,
        double screenOffsetY = 0)
    {
        if (!TryGetProcessWindow(processName, out nint handle))
        {
            return false;
        }

        if (!GetWindowRect(handle, out NativeRect rect))
        {
            LastError = "Could not read game window rectangle.";
            return false;
        }

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (!MapProjection.TryWorldToScreen(cameraMatrix, worldX, worldY, worldZ, width, height, out double screenX, out double screenY))
        {
            LastError = "Target is outside camera projection.";
            return false;
        }

        return ClickWindowPoint(handle, rect.Left + screenX + screenOffsetX, rect.Top + screenY + screenOffsetY);
    }

    private bool ClickWindowPoint(nint handle, double screenX, double screenY, bool rightButton = false, bool ctrl = false)
    {
        SetForegroundWindow(handle);
        Thread.Sleep(20);
        SetCursorPos((int)Math.Round(screenX), (int)Math.Round(screenY));
        Thread.Sleep(10);

        List<Input> inputs = [];
        if (ctrl)
        {
            inputs.Add(Input.Key(0x11, KeyEventKeyDown));
            Thread.Sleep(5);
        }

        inputs.Add(Input.Mouse(rightButton ? MouseEventRightDown : MouseEventLeftDown));
        inputs.Add(Input.Mouse(rightButton ? MouseEventRightUp : MouseEventLeftUp));

        if (ctrl)
        {
            inputs.Add(Input.Key(0x11, KeyEventKeyUp));
        }

        GateAction();
        uint sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Input>());
        if (sent != inputs.Count)
        {
            LastError = $"SendInput mouse sent {sent}/{inputs.Count} event(s).";
            return false;
        }

        LastError = string.Empty;
        return true;
    }

    public bool HoldKeys(string processName, IReadOnlyList<string> keyTokens, int holdMs)
    {
        if (keyTokens.Count == 0)
        {
            LastError = "No movement keys selected.";
            return false;
        }

        if (!TryGetProcessWindow(processName, out nint handle))
        {
            return false;
        }

        List<ushort> keys = [];
        foreach (string token in keyTokens)
        {
            if (!TryParseVirtualKey(token, out ushort key))
            {
                LastError = $"Unsupported key token '{token}'.";
                return false;
            }

            keys.Add(key);
        }

        SetForegroundWindow(handle);
        Thread.Sleep(20);

        Input[] keyDown = keys.Select(key => Input.Key(key, KeyEventKeyDown)).ToArray();
        GateAction();
        uint downSent = SendInput((uint)keyDown.Length, keyDown, Marshal.SizeOf<Input>());
        Thread.Sleep(Math.Clamp(holdMs, 40, 2000));

        Input[] keyUp = keys.AsEnumerable().Reverse().Select(key => Input.Key(key, KeyEventKeyUp)).ToArray();
        uint upSent = SendInput((uint)keyUp.Length, keyUp, Marshal.SizeOf<Input>());
        if (downSent != keyDown.Length || upSent != keyUp.Length)
        {
            LastError = $"SendInput hold sent down {downSent}/{keyDown.Length}, up {upSent}/{keyUp.Length}.";
            return false;
        }

        LastError = string.Empty;
        return true;
    }

    public bool SetHeldKeys(string processName, IReadOnlyList<string> keyTokens)
    {
        if (keyTokens.Count == 0)
        {
            return ReleaseHeldKeys(processName);
        }

        if (!TryGetProcessWindow(processName, out nint handle))
        {
            return false;
        }

        HashSet<ushort> desiredKeys = [];
        foreach (string token in keyTokens)
        {
            if (!TryParseVirtualKey(token, out ushort key))
            {
                LastError = $"Unsupported key token '{token}'.";
                return false;
            }

            desiredKeys.Add(key);
        }

        if (!_heldProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase))
        {
            ReleaseHeldKeys(_heldProcessName);
            _heldProcessName = processName;
        }

        ushort[] keysToRelease = _heldKeys.Except(desiredKeys).ToArray();
        ushort[] keysToPress = desiredKeys.Except(_heldKeys).ToArray();
        if (keysToRelease.Length == 0 && keysToPress.Length == 0)
        {
            LastError = string.Empty;
            return true;
        }

        SetForegroundWindow(handle);
        Thread.Sleep(10);

        List<Input> inputs = [];
        inputs.AddRange(keysToRelease.Reverse().Select(key => Input.Key(key, KeyEventKeyUp)));
        inputs.AddRange(keysToPress.Select(key => Input.Key(key, KeyEventKeyDown)));
        GateAction();
        uint sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Input>());
        if (sent != inputs.Count)
        {
            LastError = $"SendInput held movement sent {sent}/{inputs.Count} event(s).";
            return false;
        }

        _heldKeys.Clear();
        foreach (ushort key in desiredKeys)
        {
            _heldKeys.Add(key);
        }

        LastError = string.Empty;
        return true;
    }

    public bool ReleaseHeldKeys(string processName)
    {
        if (_heldKeys.Count == 0)
        {
            LastError = string.Empty;
            return true;
        }

        if (string.IsNullOrWhiteSpace(processName))
        {
            processName = _heldProcessName;
        }

        if (!TryGetProcessWindow(processName, out nint handle))
        {
            return false;
        }

        SetForegroundWindow(handle);
        Thread.Sleep(10);

        Input[] keyUp = _heldKeys.Reverse().Select(key => Input.Key(key, KeyEventKeyUp)).ToArray();
        uint sent = SendInput((uint)keyUp.Length, keyUp, Marshal.SizeOf<Input>());
        if (sent != keyUp.Length)
        {
            LastError = $"SendInput release held keys sent {sent}/{keyUp.Length} event(s).";
            return false;
        }

        _heldKeys.Clear();
        _heldProcessName = string.Empty;
        LastError = string.Empty;
        return true;
    }

    private bool TryGetProcessWindow(string processName, out nint handle)
    {
        handle = nint.Zero;
        if (string.IsNullOrWhiteSpace(processName))
        {
            LastError = "Process name is empty.";
            return false;
        }

        Process? process = Process.GetProcessesByName(processName)
            .OrderBy(candidate => candidate.Id)
            .FirstOrDefault(candidate => candidate.MainWindowHandle != nint.Zero);

        if (process is null)
        {
            LastError = $"Process '{processName}' window was not found.";
            return false;
        }

        handle = process.MainWindowHandle;
        return true;
    }

    private static bool TryParseVirtualKey(string token, out ushort virtualKey)
    {
        virtualKey = 0;
        token = token.Trim();
        if (token.Length == 1)
        {
            char c = char.ToUpperInvariant(token[0]);
            if (c is >= '0' and <= '9' or >= 'A' and <= 'Z')
            {
                virtualKey = c;
                return true;
            }

            if (c == '-')
            {
                virtualKey = 0xBD;
                return true;
            }
        }

        string normalized = token.Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase).ToUpperInvariant();
        if (normalized.StartsWith("F", StringComparison.Ordinal) &&
            int.TryParse(normalized[1..], out int functionKey) &&
            functionKey is >= 1 and <= 12)
        {
            virtualKey = (ushort)(0x70 + functionKey - 1);
            return true;
        }

        virtualKey = normalized switch
        {
            "SPACE" => 0x20,
            "TAB" => 0x09,
            "ENTER" => 0x0D,
            "ESC" or "ESCAPE" => 0x1B,
            "SHIFT" => 0x10,
            "CTRL" or "CONTROL" => 0x11,
            "ALT" => 0x12,
            "-" or "OEMMINUS" or "MINUS" => 0xBD,
            "NUM-" or "NUMPADSUBTRACT" => 0x6D,
            _ => 0
        };

        return virtualKey != 0;
    }

    private static bool TryParseMouseButton(string token, out bool rightButton)
    {
        rightButton = false;
        string normalized = token.Trim().Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase).ToUpperInvariant();
        if (normalized is "LMB" or "MOUSE1" or "LEFTMOUSE" or "LEFTCLICK")
        {
            return true;
        }

        if (normalized is "RMB" or "MOUSE2" or "RIGHTMOUSE" or "RIGHTCLICK")
        {
            rightButton = true;
            return true;
        }

        return false;
    }

    private static bool TryClearClipboard(nint owner, out string error)
    {
        error = string.Empty;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            if (OpenClipboard(owner))
            {
                try
                {
                    if (!EmptyClipboard())
                    {
                        error = $"Could not clear clipboard before Ctrl+C. Win32={Marshal.GetLastWin32Error()}.";
                        return false;
                    }

                    return true;
                }
                finally
                {
                    CloseClipboard();
                }
            }

            Thread.Sleep(25);
        }

        error = $"Could not open clipboard before Ctrl+C. Win32={Marshal.GetLastWin32Error()}.";
        return false;
    }

    private static bool TryReadClipboardText(out string text, out string error)
    {
        text = string.Empty;
        error = string.Empty;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            if (OpenClipboard(nint.Zero))
            {
                try
                {
                    nint dataHandle = GetClipboardData(ClipboardUnicodeText);
                    if (dataHandle == nint.Zero)
                    {
                        error = "Clipboard did not contain copied Unicode item text.";
                        return false;
                    }

                    nint dataPointer = GlobalLock(dataHandle);
                    if (dataPointer == nint.Zero)
                    {
                        error = $"Could not lock clipboard text. Win32={Marshal.GetLastWin32Error()}.";
                        return false;
                    }

                    try
                    {
                        text = Marshal.PtrToStringUni(dataPointer) ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(text))
                        {
                            error = "Clipboard item text was empty after Ctrl+C.";
                            return false;
                        }

                        return true;
                    }
                    finally
                    {
                        GlobalUnlock(dataHandle);
                    }
                }
                finally
                {
                    CloseClipboard();
                }
            }

            Thread.Sleep(25);
        }

        error = $"Could not open clipboard after Ctrl+C. Win32={Marshal.GetLastWin32Error()}.";
        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint cInputs, Input[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(nint hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetClipboardData(uint uFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(nint hMem);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;

        public static Input Key(ushort virtualKey, uint flags)
        {
            return new Input
            {
                Type = InputKeyboard,
                Union = new InputUnion
                {
                    Keyboard = new KeyboardInput
                    {
                        VirtualKey = virtualKey,
                        ScanCode = 0,
                        Flags = flags,
                        Time = 0,
                        ExtraInfo = nint.Zero
                    }
                }
            };
        }

        public static Input Mouse(uint flags)
        {
            return new Input
            {
                Type = InputMouse,
                Union = new InputUnion
                {
                    Mouse = new MouseInput
                    {
                        Dx = 0,
                        Dy = 0,
                        MouseData = 0,
                        Flags = flags,
                        Time = 0,
                        ExtraInfo = nint.Zero
                    }
                }
            };
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeRect
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;
    }
}
