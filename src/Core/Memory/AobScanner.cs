using POE2_AutoMate.Core.Game;

namespace POE2_AutoMate.Core.Memory;

public sealed class AobScanner
{
    private readonly ProcessReader _reader;
    private readonly nint _baseAddress;
    private readonly int _moduleSize;
    private byte[]? _moduleData;

    public AobScanner(ProcessReader reader, nint baseAddress, int moduleSize)
    {
        _reader = reader;
        _baseAddress = baseAddress;
        _moduleSize = moduleSize;
    }

    public AobScanResult Scan(AobPattern pattern)
    {
        long address = ScanForPattern(pattern.Bytes, pattern.DispOffset, pattern.InstrLen);
        return new AobScanResult(pattern.Description, address, address != 0);
    }

    public long ScanForPattern(byte?[] pattern, int dispOffset, int instrLen)
    {
        if (!_reader.IsAttached || _baseAddress == nint.Zero || _moduleSize <= 0 || pattern.Length == 0)
        {
            return 0;
        }

        byte[]? data = LoadModule();
        if (data is null || data.Length < pattern.Length)
        {
            return 0;
        }

        for (int i = 0; i <= data.Length - pattern.Length; i++)
        {
            if (!Matches(data, i, pattern))
            {
                continue;
            }

            return ResolveRipRelativeTarget(data, i, dispOffset, instrLen);
        }

        return 0;
    }

    private byte[]? LoadModule()
    {
        if (_moduleData is not null)
        {
            return _moduleData;
        }

        _moduleData = _reader.ReadMemory(_baseAddress.ToInt64(), _moduleSize);
        return _moduleData;
    }

    private static bool Matches(byte[] data, int offset, byte?[] pattern)
    {
        for (int i = 0; i < pattern.Length; i++)
        {
            byte? expected = pattern[i];
            if (expected.HasValue && data[offset + i] != expected.Value)
            {
                return false;
            }
        }

        return true;
    }

    private long ResolveRipRelativeTarget(byte[] data, int matchOffset, int dispOffset, int instrLen)
    {
        int displacementIndex = matchOffset + dispOffset;
        if (displacementIndex < 0 || displacementIndex + sizeof(int) > data.Length)
        {
            return 0;
        }

        int displacement = BitConverter.ToInt32(data, displacementIndex);
        long instructionAddress = _baseAddress.ToInt64() + matchOffset;
        return instructionAddress + instrLen + displacement;
    }
}

public sealed record AobScanResult(string Description, long Address, bool IsFound);
