using System.Buffers;
using Dubluri.Core.Platform;
using Dubluri.Core.Protection;
using Dubluri.Core.Scanning;

namespace Dubluri.Core.Deletion;

public enum DeleteMode { RecycleBin, Quarantine, Permanent }

public enum OperationResult { Done, Simulated, Refused, Failed }

public sealed record DeletionRequest(IReadOnlyList<DuplicateGroup> Groups, DeleteMode Mode, string? QuarantineFolder, bool DryRun);

public sealed record FileOperation(DuplicateFile File, OperationResult Result, string Message);

public sealed record DeletionProgress(int Done, int Total, string CurrentPath);

public sealed class DeletionReport
{
    public List<FileOperation> Operations { get; } = new();
    public int Count(OperationResult result) => Operations.Count(o => o.Result == result);
    public long FreedBytes => Operations.Where(o => o.Result == OperationResult.Done).Sum(o => o.File.Size);
}

/// <summary>
/// Singurul punct prin care se sterge ceva. Nu se bazeaza pe verificarile din interfata:
/// imediat inainte de fiecare fisier reverifica protectia, regula ultimei copii si continutul.
/// </summary>
public sealed class DeletionService
{
    // bucati mari: pe HDD, alternarea intre doua fisiere muta capul discului la fiecare bucata
    private const int CompareChunk = 32 * 1024 * 1024;

    private readonly ProtectionGuard _guard;
    private readonly ITrashService _trash;
    private readonly IFileIdentityService _identity;
    private readonly IPathResolver _resolver;
    private readonly OperationLog _log;

    public DeletionService(ProtectionGuard guard, ITrashService trash, IFileIdentityService identity, IPathResolver resolver, OperationLog log)
    {
        _guard = guard;
        _trash = trash;
        _identity = identity;
        _resolver = resolver;
        _log = log;
    }

