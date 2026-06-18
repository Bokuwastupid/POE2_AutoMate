using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace POE2_AutoMate.Core.Memory;

public sealed class MemoryReader : IDisposable
{
    private const int ProcessVmRead = 0x0010;
    private const int ProcessQueryInformation = 0x0400;

    private nint _processHandle;
    private Process? _process;

    public bool IsAttached => _processHandle != nint.Zero && _process is not null && !_process.HasExited;
    public string ProcessName { get; private set; } = string.Empty;
    public int? ProcessId => _process?.Id;
    public string LastError { get; private set; } = string.Empty;

    public bool Attach(string processName)
    {
        Detach();

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

            nint handle = OpenProcess(ProcessVmRead | ProcessQueryInformation, false, process.Id);
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
        if (!IsAttached || size <= 0)
        {
            return null;
        }

        byte[] buffer = new byte[size];
        bool success = ReadProcessMemory(_processHandle, (nint)address, buffer, size, out nint bytesRead);

        if (!success || bytesRead.ToInt64() != size)
        {
            LastError = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return null;
        }

        LastError = string.Empty;
        return buffer;
    }

    public T? ReadStructure<T>(long address) where T : struct
    {
        byte[]? data = ReadMemory(address, Marshal.SizeOf<T>());
        if (data is null)
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
