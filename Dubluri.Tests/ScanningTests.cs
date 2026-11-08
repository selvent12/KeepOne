using Dubluri.Core.Scanning;
using Dubluri.Platform.Windows;
using Xunit;

namespace Dubluri.Tests;

public sealed class ScanningTests : IDisposable
{
    private readonly TempDir _t = new();
    private readonly WindowsPathResolver _resolver = new();
    private readonly NtfsFileIdentityService _identity = new();
    private readonly List<ScanIssue> _issues = new();

    public void Dispose() => _t.Dispose();

    private List<ScannedFile> Scan(FileFilter? filter = null, IReadOnlyList<string>? excluded = null, IReadOnlyCollection<string>? names = null, params string[] roots) =>
        new FileScanner(_resolver).Scan(new ScanOptions
        {
            Roots = roots,
            Filter = filter ?? new FileFilter(true, Array.Empty<string>()),
            ExcludedFolders = excluded ?? Array.Empty<string>(),
            ExcludedFolderNames = names ?? Array.Empty<string>(),
        }, _issues, null, default);

    private List<DuplicateGroup> Find(IReadOnlyList<ScannedFile> files, HashCache? cache = null) =>
        new DuplicateFinder(_identity, cache ?? new HashCache(), 2).Find(files, _ => false, _issues, null, default);

