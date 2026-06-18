using System.IO;

namespace POE2_AutoMate.Services;

public static class ProjectPaths
{
    public static string Root { get; } = FindProjectRoot(AppContext.BaseDirectory);

    private static string FindProjectRoot(string startPath)
    {
        DirectoryInfo? directory = new(startPath);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "POE2_AutoMate.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return AppContext.BaseDirectory;
    }
}
