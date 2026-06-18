using System.Diagnostics;

namespace POE2_AutoMate.Core.Memory;

public sealed class ProcessManager
{
    public IReadOnlyList<ProcessInfo> FindProcesses(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return Array.Empty<ProcessInfo>();
        }

        return Process.GetProcessesByName(processName)
            .OrderBy(process => process.Id)
            .Select(process => new ProcessInfo(process.Id, process.ProcessName, TryGetMainWindowTitle(process)))
            .ToArray();
    }

    private static string TryGetMainWindowTitle(Process process)
    {
        try
        {
            return process.MainWindowTitle;
        }
        catch
        {
            return string.Empty;
        }
    }
}

public sealed record ProcessInfo(int Id, string Name, string WindowTitle);
