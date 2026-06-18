using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace POE2_AutoMate.Core.Memory;

public sealed class ProcessReader : IDisposable
{
    private const int ProcessVmRead = 0x0010;
    private const int ProcessQueryInformation = 0x0400;
    private const int ProcessQueryLimitedInformation = 0x1000;

    private nint _processHandle;
    private Process? _process;

    public bool IsAttached => _processHandle != nint.Zero && _process is not null && !_process.HasExited;
    public string ProcessName { get; private set; } = string.Empty;
    public int? ProcessId => _process?.Id;
    public string LastError { get; private set; } = string.Empty;

    public bool Attach(string processName)
    {
        Detach();

        if (string.IsNullOrWhiteSpace(processName))
        {
            LastError = "Process name is empty.";
            return false;
        }

        try
        {
            Process? process = Process.GetProcessesByName(processName)
                .OrderBy(candidate => candidate.Id)
                .FirstOrDefault();

            if (process is null)
            {
                LastError = $"Process '{processName}' was not found.";
                return false;
            }

            nint handle = OpenProcess(
                ProcessVmRead | ProcessQueryInformation | ProcessQueryLimitedInformation,
                false,
                process.Id);

            if (handle == nint.Zero)
            {
                LastError = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                process.Dispose();
                return false;
            }

            _process = process;
            _processHandle = handle;
            ProcessName = processName;
            LastError = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Detach();
            return false;
        }
    }

    public void Detach()
    {
        if (_processHandle != nint.Zero)
        {
            CloseHandle(_processHandle);
            _processHandle = nint.Zero;
        }

        _process?.Dispose();
        _process = null;
        ProcessName = string.Empty;
    }

    public byte[]? ReadMemory(long address, int size)
    {
        if (!IsAttached || address <= 0 || size <= 0)
        {
            return null;
        }

        byte[] buffer = new byte[size];
        if (!ReadProcessMemory(_processHandle, (nint)address, buffer, size, out nint bytesRead))
        {
            LastError = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return null;
        }

        int read = (int)Math.Min(bytesRead.ToInt64(), size);
        if (read <= 0)
        {
            LastError = "ReadProcessMemory returned zero bytes.";
            return null;
        }

        if (read == size)
        {
            LastError = string.Empty;
            return buffer;
        }

        LastError = $"Partial read: {read}/{size} bytes.";
        return buffer[..read];
    }

    public T? ReadStructure<T>(long address) where T : struct
    {
        byte[]? data = ReadMemory(address, Marshal.SizeOf<T>());
        if (data is null || data.Length < Marshal.SizeOf<T>())
        {
            return null;
        }

        GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            return Marshal.PtrToStructure<T>(handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
        }
    }

    public long ReadPointer(long address)
    {
        byte[]? data = ReadMemory(address, nint.Size);
        if (data is null || data.Length < nint.Size)
        {
            return 0;
        }

        return nint.Size == 8
            ? BitConverter.ToInt64(data, 0)
            : BitConverter.ToInt32(data, 0);
    }

    public string ReadStringUtf8(long address, int maxBytes = 256)
    {
        byte[]? data = ReadMemory(address, maxBytes);
        if (data is null || data.Length == 0)
        {
            return string.Empty;
        }

        int nullIndex = Array.IndexOf(data, (byte)0);
        int length = nullIndex >= 0 ? nullIndex : data.Length;
        return Encoding.UTF8.GetString(data, 0, length);
    }

    public string ReadStringUtf16(long address, int maxChars = 256)
    {
        byte[]? data = ReadMemory(address, maxChars * sizeof(char));
        if (data is null || data.Length < sizeof(char))
        {
            return string.Empty;
        }

        int length = 0;
        for (int i = 0; i + 1 < data.Length; i += 2)
        {
            if (data[i] == 0 && data[i + 1] == 0)
            {
                break;
            }

            length += 2;
        }

        return length == 0 ? string.Empty : Encoding.Unicode.GetString(data, 0, length);
    }

    public nint GetModuleBaseAddress()
    {
        try
        {
            return _process?.MainModule?.BaseAddress ?? nint.Zero;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return nint.Zero;
        }
    }

    public int GetModuleSize()
    {
        try
        {
            return _process?.MainModule?.ModuleMemorySize ?? 0;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return 0;
        }
    }

    public string GetModuleName()
    {
        try
        {
            return _process?.MainModule?.ModuleName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public void Dispose()
    {
        Detach();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(int desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(nint processHandle, nint baseAddress, byte[] buffer, int size, out nint bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);
}
