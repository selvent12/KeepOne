using Dubluri.Core.Scanning;
using Dubluri.Core.Selection;
using Xunit;

namespace Dubluri.Tests;

public sealed class SelectionTests
{
    private static SelectionManager Manager(params DuplicateGroup[] groups) => new(groups, new FakeResolver());

    [Fact]
    public void Nu_se_poate_selecta_ultima_copie()
    {
        var a = Groups.Virtual(@"D:\A\f.jpg");
        var b = Groups.Virtual(@"D:\B\f.jpg");
        var m = Manager(Groups.Of(a, b));

        Assert.True(m.TrySetSelected(a, true, out _));
        Assert.False(m.TrySetSelected(b, true, out string? reason));
        Assert.Equal(SelectionManager.LastCopyMessage, reason);
        Assert.False(b.IsSelected);
    }

    [Fact]
    public void Fisierul_protejat_nu_se_poate_selecta()
    {
        var a = Groups.Virtual(@"D:\A\f.jpg", isProtected: true);
        var b = Groups.Virtual(@"D:\B\f.jpg");
        var m = Manager(Groups.Of(a, b));

        Assert.False(m.TrySetSelected(a, true, out string? reason));
        Assert.Equal(SelectionManager.ProtectedMessage, reason);
    }

    [Fact]
    public void Selectia_pe_folder_blocheaza_grupul_cu_toate_copiile_in_folder()
    {
        var a = Groups.Virtual(@"D:\X\a\f.jpg");
        var b = Groups.Virtual(@"D:\X\b\f.jpg");
        var c = Groups.Virtual(@"D:\X\g.jpg");
        var d = Groups.Virtual(@"D:\Y\g.jpg");
        var m = Manager(Groups.Of(a, b), Groups.Of(c, d));

        var result = m.SelectFolder(@"D:\X");

        Assert.Equal(1, result.GroupsBlockedLastCopy);
        Assert.Equal(1, result.Changed);
        Assert.False(a.IsSelected || b.IsSelected);
        Assert.True(c.IsSelected);
        Assert.False(d.IsSelected);
    }

    [Fact]
    public void Selectia_pe_folder_tine_cont_de_ce_e_deja_selectat()
    {
        var a = Groups.Virtual(@"D:\X\f.jpg");
        var b = Groups.Virtual(@"D:\Y\f.jpg");
        var m = Manager(Groups.Of(a, b));
        m.TrySetSelected(b, true, out _);

        var result = m.SelectFolder(@"D:\X");

        Assert.Equal(1, result.GroupsBlockedLastCopy);
        Assert.False(a.IsSelected);
    }

    [Fact]
    public void Selectia_pe_folder_pastreaza_una_cand_toate_copiile_sunt_in_folder()
    {
        var original = Groups.Virtual(@"D:\X\IMG_001.jpg");
        var copie1 = Groups.Virtual(@"D:\X\IMG_001 (1).jpg");
        var copie2 = Groups.Virtual(@"D:\X\sub\IMG_001 - Copy.jpg");
        var m = Manager(Groups.Of(copie1, copie2, original));

        var result = m.SelectFolder(@"D:\X", KeepRule.ShortestPath);

        Assert.Equal(1, result.GroupsKeptOne);
        Assert.Equal(0, result.GroupsBlockedLastCopy);
        Assert.Equal(2, result.Changed);
        Assert.False(original.IsSelected);
        Assert.True(copie1.IsSelected && copie2.IsSelected);
    }

