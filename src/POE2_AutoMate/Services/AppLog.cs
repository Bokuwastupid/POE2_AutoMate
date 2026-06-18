using System.IO;
using System.Text;

namespace POE2_AutoMate.Services;

public static class AppLog
{
    private static readonly object Sync = new();
    private static bool _initialized;

    public static string LogDirectory { get; } = Path.Combine(ProjectPaths.Root, "logs");
    public static string DailyLogPath { get; } = Path.Combine(LogDirectory, $"automate-{DateTime.Now:yyyyMMdd}.log");
    public static string LatestLogPath { get; } = Path.Combine(LogDirectory, "latest.log");

    public static void Write(string channel, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{channel}] {message}{Environment.NewLine}";

        lock (Sync)
        {
            EnsureInitialized();
            File.AppendAllText(DailyLogPath, line, Encoding.UTF8);
            File.AppendAllText(LatestLogPath, line, Encoding.UTF8);
        }
    }

    public static void WriteException(string channel, Exception exception)
    {
        Write(channel, exception.ToString());
    }

    private static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        Directory.CreateDirectory(LogDirectory);
        File.WriteAllText(
            LatestLogPath,
            $"POE2 AutoMate log started {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}{Environment.NewLine}",
            Encoding.UTF8);

        File.AppendAllText(
            DailyLogPath,
            $"--- Session {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ---{Environment.NewLine}",
            Encoding.UTF8);

        _initialized = true;
    }
}
