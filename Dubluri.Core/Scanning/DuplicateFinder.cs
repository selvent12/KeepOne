using System.Collections.Concurrent;
using System.Security.Cryptography;
using Dubluri.Core.Platform;

namespace Dubluri.Core.Scanning;

/// <summary>Gaseste dublurile exclusiv dupa continut: marime, apoi hash partial, apoi SHA-256 complet.</summary>
public sealed class DuplicateFinder
{
    public const int PartialBlock = 64 * 1024;
    private const int BufferSize = 1024 * 1024;

    private readonly IFileIdentityService _identity;
    private readonly HashCache _cache;
    private readonly int _parallelism;

    public DuplicateFinder(IFileIdentityService identity, HashCache cache, int parallelism)
    {
        _identity = identity;
        _cache = cache;
        _parallelism = Math.Max(1, parallelism);
    }

    public List<DuplicateGroup> Find(IReadOnlyList<ScannedFile> files, Func<string, bool> isProtected, List<ScanIssue> issues,
                                     IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var bag = new ConcurrentBag<ScanIssue>();

        // 1. marime: fisierele cu marime unica nu pot avea dubluri
        var bySize = files.GroupBy(f => f.Size).Where(g => g.Count() > 1).Select(g => g.ToList()).ToList();

        // hardlink-urile sunt acelasi fisier fizic: ramane un singur reprezentant (preferabil cel protejat)
        var candidates = bySize.Select(g => RemoveHardLinks(g, isProtected, ct)).Where(g => g.Count > 1).ToList();

        // 2. hash partial (primii + ultimii 64 KB)
        var afterPartial = RunStage(ScanStage.PartialHash, candidates, ReadPartial, bag, progress, ct);

        // 3. SHA-256 complet doar pentru candidatii ramasi
        var finalGroups = RunStage(ScanStage.FullHash, afterPartial.Select(g => g.Files).ToList(), ReadFull, bag, progress, ct);

        issues.AddRange(bag);
        progress?.Report(new ScanProgress(ScanStage.Finalizing, 0, finalGroups.Count, null));

        var result = new List<DuplicateGroup>(finalGroups.Count);
        foreach (var group in finalGroups)
        {
            ct.ThrowIfCancellationRequested();
            result.Add(new DuplicateGroup(group.Hash, group.Files[0].Size,
                group.Files.Select(f => new DuplicateFile(f.Path, f.Size, f.LastWriteUtc, f.Location, isProtected(f.Path)))));
        }
        return result.OrderByDescending(g => g.RecoverableBytes).ToList();
    }

    private List<ScannedFile> RemoveHardLinks(List<ScannedFile> sameSize, Func<string, bool> isProtected, CancellationToken ct)
    {
        var result = new List<ScannedFile>(sameSize.Count);
        var seen = new Dictionary<FileIdentity, int>();
        foreach (var file in sameSize)
        {
            ct.ThrowIfCancellationRequested();
            var id = _identity.TryGetIdentity(file.Path);
            if (id is null) { result.Add(file); continue; }

            if (seen.TryGetValue(id.Value, out int index))
            {
                if (!isProtected(result[index].Path) && isProtected(file.Path)) result[index] = file;
                continue;
            }
            seen[id.Value] = result.Count;
            result.Add(file);
        }
        return result;
    }

    private sealed record HashedGroup(string Hash, List<ScannedFile> Files);

    private List<HashedGroup> RunStage(ScanStage stage, List<List<ScannedFile>> groups, Func<ScannedFile, CancellationToken, string> hash,
                                       ConcurrentBag<ScanIssue> issues, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        long total = groups.Sum(g => (long)g.Count), done = 0;
        var hashed = new ConcurrentDictionary<(int Group, string Hash), ConcurrentBag<ScannedFile>>();
        var work = groups.SelectMany((g, i) => g.Select(f => (Group: i, File: f)));

        Parallel.ForEach(work, new ParallelOptions { MaxDegreeOfParallelism = _parallelism, CancellationToken = ct }, item =>
        {
            progress?.Report(new ScanProgress(stage, Interlocked.Increment(ref done), total, item.File.Path));
            try
            {
                hashed.GetOrAdd((item.Group, hash(item.File, ct)), _ => new ConcurrentBag<ScannedFile>()).Add(item.File);
            }
            catch (UnauthorizedAccessException ex) { issues.Add(new ScanIssue(item.File.Path, ex.Message, true)); }
            catch (IOException ex) { issues.Add(new ScanIssue(item.File.Path, ex.Message, false)); }
        });

        return hashed.Where(p => p.Value.Count > 1)
                     .Select(p => new HashedGroup(p.Key.Hash, p.Value.OrderBy(f => f.Path, StringComparer.Ordinal).ToList()))
                     .ToList();
    }

    private string ReadPartial(ScannedFile file, CancellationToken ct)
    {
        string? cached = _cache.GetPartial(file);
        if (cached != null) return cached;

        // fisier mic: blocurile acopera tot continutul, deci hash-ul partial e chiar cel complet
        if (file.Size <= 2 * PartialBlock)
        {
            string whole = ReadFull(file, ct);
            _cache.SetPartial(file, whole);
            return whole;
        }

        using var stream = Open(file.Path);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[PartialBlock];
        sha.AppendData(buffer, 0, stream.ReadAtLeast(buffer, PartialBlock, throwOnEndOfStream: false));
        stream.Seek(-PartialBlock, SeekOrigin.End);
        sha.AppendData(buffer, 0, stream.ReadAtLeast(buffer, PartialBlock, throwOnEndOfStream: false));
        string hash = Convert.ToHexString(sha.GetHashAndReset());
        _cache.SetPartial(file, hash);
        return hash;
    }

    private string ReadFull(ScannedFile file, CancellationToken ct)
    {
        string? cached = _cache.GetFull(file);
        if (cached != null) return cached;

        using var stream = Open(file.Path);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            sha.AppendData(buffer, 0, read);
        }
        string hash = Convert.ToHexString(sha.GetHashAndReset());
        _cache.SetFull(file, hash);
        return hash;
    }

    private static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.SequentialScan);
}
