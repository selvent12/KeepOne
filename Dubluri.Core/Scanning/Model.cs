using System.ComponentModel;

namespace Dubluri.Core.Scanning;

public sealed record ScannedFile(string Path, long Size, DateTime LastWriteUtc, string Location);

/// <summary>Fisier sau folder care nu a putut fi procesat; nu opreste scanarea.</summary>
public sealed record ScanIssue(string Path, string Message, bool IsAccessDenied);

public enum ScanStage { Enumerating, PartialHash, FullHash, Finalizing }

public sealed record ScanProgress(ScanStage Stage, long Done, long Total, string? CurrentPath);

public sealed class DuplicateFile : INotifyPropertyChanged
{
    private bool _isSelected;

    public DuplicateFile(string path, long size, DateTime lastWriteUtc, string location, bool isProtected)
    {
        Path = path; Size = size; LastWriteUtc = lastWriteUtc; Location = location; IsProtected = isProtected;
    }

    public string Path { get; }
    public long Size { get; }
    public DateTime LastWriteUtc { get; }
    public string Location { get; }
    public bool IsProtected { get; }
    public DuplicateGroup Group { get; internal set; } = null!;

    /// <summary>
    /// Marcat pentru stergere. Regulile se aplica prin SelectionManager, iar DeletionService
    /// le reverifica oricum, deci setarea directa nu poate produce o stergere interzisa.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class DuplicateGroup
{
    public DuplicateGroup(string hash, long fileSize, IEnumerable<DuplicateFile> files)
    {
        Hash = hash; FileSize = fileSize; Files = files.ToList();
        foreach (var f in Files) f.Group = this;
    }

    public string Hash { get; }
    public long FileSize { get; }
    public List<DuplicateFile> Files { get; }

    /// <summary>Spatiul care se poate elibera: ramane o copie, iar cele protejate nu se pot elimina.</summary>
    public long RecoverableBytes => FileSize * Math.Min(Files.Count - 1, Files.Count(f => !f.IsProtected));
    public bool HasProtectedCopy => Files.Any(f => f.IsProtected);
}

public sealed record ScanResult(List<DuplicateGroup> Groups, List<ScanIssue> Issues, long FilesScanned);
