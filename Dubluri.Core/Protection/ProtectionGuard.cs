using Dubluri.Core.Platform;

namespace Dubluri.Core.Protection;

/// <summary>
/// Singurul loc care decide daca o cale e protejata. Regula: la orice indoiala raspunsul este
/// "protejat" - mai bine refuzam o stergere decat sa stergem ceva din folderele utilizatorului.
/// </summary>
public sealed class ProtectionGuard
{
    private readonly IPathResolver _resolver;
    private readonly IFileIdentityService _identity;
    private readonly List<string> _folders;
    private List<Entry> _entries = new();

    private sealed record Entry(string Folder, bool Exists, List<string> Forms, FileIdentity? Identity);

    public ProtectionGuard(IPathResolver resolver, IFileIdentityService identity, IEnumerable<string> protectedFolders)
    {
        _resolver = resolver;
        _identity = identity;
        _folders = protectedFolders.ToList();
        Refresh();
    }

    public IReadOnlyList<string> Folders => _folders;

    /// <summary>Folderele protejate care nu sunt accesibile acum (ex. stick scos).</summary>
    public IReadOnlyList<string> MissingFolders => _entries.Where(e => !e.Exists).Select(e => e.Folder).ToList();

    public bool HasMissingFolders => _entries.Any(e => !e.Exists);

    /// <summary>Recalculeaza formele folderelor (o unitate subst sau un junction se pot schimba intre timp).</summary>
    public void Refresh()
    {
        var entries = new List<Entry>(_folders.Count);
        foreach (string folder in _folders)
        {
            var forms = new List<string>();
            bool exists = false;
            FileIdentity? identity = null;
            try
            {
                forms.Add(_resolver.Normalize(folder));
                exists = Directory.Exists(folder);
                if (exists)
                {
                    string? real = _resolver.ResolveReal(folder);
                    if (real == null) exists = false;
                    else if (!forms.Contains(real, _resolver.Comparer)) forms.Add(real);
                    identity = _identity.TryGetIdentity(folder);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                exists = false;
            }
            entries.Add(new Entry(folder, exists, forms, identity));
        }
        _entries = entries;
    }

    /// <summary>
    /// true daca din calea data nu are voie sa se stearga, mute sau redenumeasca nimic.
    /// Se apeleaza si din stratul de stergere, imediat inainte de fiecare operatie.
    /// </summary>
    public bool IsProtected(string path)
    {
        if (_entries.Count == 0) return false;

        string lexical;
        string? real;
        try
        {
            lexical = _resolver.Normalize(path);
            real = _resolver.ResolveReal(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true;
        }
        if (real == null) return true;

        foreach (var entry in _entries)
            foreach (string form in entry.Forms)
                if (_resolver.IsSameOrChild(lexical, form) || _resolver.IsSameOrChild(real, form))
                    return true;

        return IsInsideByIdentity(real);
    }

    /// <summary>
    /// A doua linie de aparare: urca pe parintii caii si compara identitatea fizica a folderelor.
    /// Prinde alias-urile pe care compararea textelor nu le vede (ex. \\localhost\C$\Poze pentru C:\Poze).
    /// </summary>
    private bool IsInsideByIdentity(string realPath)
    {
        var ids = _entries.Where(e => e.Identity.HasValue).Select(e => e.Identity!.Value).ToHashSet();
        if (ids.Count == 0) return false;

        for (string? dir = Path.GetDirectoryName(realPath); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
        {
            var id = _identity.TryGetIdentity(dir);
            if (id.HasValue && ids.Contains(id.Value)) return true;
        }
        return false;
    }
}
