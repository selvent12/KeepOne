namespace Dubluri.Core.Platform;

/// <summary>Identitatea fizica a unui fisier (Windows: volum + File ID NTFS; Linux: device + inode).</summary>
public readonly record struct FileIdentity(ulong VolumeId, ulong IdHigh, ulong IdLow);

public interface IFileIdentityService
{
    /// <summary>Identitatea fisierului sau folderului; null daca nu se poate afla (ex. unele unitati de retea).</summary>
    FileIdentity? TryGetIdentity(string path);
}

public interface IPathResolver
{
    /// <summary>Compararea cailor: case-insensitive pe Windows, case-sensitive pe Linux.</summary>
    StringComparer Comparer { get; }

    /// <summary>Forma lexicala canonica: cale completa, fara ".", "..", prefix \\?\ sau separator final.</summary>
    string Normalize(string path);

    /// <summary>
    /// Calea reala, cu junction/symlink/reparse points, unitati subst, unitati de retea si nume scurte rezolvate.
    /// Pentru o cale inexistenta rezolva cel mai apropiat parinte existent.
    /// null = nu se poate determina (apelantul trebuie sa trateze cazul in defavoarea stergerii).
    /// </summary>
    string? ResolveReal(string path);

    /// <summary>Calea e chiar folderul sau se afla in el. Atentie la falsul prefix: D:\Poze2 NU e in D:\Poze.</summary>
    bool IsSameOrChild(string path, string folder);
}

public interface ITrashService
{
    /// <summary>Se poate trimite fisierul in cos? Daca nu, reason explica de ce.</summary>
    bool CanTrash(string path, out string? reason);

    /// <summary>Muta fisierul in cos. Arunca exceptie daca nu reuseste; nu il sterge niciodata definitiv.</summary>
    void MoveToTrash(string path);
}

public interface IElevationService
{
    bool IsElevated { get; }

    /// <summary>Reporneste aplicatia cu drepturi ridicate, cu acelasi folder de setari. false = anulat de utilizator.</summary>
    bool RestartElevated(string settingsDirectory);
}

public interface ISystemFolders
{
    /// <summary>Foldere de sistem excluse implicit (cai absolute).</summary>
    IReadOnlyList<string> GetExcludedFolders();

    /// <summary>Nume de foldere excluse oriunde apar (ex. $Recycle.Bin).</summary>
    IReadOnlyList<string> GetExcludedFolderNames();
}

public interface IShellService
{
    /// <summary>Deschide managerul de fisiere cu fisierul selectat.</summary>
    void RevealInFileManager(string path);
}
