using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace POE2_SignatureFinder;

internal static class Program
{
    private const int ProcessVmRead = 0x0010;
    private const int ProcessQueryInformation = 0x0400;
    private const int GameStateCurrentStatePtr = 0x08;
    private const int GameStateStates = 0x48;
    private const int GameStateStateSlotStride = 0x10;
    private const int GameStateStateSlotCount = 12;
    private const int InGameStateAreaInstanceData = 0x290;
    private const int AreaInstanceLocalPlayer = 0x5A0;

    private static readonly Signature[] DefaultSignatures =
    [
        new(
            "GameState global slot",
            "48 39 2D ?? ?? ?? ?? 0F 85 16 01 00 00",
            dispOffset: 3,
            instrLen: 7,
            kind: SignatureKind.GameState),
        new(
            "InGameState global slot",
            "8B C7 48 83 C4 40 5F C3 48 8B 05 ?? ?? ?? ?? 48 89 07 48",
            dispOffset: 11,
            instrLen: 15)
    ];

    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("[FATAL] Unexpected error:");
            Console.Error.WriteLine(ex);
            return 10;
        }
        finally
        {
            if (!args.Contains("--no-pause", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine();
                Console.WriteLine("Press Enter to close...");
                Console.ReadLine();
            }
        }
    }

    private static int Run(string[] args)
    {
        string processName = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal)) ?? "PathOfExileSteam";
        bool wait = args.Contains("--wait", StringComparer.OrdinalIgnoreCase);

        Console.WriteLine("=== POE2 Signature Finder ===");
        Console.WriteLine($"Target process: {processName}");
        Console.WriteLine("This tool reads a local process module and scans for byte patterns.");
        Console.WriteLine();

        if (wait)
        {
            Console.WriteLine("Press Enter to start scanning...");
            Console.ReadLine();
        }

        using Process? process = Process.GetProcessesByName(processName)
            .OrderBy(candidate => candidate.Id)
            .FirstOrDefault();

        if (process is null)
        {
            Console.Error.WriteLine($"[ERROR] Process '{processName}' not found.");
            return 2;
        }

        Console.WriteLine($"[OK] Process found: {process.ProcessName} ({process.Id})");

        using ProcessModule? mainModule = GetMainModule(process);
        if (mainModule is null)
        {
            Console.Error.WriteLine("[ERROR] Could not read main module metadata.");
            return 3;
        }

        nint processHandle = OpenProcess(ProcessVmRead | ProcessQueryInformation, false, process.Id);
        if (processHandle == nint.Zero)
        {
            Console.Error.WriteLine($"[ERROR] Failed to open process: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            return 4;
        }

        try
        {
            Console.WriteLine($"[OK] Module: {mainModule.ModuleName}");
            Console.WriteLine($"[OK] Base: 0x{mainModule.BaseAddress.ToInt64():X}, Size: {mainModule.ModuleMemorySize:N0} bytes");
            Console.WriteLine();

            int foundCount = 0;
            foreach (Signature signature in DefaultSignatures)
            {
                Console.WriteLine($"Scanning for: {signature.Name}");
                ScanHit hit = ScanPattern(processHandle, mainModule.BaseAddress, mainModule.ModuleMemorySize, signature);

                if (!hit.IsFound)
                {
                    Console.WriteLine($"[MISS] {signature.Name}");
                    continue;
                }

                foundCount++;
                Console.WriteLine($"[FOUND] {signature.Name}");
                Console.WriteLine($"  Match:  0x{hit.MatchAddress:X}");
                Console.WriteLine($"  Slot:   0x{hit.SlotAddress:X}");
                Console.WriteLine($"  Target: {(hit.TargetAddress == 0 ? "n/a" : $"0x{hit.TargetAddress:X}")}");
                PrintPreview(processHandle, hit.MatchAddress, 16);
            }

            Console.WriteLine();
            Console.WriteLine($"Scan complete. Found {foundCount}/{DefaultSignatures.Length} signature(s).");
            if (foundCount == 0)
            {
                Console.WriteLine("No signatures were found. The stored AOB patterns may be outdated for this game build.");
            }

            ScanHit? gameStateHit = DefaultSignatures
                .Where(signature => signature.Kind == SignatureKind.GameState)
                .Select(signature => ScanPattern(processHandle, mainModule.BaseAddress, mainModule.ModuleMemorySize, signature))
                .FirstOrDefault(hit => hit.IsFound);

            if (gameStateHit is { IsFound: true })
            {
                Console.WriteLine();
                ProbeGameChain(processHandle, gameStateHit.Value.TargetAddress);
            }

            return 0;
        }
        finally
        {
            CloseHandle(processHandle);
        }
    }

    private static ProcessModule? GetMainModule(Process process)
    {
        try
        {
            return process.MainModule;
        }
        catch
        {
            return null;
        }
    }

    private static ScanHit ScanPattern(nint processHandle, nint baseAddress, int moduleSize, Signature signature)
    {
        if (!ParsePattern(signature.Pattern, out byte[] searchBytes, out bool[] wildcards))
        {
            Console.WriteLine("[ERROR] Invalid pattern.");
            return ScanHit.Miss;
        }

        byte[] buffer = new byte[moduleSize];
        if (!ReadProcessMemory(processHandle, baseAddress, buffer, moduleSize, out nint bytesRead))
        {
            Console.WriteLine($"[ERROR] ReadProcessMemory failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            return ScanHit.Miss;
        }

        int available = Math.Min((int)bytesRead, buffer.Length);
        for (int i = 0; i <= available - searchBytes.Length; i++)
        {
            if (IsMatch(buffer, i, searchBytes, wildcards))
            {
                long match = baseAddress.ToInt64() + i;
                long slot = ResolveRipRelative(baseAddress, i, signature.DispOffset, signature.InstrLen, buffer);
                long target = ReadPointer(processHandle, slot);
                return new ScanHit(match, slot, target);
            }
        }

        return ScanHit.Miss;
    }

    private static long ResolveRipRelative(nint baseAddress, int matchOffset, int dispOffset, int instrLen, byte[] buffer)
    {
        int dispPosition = matchOffset + dispOffset;
        if (dispPosition < 0 || dispPosition + sizeof(int) > buffer.Length)
        {
            return 0;
        }

        int displacement = BitConverter.ToInt32(buffer, dispPosition);
        return baseAddress.ToInt64() + matchOffset + instrLen + displacement;
    }

    private static long ReadPointer(nint processHandle, long address)
    {
        if (address == 0)
        {
            return 0;
        }

        byte[] pointerBytes = new byte[nint.Size];
        if (!ReadProcessMemory(processHandle, (nint)address, pointerBytes, pointerBytes.Length, out nint bytesRead)
            || bytesRead.ToInt64() != pointerBytes.Length)
        {
            return 0;
        }

        return nint.Size == 8
            ? BitConverter.ToInt64(pointerBytes, 0)
            : BitConverter.ToInt32(pointerBytes, 0);
    }

    private static bool IsMatch(byte[] buffer, int offset, byte[] searchBytes, bool[] wildcards)
    {
        for (int i = 0; i < searchBytes.Length; i++)
        {
            if (wildcards[i])
            {
                continue;
            }

            if (buffer[offset + i] != searchBytes[i])
            {
                return false;
            }
        }

        return true;
    }

    private static bool ParsePattern(string pattern, out byte[] bytes, out bool[] wildcards)
    {
        List<byte> byteList = [];
        List<bool> wildcardList = [];

        foreach (string part in pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part is "?" or "??")
            {
                byteList.Add(0x00);
                wildcardList.Add(true);
                continue;
            }

            if (!byte.TryParse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte value))
            {
                bytes = [];
                wildcards = [];
                return false;
            }

            byteList.Add(value);
            wildcardList.Add(false);
        }

        bytes = byteList.ToArray();
        wildcards = wildcardList.ToArray();
        return bytes.Length > 0;
    }

    private static void PrintPreview(nint processHandle, long address, int length)
    {
        byte[] preview = new byte[length];
        if (!ReadProcessMemory(processHandle, (nint)address, preview, preview.Length, out nint bytesRead))
        {
            Console.WriteLine("  Preview unavailable.");
            return;
        }

        Console.WriteLine($"  Preview: {BitConverter.ToString(preview, 0, (int)bytesRead).Replace("-", " ")}");
    }

    private static void ProbeGameChain(nint processHandle, long gameState)
    {
        Console.WriteLine("=== Game Chain Probe ===");
        if (!IsPlausiblePointer(gameState))
        {
            Console.WriteLine($"[MISS] GameState target is not a plausible pointer: 0x{gameState:X}");
            return;
        }

        Console.WriteLine($"GameState: 0x{gameState:X}");

        List<StateCandidate> candidates = [];
        long vectorFirst = ReadPointer(processHandle, gameState + GameStateCurrentStatePtr);
        if (IsPlausiblePointer(vectorFirst))
        {
            long currentState = ReadPointer(processHandle, vectorFirst);
            candidates.Add(new StateCandidate("CurrentStatePtr[0]", currentState));
        }
        else
        {
            Console.WriteLine("CurrentStatePtr vector is not readable/plausible.");
        }

        for (int i = 0; i < GameStateStateSlotCount; i++)
        {
            long state = ReadPointer(processHandle, gameState + GameStateStates + (i * GameStateStateSlotStride));
            candidates.Add(new StateCandidate($"States[{i}]", state));
        }

        bool resolved = false;
        foreach (StateCandidate candidate in candidates.Where(candidate => IsPlausiblePointer(candidate.Address)))
        {
            long areaInstance = ReadPointer(processHandle, candidate.Address + InGameStateAreaInstanceData);
            long localPlayer = IsPlausiblePointer(areaInstance)
                ? ReadPointer(processHandle, areaInstance + AreaInstanceLocalPlayer)
                : 0;

            string status = IsPlausiblePointer(areaInstance) && IsPlausiblePointer(localPlayer)
                ? "OK"
                : "MISS";

            Console.WriteLine($"[{status}] {candidate.Name}: IGS=0x{candidate.Address:X}, Area=0x{areaInstance:X}, LocalPlayer=0x{localPlayer:X}");

            if (status == "OK")
            {
                resolved = true;
                break;
            }
        }

        if (!resolved)
        {
            Console.WriteLine("[MISS] Could not resolve InGameState -> AreaInstance -> LocalPlayer from GameState.");
            Console.WriteLine("This usually means you are not in-game yet, the area is loading, or offsets drifted.");
        }
    }

    private static bool IsPlausiblePointer(long value)
    {
        ulong pointer = (ulong)value;
        return pointer is >= 0x10000 and <= 0x7FFFFFFFFFFF;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(int desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(nint processHandle, nint baseAddress, byte[] buffer, int size, out nint bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);
}

internal sealed class Signature
{
    public Signature(string name, string pattern, int dispOffset, int instrLen, SignatureKind kind = SignatureKind.Other)
    {
        Name = name;
        Pattern = pattern;
        DispOffset = dispOffset;
        InstrLen = instrLen;
        Kind = kind;
    }

    public string Name { get; }
    public string Pattern { get; }
    public int DispOffset { get; }
    public int InstrLen { get; }
    public SignatureKind Kind { get; }
}

internal readonly record struct ScanHit(long MatchAddress, long SlotAddress, long TargetAddress)
{
    public static ScanHit Miss => new(0, 0, 0);
    public bool IsFound => MatchAddress != 0 && SlotAddress != 0;
}

internal enum SignatureKind
{
    Other,
    GameState
}

internal readonly record struct StateCandidate(string Name, long Address);
