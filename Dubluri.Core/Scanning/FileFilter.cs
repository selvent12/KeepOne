namespace Dubluri.Core.Scanning;

public static class FilePresets
{
    public static readonly IReadOnlyDictionary<string, string[]> All = new Dictionary<string, string[]>
    {
        ["Imagini"] = new[] { "jpg", "jpeg", "png", "gif", "bmp", "tif", "tiff", "heic", "webp", "raw", "cr2", "cr3", "nef", "arw", "dng" },
        ["Video"] = new[] { "mp4", "mov", "avi", "mkv", "wmv", "m4v", "mts", "3gp" },
        ["Audio"] = new[] { "mp3", "flac", "wav", "m4a", "ogg" },
        ["Documente"] = new[] { "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt" },
    };
}

/// <summary>Filtrul pe extensie (case-insensitive) si marime minima.</summary>
public sealed class FileFilter
{
    private readonly HashSet<string> _extensions;

    public FileFilter(bool allFiles, IEnumerable<string> extensions, long minSize = 1)
    {
        AllFiles = allFiles;
        MinSize = Math.Max(0, minSize);
        _extensions = new HashSet<string>(extensions.Select(Clean).Where(e => e.Length > 0), StringComparer.OrdinalIgnoreCase);
    }

    public bool AllFiles { get; }
    public long MinSize { get; }

    public bool Matches(string fileName, long size)
    {
        if (size < MinSize) return false;
        if (AllFiles) return true;
        return _extensions.Contains(Clean(Path.GetExtension(fileName)));
    }

    /// <summary>Desparte un text de forma "dwg, zip .rar" in extensii.</summary>
    public static IEnumerable<string> ParseCustom(string? text) =>
        (text ?? "").Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).Select(Clean).Where(e => e.Length > 0);

    private static string Clean(string extension) => extension.Trim().TrimStart('.', '*');
}
