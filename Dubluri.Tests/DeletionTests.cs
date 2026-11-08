using Dubluri.Core.Deletion;
using Dubluri.Core.Platform;
using Dubluri.Core.Protection;
using Dubluri.Core.Scanning;
using Dubluri.Platform.Windows;
using Xunit;

namespace Dubluri.Tests;

public sealed class DeletionTests : IDisposable
{
    private readonly TempDir _t = new();
    private readonly WindowsPathResolver _resolver = new();
    private readonly NtfsFileIdentityService _identity = new();
    private readonly FakeTrash _trash = new();
    private OperationLog? _log;

    public void Dispose()
    {
        _log?.Dispose();
        _t.Dispose();
    }

    private DeletionService Service(params string[] protectedFolders)
    {
        _log = new OperationLog(Path.Combine(_t.Dir("loguri"), Guid.NewGuid().ToString("N") + ".csv"));
        return new DeletionService(new ProtectionGuard(_resolver, _identity, protectedFolders), _trash, _identity, _resolver, _log);
    }

    private static DeletionRequest Request(DeleteMode mode, params DuplicateGroup[] groups) =>
        new(groups, mode, null, DryRun: false);

    [Fact]
    public void Sterge_copia_selectata_si_o_pastreaza_pe_cealalta()
    {
        string a = _t.File(@"A\f.jpg", "continut"), b = _t.File(@"B\f.jpg", "continut");
        var report = Service().Execute(Request(DeleteMode.RecycleBin, Groups.Of(Groups.OnDisk(a), Groups.OnDisk(b, selected: true))), null, default);

        Assert.Equal(1, report.Count(OperationResult.Done));
        Assert.True(File.Exists(a));
        Assert.False(File.Exists(b));
        Assert.Equal(new[] { b }, _trash.Trashed);
    }

    [Fact]
    public void Refuza_fisierul_protejat_chiar_daca_interfata_a_fost_ocolita()
    {
        string poze = _t.Dir("Poze");
        string prot = _t.File(@"Poze\f.jpg", "continut"), altul = _t.File(@"B\f.jpg", "continut");

        // apel direct in Core: fisierul nu e marcat protejat si e selectat
        var group = Groups.Of(Groups.OnDisk(prot, isProtected: false, selected: true), Groups.OnDisk(altul));
        var report = Service(poze).Execute(Request(DeleteMode.Permanent, group), null, default);

        Assert.Equal(1, report.Count(OperationResult.Refused));
        Assert.Equal(0, report.Count(OperationResult.Done));
        Assert.True(File.Exists(prot));
    }

    [Theory]
    [InlineData(DeleteMode.RecycleBin)]
    [InlineData(DeleteMode.Quarantine)]
    [InlineData(DeleteMode.Permanent)]
    public void Protectia_se_aplica_la_toate_modurile(DeleteMode mode)
    {
        string poze = _t.Dir("Poze");
        string prot = _t.File(@"Poze\f.jpg", "continut"), altul = _t.File(@"B\f.jpg", "continut");
        var group = Groups.Of(Groups.OnDisk(prot, selected: true), Groups.OnDisk(altul));
        var request = new DeletionRequest(new[] { group }, mode, _t.Dir("Carantina"), DryRun: false);

        Service(poze).Execute(request, null, default);

        Assert.True(File.Exists(prot));
        Assert.Empty(_trash.Trashed);
    }

    [Fact]
    public void Refuza_prin_junction_catre_folderul_protejat()
    {
        string poze = _t.Dir("Poze");
        _t.File(@"Poze\f.jpg", "continut");
        string altul = _t.File(@"B\f.jpg", "continut");
        string link = Path.Combine(_t.Dir("L"), "J");
        Cmd.Junction(link, poze);

        var group = Groups.Of(Groups.OnDisk(Path.Combine(link, "f.jpg"), selected: true), Groups.OnDisk(altul));
        var report = Service(poze).Execute(Request(DeleteMode.Permanent, group), null, default);

        Assert.Equal(1, report.Count(OperationResult.Refused));
        Assert.True(File.Exists(Path.Combine(poze, "f.jpg")));
    }