    [Fact]
    public void Fisiere_identice_cu_nume_si_date_diferite_sunt_dubluri()
    {
        string a = _t.File(@"A\vacanta.jpg", "acelasi continut");
        string b = _t.File(@"B\alt nume.png", "acelasi continut");
        File.SetLastWriteTimeUtc(a, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var groups = Find(Scan(roots: _t.Root));

        Assert.Single(groups);
        Assert.Equal(2, groups[0].Files.Count);
        Assert.True(File.Exists(b));
    }

    [Fact]
    public void Aceeasi_marime_continut_diferit_nu_sunt_dubluri()
    {
        _t.File(@"A\a.jpg", "continut-1");
        _t.File(@"B\b.jpg", "continut-2");
        Assert.Empty(Find(Scan(roots: _t.Root)));
    }

    [Fact]
    public void Fisiere_mari_care_difera_doar_la_mijloc_nu_sunt_dubluri()
    {
        var bytes = new byte[300 * 1024];
        Directory.CreateDirectory(_t.Dir("A"));
        File.WriteAllBytes(Path.Combine(_t.Root, "A", "a.bin"), bytes);
        bytes[150 * 1024] = 1;
        File.WriteAllBytes(Path.Combine(_t.Root, "A", "b.bin"), bytes);
        Assert.Empty(Find(Scan(roots: _t.Root)));
    }

    [Fact]
    public void Fisiere_mari_identice_sunt_dubluri()
    {
        var bytes = new byte[300 * 1024];
        new Random(7).NextBytes(bytes);
        _t.Dir("A");
        File.WriteAllBytes(Path.Combine(_t.Root, "A", "a.bin"), bytes);
        File.WriteAllBytes(Path.Combine(_t.Root, "A", "b.bin"), bytes);
        Assert.Single(Find(Scan(roots: _t.Root)));
    }

    [Fact]
    public void Hardlinkurile_nu_sunt_raportate()
    {
        string a = _t.File(@"A\f.jpg", "continut");
        Cmd.HardLink(Path.Combine(_t.Dir("B"), "link.jpg"), a);
        Assert.Empty(Find(Scan(roots: _t.Root)));
    }

    [Fact]
    public void Hardlink_plus_copie_reala_este_un_singur_duplicat()
    {
        string a = _t.File(@"A\f.jpg", "continut");
        Cmd.HardLink(Path.Combine(_t.Dir("B"), "link.jpg"), a);
        _t.File(@"C\copie.jpg", "continut");

        var groups = Find(Scan(roots: _t.Root));

        Assert.Single(groups);
        Assert.Equal(2, groups[0].Files.Count);
    }

    [Fact]
    public void Locatiile_suprapuse_nu_dubleaza_fisierele()
    {
        _t.File(@"A\sub\f.jpg", "continut");
        var files = Scan(roots: new[] { Path.Combine(_t.Root, "A"), Path.Combine(_t.Root, "A", "sub"), Path.Combine(_t.Root, "A") });
        Assert.Single(files);
    }

    [Fact]
    public void Locatia_prin_junction_nu_dubleaza_fisierele()
    {
        string real = _t.Dir("Real");
        _t.File(@"Real\f.jpg", "continut");
        string link = Path.Combine(_t.Dir("L"), "J");
        Cmd.Junction(link, real);

        Assert.Single(Scan(roots: new[] { real, link }));
    }

    [Fact]
    public void Junction_din_interiorul_scanarii_nu_e_urmat()
    {
        string real = _t.Dir("Real");
        _t.File(@"Real\f.jpg", "continut");
        Cmd.Junction(Path.Combine(_t.Dir("Scan"), "J"), real);

        Assert.Empty(Scan(roots: Path.Combine(_t.Root, "Scan")));
    }

    [Fact]
    public void Excluderea_subfolderelor_si_a_numelor()
    {
        _t.File(@"S\pastrat.jpg", "1");
        _t.File(@"S\exclus\a.jpg", "2");
        _t.File(@"S\x\$Recycle.Bin\b.jpg", "3");
        var files = Scan(null, new[] { Path.Combine(_t.Root, "S", "exclus") }, new[] { "$Recycle.Bin" }, Path.Combine(_t.Root, "S"));
        Assert.Equal("pastrat.jpg", Path.GetFileName(Assert.Single(files).Path));
    }

    [Fact]
    public void Filtrul_e_case_insensitive_si_ignora_zero_octeti()
    {
        _t.File(@"S\a.JPG", "x");
        _t.File(@"S\b.txt", "x");
        _t.File(@"S\gol.jpg", "");
        var files = Scan(new FileFilter(false, new[] { "jpg" }), roots: Path.Combine(_t.Root, "S"));
        Assert.Equal("a.JPG", Path.GetFileName(Assert.Single(files).Path));
    }

    [Fact]
    public void Extensii_personalizate()
    {
        Assert.Equal(new[] { "dwg", "zip", "rar" }, FileFilter.ParseCustom("dwg, .zip; *.rar"));
    }

    [Fact]
    public void Cache_ul_se_refoloseste_si_se_invalideaza()
    {
        string a = _t.File(@"A\a.jpg", "continut"), b = _t.File(@"B\b.jpg", "continut");
        string cacheFile = Path.Combine(_t.Dir("cache"), "h.bin");
        var cache = new HashCache(cacheFile);
        var files = Scan(roots: new[] { Path.Combine(_t.Root, "A"), Path.Combine(_t.Root, "B") });
        Assert.Single(Find(files, cache));
        cache.Save();

        var reloaded = new HashCache(cacheFile);
        Assert.Equal(2, reloaded.Count);
        Assert.NotNull(reloaded.GetFull(files[0]));
        Assert.Null(reloaded.GetFull(files[0] with { LastWriteUtc = files[0].LastWriteUtc.AddSeconds(5) }));
        Assert.True(File.Exists(a) && File.Exists(b));

        reloaded.Clear();
        Assert.Equal(0, reloaded.Count);
        Assert.False(File.Exists(cacheFile));
    }

    [Fact]
    public void Fisier_blocat_e_raportat_si_nu_opreste_scanarea()
    {
        string blocat = _t.File(@"A\blocat.jpg", "continut");
        _t.File(@"A\b.jpg", "continut");
        _t.File(@"A\c.jpg", "continut");
        var files = Scan(roots: Path.Combine(_t.Root, "A"));

        using var lockHandle = new FileStream(blocat, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var groups = Find(files);

        Assert.Single(groups);
        Assert.Equal(2, groups[0].Files.Count);
        Assert.Contains(_issues, i => i.Path == blocat);
    }
}
