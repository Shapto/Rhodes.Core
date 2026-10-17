using Rhodes.Core.Tools;

namespace Rhodes.Core.Workspace;

/// <summary>
/// Closure's sandbox: one folder (Data\workspace) where she can create, change and build things.
/// Every path her tools get is resolved inside it; anything that would land outside (.., absolute paths,
/// links pointing elsewhere) is refused before the tool does anything.
/// </summary>
public sealed class WorkspaceFolder
{
    private static readonly HashSet<string> SkippedFolderNames = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git", ".vs", "node_modules", "packages" };

    public WorkspaceFolder(string rootPath)
    {
        Directory.CreateDirectory(rootPath);
        RootPath = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public string RootPath { get; }

    public static bool IsSkippedFolder(string folderName) => SkippedFolderNames.Contains(folderName);

    /// <summary>
    /// The full path for a workspace-relative path, or a ToolArgumentException saying why it isn't allowed.
    /// </summary>
    public string Resolve(string? relativePath)
    {
        var cleanedPath = (relativePath ?? "").Trim().Replace('\\', '/').TrimStart('/');
        if (cleanedPath.StartsWith("workspace/", StringComparison.OrdinalIgnoreCase))
        {
            cleanedPath = cleanedPath["workspace/".Length..];
        }

        if (Path.IsPathRooted(cleanedPath) || cleanedPath.Contains(':'))
        {
            throw new ToolArgumentException("Use a path inside the workspace, like \"Gungnir/Source/Gungnir.cs\", not a full path.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(RootPath, cleanedPath.Length == 0 ? "." : cleanedPath));
        var isInside = fullPath.Equals(RootPath, StringComparison.OrdinalIgnoreCase) || fullPath.StartsWith(RootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (!isInside)
        {
            throw new ToolArgumentException("That path leads outside the workspace. Everything you work on has to stay inside it.");
        }

        // A link (symlink or junction) inside the workspace could point anywhere, so none are followed.
        var currentPath = fullPath;
        while (currentPath.Length > RootPath.Length)
        {
            var info = Directory.Exists(currentPath) ? (FileSystemInfo)new DirectoryInfo(currentPath) : new FileInfo(currentPath);
            if (info.Exists && info.LinkTarget is not null)
            {
                throw new ToolArgumentException("That path goes through a link, which the workspace doesn't follow.");
            }

            currentPath = Path.GetDirectoryName(currentPath)!;
        }

        return fullPath;
    }

    /// <summary>
    /// "Gungnir/Source/A.cs" for a full path inside the workspace, with forward slashes.
    /// </summary>
    public string ToRelative(string fullPath)
    {
        var relativePath = Path.GetRelativePath(RootPath, fullPath).Replace('\\', '/');
        return relativePath == "." ? "" : relativePath;
    }

    /// <summary>
    /// The project folder a path belongs to (its first folder), which is what "always allow" remembers for writes.
    /// A file directly in the workspace root belongs to the root itself.
    /// </summary>
    public string ProjectKey(string fullPath)
    {
        var relativePath = ToRelative(fullPath);
        var slashIndex = relativePath.IndexOf('/');
        return "workspace/" + (slashIndex < 0 ? (Directory.Exists(fullPath) ? relativePath : "") : relativePath[..slashIndex]);
    }
}