    [Fact]
    public void Refuza_ultima_copie_chiar_daca_toate_sunt_selectate_direct()
    {
        string a = _t.File(@"A\f.jpg", "continut"), b = _t.File(@"B\f.jpg", "continut");
        var group = Groups.Of(Groups.OnDisk(a, selected: true), Groups.OnDisk(b, selected: true));
        var report = Service().Execute(Request(DeleteMode.Permanent, group), null, default);

        Assert.Equal(2, report.Count(OperationResult.Refused));
        Assert.True(File.Exists(a) && File.Exists(b));
    }

    [Fact]
    public void Folder_protejat_lipsa_blocheaza_stergerile()
    {
        string a = _t.File(@"A\f.jpg", "continut"), b = _t.File(@"B\f.jpg", "continut");
        var group = Groups.Of(Groups.OnDisk(a), Groups.OnDisk(b, selected: true));
        var report = Service(Path.Combine(_t.Root, "StickScos")).Execute(Request(DeleteMode.Permanent, group), null, default);

        Assert.Equal(1, report.Count(OperationResult.Refused));
        Assert.True(File.Exists(b));
    }

    [Fact]
    public void Comparatia_octet_cu_octet_opreste_stergerea_cand_continutul_difera()
    {
        // hash identic in model (ex. cache gresit), continut diferit pe disc
        string a = _t.File(@"A\f.jpg", "continutA"), b = _t.File(@"B\f.jpg", "continutB");
        var group = Groups.Of(Groups.OnDisk(a), Groups.OnDisk(b, selected: true));
        var report = Service().Execute(Request(DeleteMode.Permanent, group), null, default);

        Assert.Equal(1, report.Count(OperationResult.Refused));
        Assert.True(File.Exists(b));
    }

    [Theory]
    [InlineData(1, -1, true)]                     // un octet
    [InlineData(1024, -1, true)]                  // fisier mic
    [InlineData(32 * 1024 * 1024, -1, true)]      // exact o bucata
    [InlineData(32 * 1024 * 1024, 32 * 1024 * 1024 - 1, false)]  // difera ultimul octet al bucatii
    [InlineData(40 * 1024 * 1024, 39 * 1024 * 1024, false)]      // difera in a doua bucata
    [InlineData(40 * 1024 * 1024, -1, true)]      // peste o bucata, identice
    public void Comparatia_octet_cu_octet(int size, int differentAt, bool expectedEqual)
    {
        var bytes = new byte[size];
        new Random(5).NextBytes(bytes);
        string a = Path.Combine(_t.Dir("A"), "a.bin"), b = Path.Combine(_t.Dir("B"), "b.bin");
        File.WriteAllBytes(a, bytes);
        if (differentAt >= 0) bytes[differentAt] ^= 0xFF;
        File.WriteAllBytes(b, bytes);

        Assert.Equal(expectedEqual, DeletionService.BytesEqual(a, b, default));
    }

    [Fact]
    public void Comparatia_fisierelor_goale_si_de_lungimi_diferite()
    {
        string a = _t.File(@"A\gol.bin", ""), b = _t.File(@"B\gol.bin", ""), c = _t.File(@"C\plin.bin", "x");
        Assert.True(DeletionService.BytesEqual(a, b, default));
        Assert.False(DeletionService.BytesEqual(a, c, default));
    }

    [Fact]
    public void Hardlink_nu_conteaza_drept_copie_pastrata()
    {
        string a = _t.File(@"A\f.jpg", "continut");
        string b = Path.Combine(_t.Dir("B"), "f.jpg");
        Cmd.HardLink(b, a);
        var group = Groups.Of(Groups.OnDisk(a), Groups.OnDisk(b, selected: true));
        var report = Service().Execute(Request(DeleteMode.Permanent, group), null, default);

        Assert.Equal(1, report.Count(OperationResult.Refused));
        Assert.True(File.Exists(b));
    }

