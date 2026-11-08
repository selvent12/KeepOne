using System.Text.Json;
using Dubluri.Core.Deletion;

namespace Dubluri.Core.Settings;

public sealed class AppSettings
{
    public List<string> ProtectedFolders { get; set; } = new();
    public List<string> SearchLocations { get; set; } = new();
    public List<string> ExcludedSubfolders { get; set; } = new();
    public bool ExcludeSystemFolders { get; set; } = true;
    public bool IncludeProtectedInScan { get; set; } = true;
    public bool HideProtectedInResults { get; set; }
    public bool KeepOneOnFolderSelect { get; set; } = true;

    public List<string> EnabledPresets { get; set; } = new() { "Imagini" };
    public bool AllFiles { get; set; }
    public string CustomExtensions { get; set; } = "";
    public long MinSizeBytes { get; set; } = 1;

    public int MaxParallelism { get; set; } = 2;
    public DeleteMode DeleteMode { get; set; } = DeleteMode.RecycleBin;
    public string QuarantineFolder { get; set; } = "";
}

/// <summary>Setarile, cache-ul si jurnalele stau intr-un singur folder din profilul utilizatorului.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public SettingsStore(string? directory = null)
    {
        Directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dubluri");
    }

    public string Directory { get; }
    public string SettingsFile => Path.Combine(Directory, "settings.json");
    public string CacheFile => Path.Combine(Directory, "hashcache.bin");
    public string LogDirectory => Path.Combine(Directory, "jurnale");

    /// <summary>
    /// Mesaj daca fisierul de setari exista dar nu poate fi citit. Lista folderelor protejate
    /// e necunoscuta in acest caz, deci aplicatia nu trebuie sa permita stergeri pana la clarificare.
    /// </summary>
    public string? LoadError { get; private set; }

    public void AcknowledgeLoadError() => LoadError = null;

    public AppSettings Load()
    {
        LoadError = null;
        if (!File.Exists(SettingsFile)) return new AppSettings();
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile), Options) ?? throw new JsonException("fișier gol");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            LoadError = $"Fișierul de setări nu poate fi citit ({ex.Message}). Lista folderelor protejate este necunoscută.";
            try { File.Copy(SettingsFile, SettingsFile + ".stricat", overwrite: true); } catch (Exception) { /* nu conteaza */ }
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        System.IO.Directory.CreateDirectory(Directory);
        string tmp = SettingsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
        File.Move(tmp, SettingsFile, overwrite: true);
    }
}
