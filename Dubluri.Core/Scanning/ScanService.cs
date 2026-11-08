using Dubluri.Core.Platform;
using Dubluri.Core.Protection;
using Dubluri.Core.Settings;

namespace Dubluri.Core.Scanning;

/// <summary>Scanarea completa pornind de la setari: enumerare, apoi cautarea dublurilor.</summary>
public sealed class ScanService
{
    private readonly IPathResolver _resolver;
    private readonly IFileIdentityService _identity;
    private readonly ISystemFolders _system;

    public ScanService(IPathResolver resolver, IFileIdentityService identity, ISystemFolders system)
    {
        _resolver = resolver;
        _identity = identity;
        _system = system;
    }

    public static FileFilter BuildFilter(AppSettings s)
    {
        var extensions = s.EnabledPresets.Where(FilePresets.All.ContainsKey).SelectMany(p => FilePresets.All[p])
                          .Concat(FileFilter.ParseCustom(s.CustomExtensions));
        return new FileFilter(s.AllFiles, extensions, s.MinSizeBytes);
    }

    public ScanResult Run(AppSettings settings, ProtectionGuard guard, HashCache cache, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var issues = new List<ScanIssue>();

        var roots = settings.SearchLocations.ToList();
        if (settings.IncludeProtectedInScan) roots.AddRange(settings.ProtectedFolders.Where(Directory.Exists));

        var excluded = settings.ExcludedSubfolders.ToList();
        var excludedNames = new List<string>();
        if (settings.ExcludeSystemFolders)
        {
            excluded.AddRange(_system.GetExcludedFolders());
            excludedNames.AddRange(_system.GetExcludedFolderNames());
        }

        var files = new FileScanner(_resolver).Scan(new ScanOptions
        {
            Roots = roots.Distinct(_resolver.Comparer).ToList(),
            ExcludedFolders = excluded,
            ExcludedFolderNames = excludedNames,
            Filter = BuildFilter(settings),
        }, issues, progress, ct);

        var finder = new DuplicateFinder(_identity, cache, settings.MaxParallelism);
        var groups = finder.Find(files, guard.IsProtected, issues, progress, ct);
        return new ScanResult(groups, issues, files.Count);
    }
}
