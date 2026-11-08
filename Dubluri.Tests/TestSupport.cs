using System.Diagnostics;
using Dubluri.Core.Platform;
using Dubluri.Core.Scanning;
using Xunit;

// testele folosesc unitati subst si fisiere temporare: ruleaza pe rand
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Dubluri.Tests;

/// <summary>Folder temporar real, sters la final.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Root = Path.Combine(Path.GetTempPath(), "dubluri-teste", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Dir(params string[] parts)
    {
        string path = Path.Combine(new[] { Root }.Concat(parts).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    public string File(string relative, string content)
    {
        string path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        // rmdir stie sa scoata junction-urile fara sa intre in ele
        Cmd.Run($"rmdir /s /q \"{Root}\"");
    }
}

public static class Cmd
{
    /// <summary>Ruleaza o comanda cmd.exe (mklink, subst); intoarce codul de iesire.</summary>
    public static int Run(string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c " + arguments)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        p.WaitForExit();
        return p.ExitCode;
    }

    public static void Junction(string link, string target) =>
        Assert.Equal(0, Run($"mklink /J \"{link}\" \"{target}\""));

    public static void HardLink(string link, string target) =>
        Assert.Equal(0, Run($"mklink /H \"{link}\" \"{target}\""));

    /// <summary>Prima litera libera pentru subst.</summary>
    public static char FreeDriveLetter()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        for (char c = 'Z'; c >= 'G'; c--)
            if (!used.Contains(c)) return c;
        throw new InvalidOperationException("Nicio literă liberă.");
    }
}

/// <summary>Rezolvator simulat, pentru cazuri greu de creat real (retea, alias-uri, esecuri).</summary>
public sealed class FakeResolver : IPathResolver
{
    public Dictionary<string, string?> Aliases { get; } = new(StringComparer.OrdinalIgnoreCase);

    public StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    public string Normalize(string path)
    {
        string p = path.Replace('/', '\\');
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) p = @"\\" + p[8..];
        else if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) p = p[4..];
        return p.TrimEnd('\\');
    }

    public string? ResolveReal(string path)
    {
        string n = Normalize(path);
        foreach (var (alias, target) in Aliases)
        {
            if (n.Equals(alias, StringComparison.OrdinalIgnoreCase)) return target;
            if (n.StartsWith(alias + "\\", StringComparison.OrdinalIgnoreCase))
                return target == null ? null : target + n[alias.Length..];
        }
        return n;
    }

    public bool IsSameOrChild(string path, string folder)
    {
        string p = Normalize(path), f = Normalize(folder);
        return p.Equals(f, StringComparison.OrdinalIgnoreCase) || p.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class NoIdentity : IFileIdentityService
{
    public FileIdentity? TryGetIdentity(string path) => null;
}

/// <summary>Cos simulat: tine evidenta si muta fisierul intr-un folder, ca sa putem verifica efectul.</summary>
public sealed class FakeTrash : ITrashService
{
    public List<string> Trashed { get; } = new();
    public string? Refuse { get; set; }

    public bool CanTrash(string path, out string? reason)
    {
        reason = Refuse;
        return Refuse == null;
    }

    public void MoveToTrash(string path)
    {
        Trashed.Add(path);
        File.Delete(path);
    }
}

public static class Groups
{
    public static DuplicateGroup Of(params DuplicateFile[] files) => new("HASH", files[0].Size, files);

    public static DuplicateFile OnDisk(string path, bool isProtected = false, bool selected = false)
    {
        var info = new FileInfo(path);
        return new DuplicateFile(path, info.Length, info.LastWriteTimeUtc, Path.GetDirectoryName(path)!, isProtected) { IsSelected = selected };
    }

    public static DuplicateFile Virtual(string path, DateTime? date = null, bool isProtected = false) =>
        new(path, 100, date ?? new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), @"D:\", isProtected);
}