    [Fact]
    public void Fisier_modificat_dupa_scanare_e_refuzat()
    {
        string a = _t.File(@"A\f.jpg", "continut"), b = _t.File(@"B\f.jpg", "continut");
        var fb = Groups.OnDisk(b, selected: true);
        File.SetLastWriteTimeUtc(b, DateTime.UtcNow.AddHours(1));
        var report = Service().Execute(Request(DeleteMode.Permanent, Groups.Of(Groups.OnDisk(a), fb)), null, default);

        Assert.Equal(1, report.Count(OperationResult.Refused));
        Assert.True(File.Exists(b));
    }

    [Fact]
    public void Simularea_nu_modifica_nimic()
    {
        string a = _t.File(@"A\f.jpg", "continut"), b = _t.File(@"B\f.jpg", "continut");
        var group = Groups.Of(Groups.OnDisk(a), Groups.OnDisk(b, selected: true));
        var report = Service().Execute(new DeletionRequest(new[] { group }, DeleteMode.Permanent, null, DryRun: true), null, default);

        Assert.Equal(1, report.Count(OperationResult.Simulated));
        Assert.True(File.Exists(b));
        Assert.Empty(_trash.Trashed);
    }

    [Fact]
    public void Carantina_pastreaza_structura_de_foldere()
    {
        string a = _t.File(@"A\f.jpg", "continut"), b = _t.File(@"B\sub\f.jpg", "continut");
        string q = _t.Dir("Carantina");
        var group = Groups.Of(Groups.OnDisk(a), Groups.OnDisk(b, selected: true));
        var report = Service().Execute(new DeletionRequest(new[] { group }, DeleteMode.Quarantine, q, DryRun: false), null, default);

        Assert.Equal(1, report.Count(OperationResult.Done));
        Assert.False(File.Exists(b));
        string expected = Path.Combine(q, b[0].ToString(), b[3..]);
        Assert.True(File.Exists(expected));
    }

    [Fact]
    public void Carantina_in_folder_protejat_e_refuzata()
    {
        string poze = _t.Dir("Poze");
        string a = _t.File(@"A\f.jpg", "continut"), b = _t.File(@"B\f.jpg", "continut");
        var group = Groups.Of(Groups.OnDisk(a), Groups.OnDisk(b, selected: true));
        var report = Service(poze).Execute(new DeletionRequest(new[] { group }, DeleteMode.Quarantine, _t.Dir("Poze", "Q"), DryRun: false), null, default);

        Assert.Equal(1, report.Count(OperationResult.Refused));
        Assert.True(File.Exists(b));
    }

    [Fact]
    public void Unitate_fara_cos_refuza_si_nu_sterge_definitiv()
    {
        string a = _t.File(@"A\f.jpg", "continut"), b = _t.File(@"B\f.jpg", "continut");
        _trash.Refuse = "fără cos";
        var group = Groups.Of(Groups.OnDisk(a), Groups.OnDisk(b, selected: true));
        var report = Service().Execute(Request(DeleteMode.RecycleBin, group), null, default);

        Assert.Equal(1, report.Count(OperationResult.Refused));
        Assert.True(File.Exists(b));
    }

    [Fact]
    public void Jurnalul_contine_operatia()
    {
        string a = _t.File(@"A\f.jpg", "continut"), b = _t.File(@"B\f.jpg", "continut");
        var service = Service();
        service.Execute(Request(DeleteMode.RecycleBin, Groups.Of(Groups.OnDisk(a), Groups.OnDisk(b, selected: true))), null, default);
        string logFile = _log!.FilePath;
        _log.Dispose();
        string text = File.ReadAllText(logFile);
        Assert.Contains(b, text);
        Assert.Contains("Reușit", text);
    }

    [Fact]
    public void Cosul_real_primeste_fisierul()
    {
        string file = _t.File("dubluri-test-recycle-bin.txt", "fisier de test Dubluri, poate fi sters");
        var trash = new WindowsTrashService();
        if (!trash.CanTrash(file, out _)) Assert.Skip("Folderul temporar nu e pe o unitate cu Recycle Bin.");
        trash.MoveToTrash(file);
        Assert.False(File.Exists(file));
    }
}
