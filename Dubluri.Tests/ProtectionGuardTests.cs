using System.Runtime.InteropServices;
using System.Text;
using Dubluri.Core.Platform;
using Dubluri.Core.Protection;
using Dubluri.Platform.Windows;
using Xunit;

namespace Dubluri.Tests;

/// <summary>IsProtected pe sistemul de fisiere real: toate cazurile din sectiunea 1 a cerintelor.</summary>
public sealed class ProtectionGuardRealTests : IDisposable
{
    private readonly TempDir _t = new();
    private readonly WindowsPathResolver _resolver = new();
    private readonly NtfsFileIdentityService _identity = new();

    private ProtectionGuard Guard(params string[] folders) => new(_resolver, _identity, folders);

    public void Dispose() => _t.Dispose();

    [Fact]
    public void Fisier_direct_si_din_subfolder_sunt_protejate()
    {
        string poze = _t.Dir("Poze");
        var guard = Guard(poze);
        Assert.True(guard.IsProtected(_t.File(@"Poze\a.jpg", "x")));
        Assert.True(guard.IsProtected(_t.File(@"Poze\2020\vara\b.jpg", "x")));
        Assert.True(guard.IsProtected(poze));
    }

    [Fact]
    public void Prefix_fals_nu_e_protejat()
    {
        string poze = _t.Dir("Poze");
        Assert.False(Guard(poze).IsProtected(_t.File(@"Poze2\a.jpg", "x")));
        Assert.False(Guard(poze).IsProtected(_t.File(@"Poze vechi\a.jpg", "x")));
    }

    [Fact]
    public void Punct_punct_nu_pacaleste_protectia()
    {
        string poze = _t.Dir("Poze");
        string file = _t.File(@"Poze\a.jpg", "x");
        _t.Dir("Altele");
        var guard = Guard(poze);

        string intrare = Path.Combine(_t.Root, "Altele", "..", "Poze", "a.jpg");
        Assert.True(guard.IsProtected(intrare));
        Assert.True(guard.IsProtected(Path.Combine(_t.Root, "Poze", ".", "a.jpg")));

        string iesire = _t.File(@"Altele\b.jpg", "x");
        Assert.False(guard.IsProtected(Path.Combine(poze, "..", "Altele", "b.jpg")));
        Assert.True(File.Exists(file) && File.Exists(iesire));
    }

    [Fact]
    public void Majuscule_diferite_sunt_protejate()
    {
        string poze = _t.Dir("Poze");
        string file = _t.File(@"Poze\Vara.JPG", "x");
        var guard = Guard(poze.ToUpperInvariant());
        Assert.True(guard.IsProtected(file));
        Assert.True(guard.IsProtected(file.ToLowerInvariant()));
    }