    public DeletionReport Execute(DeletionRequest request, IProgress<DeletionProgress>? progress, CancellationToken ct)
    {
        var report = new DeletionReport();
        int total = request.Groups.Sum(g => g.Files.Count(f => f.IsSelected)), done = 0;
        string action = ActionName(request);

        string? quarantineProblem = CheckQuarantine(request);

        foreach (var group in request.Groups)
        {
            var selected = group.Files.Where(f => f.IsSelected).ToList();
            if (selected.Count == 0) continue;

            var keepers = group.Files.Where(f => !f.IsSelected).OrderByDescending(f => f.IsProtected).ToList();

            foreach (var file in selected)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new DeletionProgress(++done, total, file.Path));

                string? refusal = quarantineProblem ?? CheckAllowed(file, keepers, request, ct);
                if (refusal != null)
                {
                    Record(report, file, action, OperationResult.Refused, refusal);
                    continue;
                }
                if (request.DryRun)
                {
                    Record(report, file, action, OperationResult.Simulated, "Ar fi fost procesat; nu s-a modificat nimic.");
                    continue;
                }
                try
                {
                    Perform(file, request);
                    Record(report, file, action, OperationResult.Done, "OK");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                               or System.Runtime.InteropServices.COMException)
                {
                    Record(report, file, action, OperationResult.Failed, ex.Message);
                }
            }
        }
        return report;
    }

    private static string ActionName(DeletionRequest r)
    {
        string name = r.Mode switch
        {
            DeleteMode.RecycleBin => "Recycle Bin",
            DeleteMode.Quarantine => "Carantină",
            _ => "Ștergere definitivă",
        };
        return r.DryRun ? "Simulare: " + name : name;
    }

    private string? CheckQuarantine(DeletionRequest request)
    {
        if (request.Mode != DeleteMode.Quarantine) return null;
        if (string.IsNullOrWhiteSpace(request.QuarantineFolder)) return "Nu a fost ales un folder de carantină.";
        _guard.Refresh();
        return _guard.IsProtected(request.QuarantineFolder) ? "Folderul de carantină este într-un folder protejat." : null;
    }

    /// <summary>null = operatia e permisa; altfel motivul refuzului.</summary>
    private string? CheckAllowed(DuplicateFile file, List<DuplicateFile> keepers, DeletionRequest request, CancellationToken ct)
    {
        _guard.Refresh();
        if (_guard.HasMissingFolders)
            return "Un folder protejat nu este accesibil: " + string.Join(", ", _guard.MissingFolders);

        if (file.IsProtected || _guard.IsProtected(file.Path))
            return "Fișierul este într-un folder protejat.";

        if (keepers.Count == 0)
            return "Este ultima copie din grup.";

        var info = new FileInfo(file.Path);
        if (!info.Exists) return "Fișierul nu mai există.";
        if (info.Length != file.Size || info.LastWriteTimeUtc != file.LastWriteUtc)
            return "Fișierul s-a modificat după scanare.";

        if (request.Mode == DeleteMode.RecycleBin && !_trash.CanTrash(file.Path, out string? reason))
            return reason ?? "Fișierul nu poate fi trimis în Recycle Bin.";

        return HasVerifiedCopy(file, keepers, ct) ? null : "Nu există o altă copie identică verificată octet cu octet.";
    }

    private bool HasVerifiedCopy(DuplicateFile file, List<DuplicateFile> keepers, CancellationToken ct)
    {
        foreach (var keeper in keepers)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (IsSamePhysicalFile(file.Path, keeper.Path)) continue;
                if (BytesEqual(file.Path, keeper.Path, ct)) return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // copia aceasta nu poate fi citita: incercam urmatoarea
            }
        }
        return false;
    }

    /// <summary>Hardlink sau alias (subst, junction) catre acelasi fisier nu conteaza drept copie separata.</summary>
    private bool IsSamePhysicalFile(string a, string b)
    {
        var idA = _identity.TryGetIdentity(a);
        var idB = _identity.TryGetIdentity(b);
        if (idA.HasValue && idB.HasValue) return idA.Value == idB.Value;

        string? realA = _resolver.ResolveReal(a), realB = _resolver.ResolveReal(b);
        return realA == null || realB == null || _resolver.Comparer.Equals(realA, realB);
    }

    public static bool BytesEqual(string a, string b, CancellationToken ct)
    {
        // bufferSize 1 = fara buffer intern: citirile mari merg direct in tampoanele de mai jos
        using var fa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        using var fb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        if (fa.Length != fb.Length) return false;

        int chunk = (int)Math.Clamp(fa.Length, 1, CompareChunk);
        byte[] ba = ArrayPool<byte>.Shared.Rent(chunk);
        byte[] bb = ArrayPool<byte>.Shared.Rent(chunk);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int ra = fa.ReadAtLeast(ba.AsSpan(0, chunk), chunk, throwOnEndOfStream: false);
                int rb = fb.ReadAtLeast(bb.AsSpan(0, chunk), chunk, throwOnEndOfStream: false);
                if (ra != rb || !ba.AsSpan(0, ra).SequenceEqual(bb.AsSpan(0, rb))) return false;
                if (ra < chunk) return true;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(ba);
            ArrayPool<byte>.Shared.Return(bb);
        }
    }

    private void Perform(DuplicateFile file, DeletionRequest request)
    {
        switch (request.Mode)
        {
            case DeleteMode.RecycleBin:
                _trash.MoveToTrash(file.Path);
                break;
            case DeleteMode.Quarantine:
                string target = QuarantineTarget(request.QuarantineFolder!, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(file.Path, target, overwrite: false);
                break;
            case DeleteMode.Permanent:
                File.Delete(file.Path);
                break;
        }
    }

    /// <summary>D:\Poze\a.jpg -> carantina\D\Poze\a.jpg; structura de foldere se pastreaza pentru restaurare.</summary>
    public static string QuarantineTarget(string quarantine, string path)
    {
        string root = Path.GetPathRoot(path) ?? "";
        string rootName = string.Concat(root.Where(c => c != ':' && c != '?')).Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                .Replace(Path.DirectorySeparatorChar, '_').Replace(Path.AltDirectorySeparatorChar, '_');
        string target = Path.Combine(quarantine, rootName.Length > 0 ? rootName : "radacina", path[root.Length..]);
        if (!File.Exists(target)) return target;

        string dir = Path.GetDirectoryName(target)!, name = Path.GetFileNameWithoutExtension(target), ext = Path.GetExtension(target);
        for (int i = 1; ; i++)
        {
            string candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private void Record(DeletionReport report, DuplicateFile file, string action, OperationResult result, string message)
    {
        report.Operations.Add(new FileOperation(file, result, message));
        string status = result switch
        {
            OperationResult.Done => "Reușit",
            OperationResult.Simulated => "Simulat",
            OperationResult.Refused => "Refuzat: " + message,
            _ => "Eroare: " + message,
        };
        _log.Write(file.Path, file.Group.Hash, action, status);
    }
}
