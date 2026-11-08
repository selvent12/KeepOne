using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using Dubluri.Core.Platform;

namespace Dubluri.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsPathResolver : IPathResolver
{
    public StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    public string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Cale goală.", nameof(path));

        string p = path;
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) p = @"\\" + p[8..];
        else if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal)) p = p[4..];

        p = Path.GetFullPath(p);
        string? root = Path.GetPathRoot(p);
        return p.Length > (root?.Length ?? 0) ? p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : p;
    }

    public string? ResolveReal(string path)
    {
        string current = Normalize(path);
        string suffix = "";
        while (true)
        {
            using var handle = NativeFile.Open(current, out int error);
            if (!handle.IsInvalid)
            {
                string? final = NativeFile.GetFinalPath(handle);
                return final == null ? null : Normalize(suffix.Length > 0 ? Path.Combine(Normalize(final), suffix) : final);
            }
            bool notFound = error is NativeFile.ERROR_FILE_NOT_FOUND or NativeFile.ERROR_PATH_NOT_FOUND;
            if (!notFound) return null; // acces refuzat, retea indisponibila etc.: nu putem sti unde duce

            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent)) return null;
            suffix = suffix.Length > 0 ? Path.Combine(Path.GetFileName(current), suffix) : Path.GetFileName(current);
            current = parent;
        }
    }

    public bool IsSameOrChild(string path, string folder)
    {
        string p = Normalize(path), f = Normalize(folder);
        if (p.Equals(f, StringComparison.OrdinalIgnoreCase)) return true;
        string prefix = Path.EndsInDirectorySeparator(f) ? f : f + Path.DirectorySeparatorChar;
        return p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}

[SupportedOSPlatform("windows")]
public sealed class NtfsFileIdentityService : IFileIdentityService
{
    private readonly WindowsPathResolver _resolver = new();

    public FileIdentity? TryGetIdentity(string path)
    {
        string full;
        try { full = _resolver.Normalize(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }

        using var handle = NativeFile.Open(full, out _);
        if (handle.IsInvalid) return null;
        var id = NativeFile.GetFileId(handle);
        return id is null ? null : new FileIdentity(id.Value.Volume, id.Value.High, id.Value.Low);
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsElevationService : IElevationService
{
    public bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public bool RestartElevated(string settingsDirectory)
    {
        try
        {
            // folderul de setari se transmite explicit: elevarea cu alt cont ar avea alt %AppData%,
            // deci alta lista de foldere protejate (eventual goala)
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"--settings-dir \"{settingsDirectory}\"",
            });
            return true;
        }
        catch (Win32Exception) // utilizatorul a refuzat UAC
        {
            return false;
        }
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsSystemFolders : ISystemFolders
{
    public IReadOnlyList<string> GetExcludedFolders() =>
        new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        }.Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public IReadOnlyList<string> GetExcludedFolderNames() => new[] { "$Recycle.Bin", "System Volume Information" };
}

[SupportedOSPlatform("windows")]
public sealed class WindowsShellService : IShellService
{
    public void RevealInFileManager(string path)
    {
        var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        info.Arguments = $"/select,\"{path}\"";
        Process.Start(info);
    }
}