    [Fact]
    public void Prefix_lung_si_separator_final()
    {
        string poze = _t.Dir("Poze");
        string file = _t.File(@"Poze\a.jpg", "x");
        Assert.True(Guard(poze + Path.DirectorySeparatorChar).IsProtected(file));
        Assert.True(Guard(@"\\?\" + poze).IsProtected(file));
        Assert.True(Guard(poze).IsProtected(@"\\?\" + file));
        Assert.True(Guard(poze.Replace('\\', '/')).IsProtected(file.Replace('\\', '/')));
    }

    [Fact]
    public void Cale_peste_260_de_caractere()
    {
        string poze = _t.Dir("Poze");
        string deep = poze;
        while (deep.Length < 300) deep = Path.Combine(deep, new string('d', 40));
        Directory.CreateDirectory(deep);
        string file = Path.Combine(deep, "lung.jpg");
        File.WriteAllText(file, "x");
        Assert.True(file.Length > 260);

        Assert.True(Guard(poze).IsProtected(file));
        Assert.True(Guard(deep).IsProtected(file));
        Assert.False(Guard(_t.Dir("Altele")).IsProtected(file));
    }

    [Fact]
    public void Junction_catre_folder_protejat_e_protejat()
    {
        string poze = _t.Dir("Poze");
        _t.File(@"Poze\a.jpg", "x");
        string link = Path.Combine(_t.Dir("Linkuri"), "PozeLink");
        Cmd.Junction(link, poze);

        var guard = Guard(poze);
        Assert.True(guard.IsProtected(Path.Combine(link, "a.jpg")));
        Assert.True(guard.IsProtected(link));
    }

    [Fact]
    public void Folder_protejat_definit_prin_junction()
    {
        string real = _t.Dir("Real");
        string file = _t.File(@"Real\a.jpg", "x");
        string link = Path.Combine(_t.Dir("Linkuri"), "Protejat");
        Cmd.Junction(link, real);

        Assert.True(Guard(link).IsProtected(file));
    }

    [Fact]
    public void Symlink_catre_folder_protejat_e_protejat()
    {
        string poze = _t.Dir("Poze");
        _t.File(@"Poze\a.jpg", "x");
        string link = Path.Combine(_t.Dir("Linkuri"), "PozeSym");
        try { Directory.CreateSymbolicLink(link, poze); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Crearea de symlink cere Developer Mode sau drepturi de administrator.");
        }

        Assert.True(Guard(poze).IsProtected(Path.Combine(link, "a.jpg")));
    }

    [Fact]
    public void Unitate_subst_catre_folder_protejat_e_protejata()
    {
        string poze = _t.Dir("Poze");
        _t.File(@"Poze\a.jpg", "x");
        char letter = Cmd.FreeDriveLetter();
        Assert.Equal(0, Cmd.Run($"subst {letter}: \"{poze}\""));
        try
        {
            Assert.True(Guard(poze).IsProtected($@"{letter}:\a.jpg"));
            Assert.True(Guard($@"{letter}:\").IsProtected(Path.Combine(poze, "a.jpg")));
        }
        finally
        {
            Cmd.Run($"subst {letter}: /D");
        }
    }

    [Fact]
    public void Nume_scurt_8_3_e_protejat()
    {
        string poze = _t.Dir("Folder Cu Nume Foarte Lung");
        string file = _t.File(@"Folder Cu Nume Foarte Lung\a.jpg", "x");
        var buffer = new StringBuilder(1024);
        GetShortPathNameW(file, buffer, 1024);
        string shortName = buffer.ToString();
        if (shortName.Length == 0 || string.Equals(shortName, file, StringComparison.OrdinalIgnoreCase))
            Assert.Skip("Numele 8.3 sunt dezactivate pe acest volum.");

        Assert.True(Guard(poze).IsProtected(shortName));
    }

    [Fact]
    public void Cale_UNC_reala_catre_folder_protejat()
    {
        string poze = _t.Dir("Poze");
        string file = _t.File(@"Poze\a.jpg", "x");
        string drive = file[..1];
        string unc = $@"\\localhost\{drive}${file[2..]}";
        if (!File.Exists(unc)) Assert.Skip(@"Partajarea administrativă \\localhost\C$ nu e accesibilă fără drepturi de administrator.");

        Assert.True(Guard(poze).IsProtected(unc));
    }

    [Fact]
    public void Folder_protejat_lipsa_e_raportat()
    {
        string lipsa = Path.Combine(_t.Root, "StickScos");
        string poze = _t.Dir("Poze");
        var guard = Guard(poze, lipsa);
        Assert.True(guard.HasMissingFolders);
        Assert.Equal(new[] { lipsa }, guard.MissingFolders);

        Directory.CreateDirectory(lipsa);
        guard.Refresh();
        Assert.False(guard.HasMissingFolders);
    }

    [Fact]
    public void Fara_foldere_protejate_nimic_nu_e_protejat() =>
        Assert.False(Guard().IsProtected(_t.File("a.jpg", "x")));

    [Fact]
    public void Rezolvatorul_gaseste_calea_reala_si_pentru_fisier_inexistent()
    {
        string real = _t.Dir("Real");
        string link = Path.Combine(_t.Dir("L"), "J");
        Cmd.Junction(link, real);
        string? resolved = _resolver.ResolveReal(Path.Combine(link, "nu", "exista.jpg"));
        Assert.True(_resolver.Comparer.Equals(Path.Combine(_resolver.ResolveReal(real)!, "nu", "exista.jpg"), resolved));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetShortPathNameW(string path, StringBuilder shortPath, uint size);
}

/// <summary>Logica guard-ului cu un rezolvator simulat: retea, alias-uri si esecuri.</summary>
public sealed class ProtectionGuardLogicTests
{
    private static ProtectionGuard Guard(FakeResolver r, IFileIdentityService? id = null, params string[] folders) =>
        new(r, id ?? new NoIdentity(), folders);

    [Fact]
    public void UNC_prefix_extins_si_majuscule()
    {
        var guard = Guard(new FakeResolver(), null, @"\\server\share\Poze");
        Assert.True(guard.IsProtected(@"\\SERVER\share\poze\a.jpg"));
        Assert.True(guard.IsProtected(@"\\?\UNC\server\share\Poze\a.jpg"));
        Assert.False(guard.IsProtected(@"\\server\share\Poze2\a.jpg"));
    }

    [Fact]
    public void Unitate_mapata_catre_UNC_protejat()
    {
        var r = new FakeResolver();
        r.Aliases[@"Z:"] = @"\\server\share";
        Assert.True(Guard(r, null, @"\\server\share\Poze").IsProtected(@"Z:\Poze\a.jpg"));
        Assert.False(Guard(r, null, @"\\server\share\Poze").IsProtected(@"Z:\Altele\a.jpg"));
    }

    [Fact]
    public void Cand_calea_nu_se_poate_rezolva_raspunsul_e_protejat()
    {
        var r = new FakeResolver();
        r.Aliases[@"E:\Necunoscut"] = null;
        Assert.True(Guard(r, null, @"D:\Poze").IsProtected(@"E:\Necunoscut\a.jpg"));
    }

    [Fact]
    public void Identitatea_fizica_prinde_aliasurile_necunoscute()
    {
        var id = new MapIdentity();
        var same = new FileIdentity(1, 0, 42);
        id.Ids[@"\\localhost\D$\Poze"] = same;
        id.Ids[@"D:\Poze"] = same;
        string folder = OperatingSystemTempFolder();
        var r = new FakeResolver();
        r.Aliases[folder] = @"D:\Poze";
        Assert.True(Guard(r, id, folder).IsProtected(@"\\localhost\D$\Poze\a.jpg"));
    }

    // Directory.Exists trebuie sa fie adevarat pentru folderul protejat din test
    private static string OperatingSystemTempFolder() => Path.GetTempPath().TrimEnd('\\');

    private sealed class MapIdentity : IFileIdentityService
    {
        public Dictionary<string, FileIdentity> Ids { get; } = new(StringComparer.OrdinalIgnoreCase);

        public FileIdentity? TryGetIdentity(string path)
        {
            if (Ids.TryGetValue(path, out var id)) return id;
            return string.Equals(path, OperatingSystemTempFolder(), StringComparison.OrdinalIgnoreCase) ? Ids[@"D:\Poze"] : null;
        }
    }
}
