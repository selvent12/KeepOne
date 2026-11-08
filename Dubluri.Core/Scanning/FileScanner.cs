using Dubluri.Core.Platform;

namespace Dubluri.Core.Scanning;

public sealed class ScanOptions
{
    public required IReadOnlyList<string> Roots { get; init; }
    public IReadOnlyList<string> ExcludedFolders { get; init; } = Array.Empty<string>();
    public IReadOnlyCollection<string> ExcludedFolderNames { get; init; } = Array.Empty<string>();
    public required FileFilter Filter { get; init; }
}

/// <summary>Enumereaza fisierele. Fiecare folder real se parcurge o singura data, chiar daca locatiile se suprapun.</summary>
public sealed class FileScanner
{
    private const FileAttributes CloudOnly = FileAttributes.Offline
        | (FileAttributes)0x00040000  // RECALL_ON_OPEN
        | (FileAttributes)0x00400000; // RECALL_ON_DATA_ACCESS

    private readonly IPathResolver _resolver;

    public FileScanner(IPathResolver resolver) => _resolver = resolver;

    public List<ScannedFile> Scan(ScanOptions options, List<ScanIssue> issues, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var files = new List<ScannedFile>();
        var visited = new HashSet<string>(_resolver.Comparer);
        var excludedNames = new HashSet<string>(options.ExcludedFolderNames, StringComparer.OrdinalIgnoreCase);
        var excluded = options.ExcludedFolders.Select(Canonical).ToList();

        var enumeration = new EnumerationOptions
        {
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            MatchType = MatchType.Simple,
        };

        foreach (string root in options.Roots)
        {
            string start = Canonical(root);
            var stack = new Stack<string>();
            stack.Push(start);

            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                string dir = stack.Pop();
                if (!visited.Add(dir)) continue;
                if (excluded.Any(e => _resolver.IsSameOrChild(dir, e))) continue;

                progress?.Report(new ScanProgress(ScanStage.Enumerating, files.Count, 0, dir));
                try
                {
                    foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", enumeration))
                    {
                        ct.ThrowIfCancellationRequested();
                        var attributes = entry.Attributes;

                        // junction / symlink: nu le urmam, ar duce la acelasi fisier pe doua cai
                        if ((attributes & FileAttributes.ReparsePoint) != 0 && entry.LinkTarget != null) continue;

                        if (entry is DirectoryInfo)
                        {
                            if (!excludedNames.Contains(entry.Name)) stack.Push(entry.FullName);
                            continue;
                        }

                        if (entry is not FileInfo file) continue;
                        if ((attributes & FileAttributes.System) != 0) continue;
                        if ((attributes & CloudOnly) != 0)
                        {
                            issues.Add(new ScanIssue(file.FullName, "Sărit: fișierul există doar în cloud.", false));
                            continue;
                        }
                        if (options.Filter.Matches(file.Name, file.Length))
                            files.Add(new ScannedFile(file.FullName, file.Length, file.LastWriteTimeUtc, start));
                    }
                }
                catch (UnauthorizedAccessException ex) { issues.Add(new ScanIssue(dir, ex.Message, true)); }
                catch (Exception ex) when (ex is IOException or System.Security.SecurityException)
                {
                    issues.Add(new ScanIssue(dir, ex.Message, false));
                }
            }
        }
        return files;
    }

    private string Canonical(string path)
    {
        try { return _resolver.ResolveReal(path) ?? _resolver.Normalize(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return path; }
    }
}
