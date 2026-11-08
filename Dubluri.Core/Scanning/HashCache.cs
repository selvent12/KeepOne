using System.Collections.Concurrent;

namespace Dubluri.Core.Scanning;

/// <summary>Cache de hash-uri, cu cheia cale + marime + data modificarii.</summary>
public sealed class HashCache
{
    private const int FormatVersion = 1;

    private readonly record struct Key(string Path, long Size, long Ticks);
    private sealed class Entry { public string? Partial; public string? Full; }

    private readonly ConcurrentDictionary<Key, Entry> _entries = new();
    private readonly string? _file;

    public HashCache(string? file = null)
    {
        _file = file;
        Load();
    }

    public int Count => _entries.Count;

    public string? GetPartial(ScannedFile f) => _entries.TryGetValue(KeyOf(f), out var e) ? e.Partial : null;
    public string? GetFull(ScannedFile f) => _entries.TryGetValue(KeyOf(f), out var e) ? e.Full : null;
    public void SetPartial(ScannedFile f, string hash) => _entries.GetOrAdd(KeyOf(f), _ => new Entry()).Partial = hash;
    public void SetFull(ScannedFile f, string hash) => _entries.GetOrAdd(KeyOf(f), _ => new Entry()).Full = hash;

    public void Clear()
    {
        _entries.Clear();
        if (_file != null && File.Exists(_file)) File.Delete(_file);
    }

    public void Save()
    {
        if (_file == null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        string tmp = _file + ".tmp";
        using (var writer = new BinaryWriter(File.Create(tmp)))
        {
            writer.Write(FormatVersion);
            writer.Write(_entries.Count);
            foreach (var (key, entry) in _entries)
            {
                writer.Write(key.Path);
                writer.Write(key.Size);
                writer.Write(key.Ticks);
                writer.Write(entry.Partial ?? "");
                writer.Write(entry.Full ?? "");
            }
        }
        File.Move(tmp, _file, overwrite: true);
    }

    private void Load()
    {
        if (_file == null || !File.Exists(_file)) return;
        try
        {
            using var reader = new BinaryReader(File.OpenRead(_file));
            if (reader.ReadInt32() != FormatVersion) return;
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                var key = new Key(reader.ReadString(), reader.ReadInt64(), reader.ReadInt64());
                string partial = reader.ReadString(), full = reader.ReadString();
                _entries[key] = new Entry { Partial = partial.Length > 0 ? partial : null, Full = full.Length > 0 ? full : null };
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or UnauthorizedAccessException or FormatException)
        {
            _entries.Clear(); // cache stricat: se reconstruieste la scanare
        }
    }

    private static Key KeyOf(ScannedFile f) => new(f.Path, f.Size, f.LastWriteUtc.Ticks);
}
