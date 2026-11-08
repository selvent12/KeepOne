using Dubluri.Core.Platform;
using Dubluri.Core.Scanning;

namespace Dubluri.Core.Selection;

public enum KeepRule { PriorityLocation, Oldest, Newest, ShortestPath }

/// <summary>Rezultatul unei selectii pe folder.</summary>
public sealed record FolderSelectionResult(int Changed, int GroupsBlockedLastCopy, int ProtectedSkipped, int GroupsKeptOne = 0);

/// <summary>Aplica regulile de selectie. Regula de baza: in fiecare grup ramane cel putin o copie neselectata.</summary>
public sealed class SelectionManager
{
    public const string LastCopyMessage = "Nu poți selecta toate copiile: în fiecare grup trebuie să rămână cel puțin o copie.";
    public const string ProtectedMessage = "Fișierul este într-un folder protejat și nu poate fi selectat.";

    private readonly IReadOnlyList<DuplicateGroup> _groups;
    private readonly IPathResolver _resolver;

    public SelectionManager(IReadOnlyList<DuplicateGroup> groups, IPathResolver resolver)
    {
        _groups = groups;
        _resolver = resolver;
    }

    /// <summary>Selectie individuala. false = refuzata, cu motivul in reason.</summary>
    public bool TrySetSelected(DuplicateFile file, bool selected, out string? reason)
    {
        reason = null;
        if (!selected)
        {
            file.IsSelected = false;
            return true;
        }
        if (file.IsProtected)
        {
            reason = ProtectedMessage;
            return false;
        }
        if (file.Group.Files.All(f => f == file || f.IsSelected))
        {
            reason = LastCopyMessage;
            return false;
        }
        file.IsSelected = true;
        return true;
    }

    /// <summary>
    /// Selecteaza dublurile din folder (si subfoldere). Un grup in care selectia ar cuprinde toate
    /// copiile ramane neschimbat si e numarat in GroupsBlockedLastCopy. Cu keepOneRule, daca toate
    /// copiile grupului sunt in folder, se pastreaza una dupa regula si restul se selecteaza.
    /// </summary>
    public FolderSelectionResult SelectFolder(string folder, KeepRule? keepOneRule = null, IReadOnlyList<string>? priorityLocations = null)
    {
        int changed = 0, blocked = 0, protectedSkipped = 0, keptOne = 0;
        foreach (var group in _groups)
        {
            var inFolder = group.Files.Where(f => _resolver.IsSameOrChild(f.Path, folder)).ToList();
            if (inFolder.Count == 0) continue;

            protectedSkipped += inFolder.Count(f => f.IsProtected);
            var toSelect = inFolder.Where(f => !f.IsProtected && !f.IsSelected).ToList();
            if (toSelect.Count == 0) continue;

            if (group.Files.All(f => f.IsSelected || toSelect.Contains(f)))
            {
                // o copie din afara folderului, deja bifata, ramane decizia utilizatorului: grupul e blocat
                if (keepOneRule is not KeepRule rule || inFolder.Count != group.Files.Count)
                {
                    blocked++;
                    continue;
                }
                var keeper = ChooseKeeper(group, rule, priorityLocations ?? Array.Empty<string>());
                keeper.IsSelected = false;
                toSelect.Remove(keeper);
                keptOne++;
            }
            foreach (var file in toSelect) file.IsSelected = true;
            changed += toSelect.Count;
        }
        return new FolderSelectionResult(changed, blocked, protectedSkipped, keptOne);
    }

    public int DeselectFolder(string folder)
    {
        int changed = 0;
        foreach (var file in _groups.SelectMany(g => g.Files).Where(f => f.IsSelected && _resolver.IsSameOrChild(f.Path, folder)))
        {
            file.IsSelected = false;
            changed++;
        }
        return changed;
    }

    public void ClearAll()
    {
        foreach (var file in _groups.SelectMany(g => g.Files)) file.IsSelected = false;
    }

    /// <summary>
    /// Auto-selectie: in fiecare grup se pastreaza o copie dupa regula, restul se selecteaza.
    /// Daca grupul are copii protejate, acestea sunt cele pastrate.
    /// </summary>
    public void ApplyRule(KeepRule rule, IReadOnlyList<string> priorityLocations)
    {
        foreach (var group in _groups)
        {
            var keeper = group.HasProtectedCopy ? null : ChooseKeeper(group, rule, priorityLocations);
            foreach (var file in group.Files)
                file.IsSelected = !file.IsProtected && file != keeper;
        }
    }

    private DuplicateFile ChooseKeeper(DuplicateGroup group, KeepRule rule, IReadOnlyList<string> priority)
    {
        IEnumerable<DuplicateFile> ordered = rule switch
        {
            KeepRule.Oldest => group.Files.OrderBy(f => f.LastWriteUtc),
            KeepRule.Newest => group.Files.OrderByDescending(f => f.LastWriteUtc),
            KeepRule.ShortestPath => group.Files.OrderBy(f => f.Path.Length),
            _ => group.Files.OrderBy(f => PriorityIndex(f, priority)).ThenBy(f => f.Path.Length),
        };
        return ordered.First();
    }

    private int PriorityIndex(DuplicateFile file, IReadOnlyList<string> priority)
    {
        for (int i = 0; i < priority.Count; i++)
            if (_resolver.IsSameOrChild(file.Path, priority[i])) return i;
        return int.MaxValue;
    }
}
