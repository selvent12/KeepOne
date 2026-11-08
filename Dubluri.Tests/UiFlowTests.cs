using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dubluri.App;
using Dubluri.App.ViewModels;
using Dubluri.Core.Settings;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(Dubluri.Tests.HeadlessApp))]

namespace Dubluri.Tests;

public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Dubluri.App.App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>Fereastra reala, randata in memorie, condusa prin comenzile ei, pe fisiere reale.</summary>
public sealed class UiFlowTests : IDisposable
{
    private readonly TempDir _t = new();

    public void Dispose() => _t.Dispose();

    private sealed class Dialogs : IUserDialogs
    {
        public List<string> Shown { get; } = new();
        public int Confirmations { get; private set; }
        public string? FolderToPick { get; set; }

        public Task<string?> PickFolderAsync(string title) => Task.FromResult(FolderToPick);
        public Task<string?> PickSaveFileAsync(string title, string suggestedName) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message, string yesText, string noText, bool danger)
        {
            Confirmations++;
            return Task.FromResult(true);
        }
        public Task ShowAsync(string title, string message)
        {
            Shown.Add(message);
            return Task.CompletedTask;
        }
    }

    private (MainViewModel Vm, Dialogs Dialogs, Window Window) Open(AppSettings settings)
    {
        string dir = _t.Dir("setari");
        File.WriteAllText(Path.Combine(dir, "settings.json"), JsonSerializer.Serialize(settings));
        var dialogs = new Dialogs();
        var vm = new MainViewModel(PlatformServices.Create(), new SettingsStore(dir)) { Dialogs = dialogs };
        var window = new MainWindow { DataContext = vm, Width = 1240, Height = 820 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (vm, dialogs, window);
    }

    private static List<FileRow> Files(MainViewModel vm) => vm.Rows.OfType<FileRow>().ToList();

    private static async Task Run(Func<Task> test)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        // varianta cu rezultat: cea cu Func<Task> s-ar termina la primul await, fara sa astepte testul
        await session.Dispatch(async () =>
        {
            await test();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public Task Fluxul_complet_scanare_selectie_simulare_carantina() => Run(async () =>
    {
        string poze = _t.Dir("Poze");
        string original = _t.File(@"Poze\vara.jpg", "imagine-1");
        string copie1 = _t.File(@"Copii\vara copie.jpg", "imagine-1");
        string copie2 = _t.File(@"Copii\sub\alt nume.jpg", "imagine-1");
        string a = _t.File(@"Copii\doc.jpg", "imagine-22");
        string b = _t.File(@"Altele\doc.jpg", "imagine-22");
        _t.File(@"Copii\unic.jpg", "imagine-333");
        string q = _t.Dir("Carantina");

        var (vm, dialogs, window) = Open(new AppSettings
        {
            ProtectedFolders = { poze },
            SearchLocations = { Path.Combine(_t.Root, "Copii"), Path.Combine(_t.Root, "Altele") },
            EnabledPresets = { "Imagini" },
            QuarantineFolder = q,
        });

        await vm.StartScanCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, vm.ActiveTab);
        Assert.Equal(5, Files(vm).Count);
        Assert.Equal(2, vm.Rows.OfType<GroupRow>().Count());
        var protectedRow = Assert.Single(Files(vm), r => r.IsProtected);
        Assert.False(protectedRow.CanSelect);

        // randul protejat are bifa dezactivata in fereastra
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var boxes = window.GetVisualDescendants().OfType<CheckBox>().Where(c => c.DataContext is FileRow).ToList();
        Assert.NotEmpty(boxes);
        Assert.Contains(boxes, c => !c.IsEnabled && ((FileRow)c.DataContext!).IsProtected);

        // nu se poate selecta ultima copie
        var rowA = Files(vm).First(r => r.Path == a);
        var rowB = Files(vm).First(r => r.Path == b);
        rowA.IsSelected = true;
        rowB.IsSelected = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(rowA.IsSelected);
        Assert.False(rowB.IsSelected);
        Assert.Contains("cel puțin o copie", vm.StatusMessage);

        // auto-selectia pastreaza originalul protejat
        vm.ClearSelectionCommand.Execute(null);
        vm.RuleIndex = 3;
        vm.ApplyRuleCommand.Execute(null);
        Assert.False(protectedRow.IsSelected);
        Assert.Equal(3, Files(vm).Count(r => r.IsSelected));

        // fereastra se poate randa cu rezultatele incarcate
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Assert.NotNull(window.CaptureRenderedFrame());

        // simulare: nimic nu se modifica
        vm.DryRun = true;
        await vm.ExecuteDeletionCommand.ExecuteAsync(null);
        Assert.True(File.Exists(copie1) && File.Exists(copie2));
        Assert.Contains("Simulare", vm.ProgressText);
        Assert.Equal(0, dialogs.Confirmations);

        // carantina reala
        vm.DryRun = false;
        vm.DeleteModeIndex = 1;
        await vm.ExecuteDeletionCommand.ExecuteAsync(null);
        Assert.Equal(1, dialogs.Confirmations);
        Assert.True(File.Exists(original), "originalul protejat trebuie sa ramana");
        Assert.False(File.Exists(copie1));
        Assert.False(File.Exists(copie2));
        Assert.Equal(1, new[] { a, b }.Count(File.Exists));
        Assert.Empty(vm.Rows);
        Assert.Equal(3, Directory.GetFiles(q, "*", SearchOption.AllDirectories).Length);
        window.Close();
    });

    [Fact]
    public Task Ascunderea_copiilor_protejate_schimba_doar_afisarea() => Run(async () =>
    {
        string poze = _t.Dir("Poze");
        string original = _t.File(@"Poze\a.jpg", "imagine-1");
        string copie = _t.File(@"Copii\a.jpg", "imagine-1");
        _t.File(@"Poze\b1.jpg", "imagine-22");   // grup doar cu copii protejate
        _t.File(@"Poze\b2.jpg", "imagine-22");
        string q = _t.Dir("Carantina");
        var (vm, _, window) = Open(new AppSettings { ProtectedFolders = { poze }, SearchLocations = { Path.GetDirectoryName(copie)! }, QuarantineFolder = q });

        await vm.StartScanCommand.ExecuteAsync(null);
        Assert.Equal(4, Files(vm).Count);
        Assert.Equal(2, vm.Rows.OfType<GroupRow>().Count());
        Assert.Contains(Format.Size(9), vm.SummaryText); // grupul doar protejat nu e recuperabil

        vm.HideProtectedInResults = true;
        Assert.Equal(copie, Assert.Single(Files(vm)).Path);
        Assert.Single(vm.Rows.OfType<GroupRow>());

        // copia protejata ascunsa ramane copia pastrata: stergerea functioneaza si o verifica
        vm.ApplyRuleCommand.Execute(null);
        vm.DeleteModeIndex = 1;
        await vm.ExecuteDeletionCommand.ExecuteAsync(null);
        Assert.False(File.Exists(copie));
        Assert.True(File.Exists(original));

        vm.HideProtectedInResults = false;
        Assert.Equal(2, Files(vm).Count);
        Assert.All(Files(vm), r => Assert.True(r.IsProtected));
        window.Close();
    });

    [Fact]
    public Task Folder_protejat_lipsa_blocheaza_executia_din_interfata() => Run(async () =>
    {
        string lipsa = Path.Combine(_t.Root, "StickScos");
        string a = _t.File(@"A\x.jpg", "aaa"), b = _t.File(@"B\x.jpg", "aaa");
        var (vm, dialogs, window) = Open(new AppSettings
        {
            ProtectedFolders = { lipsa },
            SearchLocations = { Path.GetDirectoryName(a)!, Path.GetDirectoryName(b)! },
        });
        Assert.NotNull(vm.MissingProtectedText);

        await vm.StartScanCommand.ExecuteAsync(null);
        vm.RuleIndex = 3;
        vm.ApplyRuleCommand.Execute(null);
        await vm.ExecuteDeletionCommand.ExecuteAsync(null);

        Assert.True(File.Exists(a) && File.Exists(b));
        Assert.Contains(dialogs.Shown, m => m.Contains("StickScos"));
        window.Close();
    });

    [Fact]
    public Task Selectia_pe_folder_explica_blocarea() => Run(async () =>
    {
        string dir = _t.Dir("X");
        _t.File(@"X\a\f.jpg", "aaa");
        _t.File(@"X\b\f.jpg", "aaa");
        var (vm, _, window) = Open(new AppSettings { SearchLocations = { dir }, KeepOneOnFolderSelect = false });

        await vm.StartScanCommand.ExecuteAsync(null);
        vm.FolderForSelection = dir;
        vm.SelectFolderCommand.Execute(null);

        Assert.DoesNotContain(Files(vm), r => r.IsSelected);
        Assert.Contains("cel puțin una", vm.StatusMessage);
        window.Close();
    });

    [Fact]
    public Task Dubluri_in_acelasi_folder_cu_o_singura_locatie() => Run(async () =>
    {
        string dir = _t.Dir("X");
        string original = _t.File(@"X\IMG_001.jpg", "aaa");
        string copie = _t.File(@"X\IMG_001 (1).jpg", "aaa");
        string q = _t.Dir("Carantina");
        var (vm, _, window) = Open(new AppSettings { SearchLocations = { dir }, QuarantineFolder = q });

        await vm.StartScanCommand.ExecuteAsync(null);
        Assert.True(vm.KeepOneOnFolderSelect);
        vm.RuleIndex = 3; // calea cea mai scurta
        vm.FolderForSelection = dir;
        vm.SelectFolderCommand.Execute(null);

        Assert.Equal(copie, Assert.Single(Files(vm), r => r.IsSelected).Path);
        Assert.Contains("s-a păstrat câte una", vm.StatusMessage);

        vm.DeleteModeIndex = 1;
        await vm.ExecuteDeletionCommand.ExecuteAsync(null);
        Assert.True(File.Exists(original));
        Assert.False(File.Exists(copie));
        window.Close();
    });

    [Fact]
    public Task Schimbarea_listei_protejate_invalideaza_rezultatele() => Run(async () =>
    {
        string dir = _t.Dir("X");
        _t.File(@"X\a.jpg", "aaa");
        _t.File(@"X\b.jpg", "aaa");
        var (vm, dialogs, window) = Open(new AppSettings { SearchLocations = { dir } });

        await vm.StartScanCommand.ExecuteAsync(null);
        Assert.NotEmpty(vm.Rows);

        dialogs.FolderToPick = dir;
        await vm.AddProtectedCommand.ExecuteAsync(null);
        Assert.Empty(vm.Rows);
        window.Close();
    });
}