    [Fact]
    public void Pastrarea_unei_copii_respecta_regula_aleasa_si_selectia_existenta()
    {
        var veche = Groups.Virtual(@"D:\X\foarte-lung-veche.jpg", new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var noua = Groups.Virtual(@"D:\X\n.jpg", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var m = Manager(Groups.Of(veche, noua));
        m.TrySetSelected(veche, true, out _); // copia pe care regula o va pastra era deja bifata

        var result = m.SelectFolder(@"D:\X", KeepRule.Oldest);

        Assert.Equal(1, result.GroupsKeptOne);
        Assert.False(veche.IsSelected);
        Assert.True(noua.IsSelected);
    }

    [Fact]
    public void Pastrarea_unei_copii_nu_anuleaza_bifa_din_afara_folderului()
    {
        var inFolder = Groups.Virtual(@"D:\X\f.jpg");
        var afara = Groups.Virtual(@"D:\Y\f.jpg");
        var m = Manager(Groups.Of(inFolder, afara));
        m.TrySetSelected(afara, true, out _);

        var result = m.SelectFolder(@"D:\X", KeepRule.ShortestPath);

        Assert.Equal(1, result.GroupsBlockedLastCopy);
        Assert.Equal(0, result.GroupsKeptOne);
        Assert.False(inFolder.IsSelected);
        Assert.True(afara.IsSelected);
    }

    [Fact]
    public void Pastrarea_unei_copii_nu_atinge_fisierele_protejate()
    {
        var prot = Groups.Virtual(@"D:\X\p.jpg", isProtected: true);
        var a = Groups.Virtual(@"D:\X\a.jpg");
        var b = Groups.Virtual(@"D:\X\b.jpg");
        var m = Manager(Groups.Of(prot, a, b));

        var result = m.SelectFolder(@"D:\X", KeepRule.ShortestPath);

        Assert.Equal(0, result.GroupsKeptOne);
        Assert.False(prot.IsSelected);
        Assert.True(a.IsSelected && b.IsSelected);
    }

    [Fact]
    public void Selectia_pe_folder_nu_ia_prefixul_fals()
    {
        var a = Groups.Virtual(@"D:\X2\f.jpg");
        var b = Groups.Virtual(@"D:\Y\f.jpg");
        var m = Manager(Groups.Of(a, b));
        Assert.Equal(0, m.SelectFolder(@"D:\X").Changed);
    }

    [Fact]
    public void Selectia_pe_folder_ocoleste_copiile_protejate()
    {
        var a = Groups.Virtual(@"D:\X\f.jpg", isProtected: true);
        var b = Groups.Virtual(@"D:\X\g.jpg");
        var m = Manager(Groups.Of(a, b));

        var result = m.SelectFolder(@"D:\X");

        Assert.Equal(1, result.ProtectedSkipped);
        Assert.True(b.IsSelected);
        Assert.False(a.IsSelected);
    }

    [Fact]
    public void Deselectia_pe_folder()
    {
        var a = Groups.Virtual(@"D:\X\f.jpg");
        var b = Groups.Virtual(@"D:\Y\f.jpg");
        var m = Manager(Groups.Of(a, b));
        m.SelectFolder(@"D:\X");
        Assert.Equal(1, m.DeselectFolder(@"D:\X"));
        Assert.False(a.IsSelected);
    }

    [Theory]
    [InlineData(KeepRule.Oldest, 0)]
    [InlineData(KeepRule.Newest, 1)]
    [InlineData(KeepRule.ShortestPath, 2)]
    public void Regulile_pastreaza_copia_potrivita(KeepRule rule, int keptIndex)
    {
        var files = new[]
        {
            Groups.Virtual(@"D:\foarte\lung\vechi.jpg", new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            Groups.Virtual(@"D:\foarte\lung\nou123.jpg", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            Groups.Virtual(@"D:\s.jpg", new DateTime(2010, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
        };
        Manager(Groups.Of(files)).ApplyRule(rule, Array.Empty<string>());

        for (int i = 0; i < files.Length; i++) Assert.Equal(i != keptIndex, files[i].IsSelected);
    }

    [Fact]
    public void Regula_locatiei_prioritare()
    {
        var a = Groups.Virtual(@"D:\Copii\f.jpg");
        var b = Groups.Virtual(@"D:\Arhiva\adanc\de\tot\f.jpg");
        Manager(Groups.Of(a, b)).ApplyRule(KeepRule.PriorityLocation, new[] { @"D:\Arhiva", @"D:\Copii" });

        Assert.True(a.IsSelected);
        Assert.False(b.IsSelected);
    }

    [Fact]
    public void Copia_protejata_e_cea_pastrata_automat_indiferent_de_regula()
    {
        var a = Groups.Virtual(@"D:\s.jpg");
        var b = Groups.Virtual(@"D:\foarte\lung\f.jpg", isProtected: true);
        Manager(Groups.Of(a, b)).ApplyRule(KeepRule.ShortestPath, Array.Empty<string>());

        Assert.True(a.IsSelected);
        Assert.False(b.IsSelected);
    }
}
