using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dubluri.Core.Deletion;
using Dubluri.Core.Protection;
using Dubluri.Core.Scanning;
using Dubluri.Core.Selection;
using Dubluri.Core.Settings;

namespace Dubluri.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private static readonly HashSet<string> Previewable = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp" };
    private const long MaxPreviewBytes = 100L * 1024 * 1024;

    private readonly PlatformServices _platform;
    private readonly SettingsStore _store;
    private readonly HashCache _cache;
    private readonly DispatcherTimer _progressTimer;

    private AppSettings _settings;
    private List<DuplicateGroup> _groups = new();
    private readonly Dictionary<DuplicateFile, FileRow> _fileRows = new();
    private readonly Dictionary<DuplicateGroup, GroupRow> _groupRows = new();
    private SelectionManager? _selection;
    private CancellationTokenSource? _cts;
    private volatile ScanProgress? _latestScan;
    private volatile DeletionProgress? _latestDelete;
    private int _previewVersion;

    public MainViewModel(PlatformServices platform, SettingsStore store)
    {
        _platform = platform;
        _store = store;
        _settings = store.Load();
        _cache = new HashCache(store.CacheFile);

        ProtectedFolders = new ObservableCollection<string>(_settings.ProtectedFolders);
        SearchLocations = new ObservableCollection<string>(_settings.SearchLocations);
        ExcludedSubfolders = new ObservableCollection<string>(_settings.ExcludedSubfolders);

        _excludeSystemFolders = _settings.ExcludeSystemFolders;
        _includeProtectedInScan = _settings.IncludeProtectedInScan;
        _hideProtectedInResults = _settings.HideProtectedInResults;
        _keepOneOnFolderSelect = _settings.KeepOneOnFolderSelect;
        _presetImages = _settings.EnabledPresets.Contains("Imagini");
        _presetVideo = _settings.EnabledPresets.Contains("Video");
        _presetAudio = _settings.EnabledPresets.Contains("Audio");
        _presetDocuments = _settings.EnabledPresets.Contains("Documente");
        _allFiles = _settings.AllFiles;
        _customExtensions = _settings.CustomExtensions;
        _minSizeKb = _settings.MinSizeBytes <= 1 ? 0 : _settings.MinSizeBytes / 1024m;
        _maxParallelism = _settings.MaxParallelism;
        _deleteModeIndex = (int)_settings.DeleteMode;
        _quarantineFolder = _settings.QuarantineFolder;
        _settingsError = store.LoadError;

        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _progressTimer.Tick += (_, _) => ShowProgress();

        RefreshProtectionStatus();
    }

    public IUserDialogs Dialogs { get; set; } = null!;

    public ObservableCollection<string> ProtectedFolders { get; }
    public ObservableCollection<string> SearchLocations { get; }
    public ObservableCollection<string> ExcludedSubfolders { get; }
    public ObservableCollection<ScanIssue> Issues { get; } = new();
    public ObservableCollection<string> OperationLines { get; } = new();

    [ObservableProperty] private string? _selectedProtected;
    [ObservableProperty] private string? _selectedLocation;
    [ObservableProperty] private string? _selectedExcluded;

    [ObservableProperty] private bool _excludeSystemFolders;
    [ObservableProperty] private bool _includeProtectedInScan;
    [ObservableProperty] private bool _presetImages;
    [ObservableProperty] private bool _presetVideo;
    [ObservableProperty] private bool _presetAudio;
    [ObservableProperty] private bool _presetDocuments;
    [ObservableProperty] private bool _allFiles;
    [ObservableProperty] private string _customExtensions = "";
    [ObservableProperty] private decimal? _minSizeKb;
    [ObservableProperty] private decimal? _maxParallelism;

    [ObservableProperty] private string? _missingProtectedText;
    [ObservableProperty] private string? _settingsError;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private string _progressText = "Gata.";
    [ObservableProperty] private string _currentFile = "";
    [ObservableProperty] private int _activeTab;

    [ObservableProperty] private ObservableCollection<RowBase> _rows = new();
    [ObservableProperty] private RowBase? _selectedRow;
    [ObservableProperty] private string _summaryText = "Nicio scanare efectuată.";
    [ObservableProperty] private string _selectionText = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private bool _hideProtectedInResults;
    [ObservableProperty] private bool _keepOneOnFolderSelect;
    [ObservableProperty] private int _sortIndex;
    [ObservableProperty] private int _ruleIndex;
    [ObservableProperty] private string _folderForSelection = "";
    [ObservableProperty] private Bitmap? _preview;
    [ObservableProperty] private string _previewNote = "";

    [ObservableProperty] private int _deleteModeIndex;
    [ObservableProperty] private string _quarantineFolder = "";
    [ObservableProperty] private bool _dryRun;
    [ObservableProperty] private bool _showElevate;
    [ObservableProperty] private string _issuesSummary = "";

    public bool IsQuarantine => DeleteModeIndex == (int)DeleteMode.Quarantine;
    public bool HasResults => _groups.Count > 0;
    public string? SelectedPath => (SelectedRow as FileRow)?.Path;
    public bool HasSelectedFile => SelectedRow is FileRow;
    public string WindowTitle => _platform.Elevation.IsElevated ? "Dubluri (administrator)" : "Dubluri";

    partial void OnDeleteModeIndexChanged(int value) => OnPropertyChanged(nameof(IsQuarantine));
    partial void OnFilterTextChanged(string value) => RebuildRows();
    partial void OnSortIndexChanged(int value) => RebuildRows();
    partial void OnHideProtectedInResultsChanged(bool value) => RebuildRows();

    partial void OnSelectedRowChanged(RowBase? value)
    {
        OnPropertyChanged(nameof(SelectedPath));
        OnPropertyChanged(nameof(HasSelectedFile));
        _ = LoadPreviewAsync(value as FileRow);
    }

    // ------------------------------------------------------------------ setari

    public void SaveSettings()
    {
        var presets = new List<string>();
        if (PresetImages) presets.Add("Imagini");
        if (PresetVideo) presets.Add("Video");
        if (PresetAudio) presets.Add("Audio");
        if (PresetDocuments) presets.Add("Documente");

        decimal kb = MinSizeKb ?? 0;
        _settings = new AppSettings
        {
            ProtectedFolders = ProtectedFolders.ToList(),
            SearchLocations = SearchLocations.ToList(),
            ExcludedSubfolders = ExcludedSubfolders.ToList(),
            ExcludeSystemFolders = ExcludeSystemFolders,
            IncludeProtectedInScan = IncludeProtectedInScan,
            HideProtectedInResults = HideProtectedInResults,
            KeepOneOnFolderSelect = KeepOneOnFolderSelect,
            EnabledPresets = presets,
            AllFiles = AllFiles,
            CustomExtensions = CustomExtensions ?? "",
            MinSizeBytes = kb <= 0 ? 1 : (long)(kb * 1024),
            MaxParallelism = (int)Math.Clamp(MaxParallelism ?? 2, 1, 16),
            DeleteMode = (DeleteMode)DeleteModeIndex,
            QuarantineFolder = QuarantineFolder ?? "",
        };
        // setari stricate la pornire: nu suprascriem fisierul pana nu confirma utilizatorul
        if (_store.LoadError != null) return;
        try { _store.Save(_settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StatusMessage = "Nu pot salva setările: " + ex.Message; }
    }

    private ProtectionGuard NewGuard() => new(_platform.Paths, _platform.Identity, ProtectedFolders);

    private void RefreshProtectionStatus()
    {
        var missing = NewGuard().MissingFolders;
        MissingProtectedText = missing.Count == 0 ? null
            : "Foldere protejate inaccesibile: " + string.Join(", ", missing) + ". Ștergerile sunt blocate până când le reconectezi sau le scoți din listă.";
    }

    [RelayCommand]
    private void RecheckProtected() => RefreshProtectionStatus();

    [RelayCommand]
    private void AcknowledgeSettingsError()
    {
        _store.AcknowledgeLoadError();
        SettingsError = null;
        SaveSettings();
    }

    /// <summary>Marcajele de protectie din rezultate nu mai sunt valabile dupa schimbarea listei.</summary>
    private void ProtectedListChanged()
    {
        SaveSettings();
        RefreshProtectionStatus();
        if (_groups.Count > 0)
        {
            SetGroups(new List<DuplicateGroup>());
            SummaryText = "Lista folderelor protejate s-a schimbat. Scanează din nou.";
        }
    }

    [RelayCommand]
    private async Task AddProtected()
    {
        string? folder = await Dialogs.PickFolderAsync("Alege un folder protejat");
        if (folder == null || ProtectedFolders.Contains(folder, _platform.Paths.Comparer)) return;
        ProtectedFolders.Add(folder);
        ProtectedListChanged();
    }

    [RelayCommand]
    private async Task RemoveProtected()
    {
        if (SelectedProtected is not { } folder) return;
        if (!await Dialogs.ConfirmAsync("Elimină protecția", $"Folderul nu va mai fi protejat:\n\n{folder}", "Elimină protecția", "Renunță", danger: true)) return;
        ProtectedFolders.Remove(folder);
        ProtectedListChanged();
    }

    [RelayCommand]
    private async Task AddLocation()
    {
        string? folder = await Dialogs.PickFolderAsync("Alege o locație de căutare");
        if (folder == null || SearchLocations.Contains(folder, _platform.Paths.Comparer)) return;
        SearchLocations.Add(folder);
        SaveSettings();
    }

    [RelayCommand]
    private void RemoveLocation()
    {
        if (SelectedLocation is { } folder) SearchLocations.Remove(folder);
        SaveSettings();
    }

    [RelayCommand]
    private void MoveLocationUp() => MoveLocation(-1);

    [RelayCommand]
    private void MoveLocationDown() => MoveLocation(1);

    private void MoveLocation(int delta)
    {
        if (SelectedLocation is not { } folder) return;
        int index = SearchLocations.IndexOf(folder), target = index + delta;
        if (index < 0 || target < 0 || target >= SearchLocations.Count) return;
        SearchLocations.Move(index, target);
        SelectedLocation = folder;
        SaveSettings();
    }

    [RelayCommand]
    private async Task AddExcluded()
    {
        string? folder = await Dialogs.PickFolderAsync("Alege un subfolder de exclus");
        if (folder == null || ExcludedSubfolders.Contains(folder, _platform.Paths.Comparer)) return;
        ExcludedSubfolders.Add(folder);
        SaveSettings();
    }

    [RelayCommand]
    private void RemoveExcluded()
    {
        if (SelectedExcluded is { } folder) ExcludedSubfolders.Remove(folder);
        SaveSettings();
    }

    [RelayCommand]
    private void ClearCache()
    {
        _cache.Clear();
        StatusMessage = "Cache-ul de hash-uri a fost golit.";
        ProgressText = StatusMessage;
    }

    // ------------------------------------------------------------------ scanare

    [RelayCommand]
    private async Task StartScan()
    {
        if (IsBusy) return;
        SaveSettings();
        if (SearchLocations.Count == 0 && !(IncludeProtectedInScan && ProtectedFolders.Count > 0))
        {
            await Dialogs.ShowAsync("Lipsesc locații", "Adaugă cel puțin o locație de căutare.");
            return;
        }
        if (!AllFiles && !PresetImages && !PresetVideo && !PresetAudio && !PresetDocuments && !FileFilter.ParseCustom(CustomExtensions).Any())
        {
            await Dialogs.ShowAsync("Lipsește filtrul", "Bifează cel puțin un tip de fișier sau „Toate fișierele”.");
            return;
        }

        RefreshProtectionStatus();
        Issues.Clear();
        ShowElevate = false;
        IssuesSummary = "";
        SetGroups(new List<DuplicateGroup>());
        BeginWork("Se caută fișierele...");

        var settings = _settings;
        var guard = NewGuard();
        var service = new ScanService(_platform.Paths, _platform.Identity, _platform.SystemFolders);
        var token = _cts!.Token;
        var progress = new LatestProgress<ScanProgress>(p => _latestScan = p);
        try
        {
            var result = await Task.Run(() => service.Run(settings, guard, _cache, progress, token));
            await Task.Run(_cache.Save);
            SetGroups(result.Groups);
            foreach (var issue in result.Issues) Issues.Add(issue);
            ShowIssuesSummary();
            long recoverable = _groups.Sum(g => g.RecoverableBytes);
            ProgressText = $"Scanare terminată: {result.FilesScanned} fișiere, {_groups.Count} grupuri de duplicate, recuperabil {Format.Size(recoverable)}.";
            ActiveTab = 1;
        }
        catch (OperationCanceledException)
        {
            ProgressText = "Scanare anulată.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ProgressText = "Scanarea s-a oprit: " + ex.Message;
        }
        finally
        {
            EndWork();
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private void BeginWork(string text)
    {
        _cts = new CancellationTokenSource();
        _latestScan = null;
        _latestDelete = null;
        IsBusy = true;
        IsIndeterminate = true;
        ProgressValue = 0;
        ProgressText = text;
        CurrentFile = "";
        _progressTimer.Start();
    }

    private void EndWork()
    {
        _progressTimer.Stop();
        IsBusy = false;
        IsIndeterminate = false;
        CurrentFile = "";
        _cts?.Dispose();
        _cts = null;
    }

    private void ShowProgress()
    {
        if (_latestDelete is { } d)
        {
            IsIndeterminate = false;
            ProgressValue = d.Total == 0 ? 0 : 100.0 * d.Done / d.Total;
            ProgressText = $"Se procesează {d.Done} din {d.Total}...";
            CurrentFile = d.CurrentPath;
            return;
        }
        if (_latestScan is not { } s) return;
        CurrentFile = s.CurrentPath ?? "";
        switch (s.Stage)
        {
            case ScanStage.Enumerating:
                IsIndeterminate = true;
                ProgressText = $"Se caută fișierele... {s.Done} găsite";
                break;
            case ScanStage.PartialHash:
            case ScanStage.FullHash:
                IsIndeterminate = false;
                ProgressValue = s.Total == 0 ? 0 : 100.0 * s.Done / s.Total;
                ProgressText = (s.Stage == ScanStage.PartialHash ? "Comparație rapidă" : "Comparație completă (SHA-256)") + $": {s.Done} din {s.Total}";
                break;
            default:
                IsIndeterminate = true;
                ProgressText = "Se pregătesc rezultatele...";
                break;
        }
    }

    private void ShowIssuesSummary()
    {
        int denied = Issues.Count(i => i.IsAccessDenied);
        IssuesSummary = Issues.Count == 0 ? "Nu au fost erori." : $"{Issues.Count} elemente sărite, dintre care {denied} cu acces refuzat.";
        ShowElevate = denied > 0 && !_platform.Elevation.IsElevated;
    }

    [RelayCommand]
    private async Task RestartElevated()
    {
        if (!await Dialogs.ConfirmAsync("Repornește ca administrator", "Aplicația se închide și pornește din nou cu drepturi de administrator. Rezultatele curente se pierd, dar rescanarea va fi rapidă datorită cache-ului.", "Repornește", "Renunță", danger: false))
            return;
        SaveSettings();
        if (_platform.Elevation.RestartElevated(_store.Directory))
            (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    // ------------------------------------------------------------------ rezultate

    private void SetGroups(List<DuplicateGroup> groups)
    {
        _groups = groups;
        _fileRows.Clear();
        _groupRows.Clear();
        for (int i = 0; i < groups.Count; i++)
        {
            _groupRows[groups[i]] = new GroupRow(groups[i], i + 1);
            foreach (var file in groups[i].Files) _fileRows[file] = new FileRow(file, this);
        }
        _selection = new SelectionManager(_groups, _platform.Paths);
        OnPropertyChanged(nameof(HasResults));
        RebuildRows();
        UpdateSummary();
    }

    private void RebuildRows()
    {
        IEnumerable<DuplicateGroup> query = _groups;
        string text = (FilterText ?? "").Trim();
        if (text.Length > 0)
            query = query.Where(g => VisibleFiles(g).Any(f => f.Path.Contains(text, StringComparison.OrdinalIgnoreCase)));

        query = SortIndex switch
        {
            1 => query.OrderByDescending(g => g.Files.Count).ThenByDescending(g => g.RecoverableBytes),
            2 => query.OrderBy(g => System.IO.Path.GetDirectoryName(g.Files[0].Path), _platform.Paths.Comparer),
            _ => query.OrderByDescending(g => g.RecoverableBytes),
        };

        var rows = new List<RowBase>();
        foreach (var group in query)
        {
            // grup doar cu copii protejate: nu are nimic de eliminat, deci dispare cand ele sunt ascunse
            var visible = VisibleFiles(group).ToList();
            if (visible.Count == 0) continue;
            rows.Add(_groupRows[group]);
            rows.AddRange(visible.Select(f => _fileRows[f]));
        }
        Rows = new ObservableCollection<RowBase>(rows);
    }

    private IEnumerable<DuplicateFile> VisibleFiles(DuplicateGroup group) =>
        HideProtectedInResults ? group.Files.Where(f => !f.IsProtected) : group.Files;

    private void UpdateSummary()
    {
        if (_groups.Count == 0)
        {
            SummaryText = "Nu există rezultate.";
            SelectionText = "";
            return;
        }
        SummaryText = $"{_groups.Count} grupuri  ·  total recuperabil {Format.Size(_groups.Sum(g => g.RecoverableBytes))}";
        var selected = _groups.SelectMany(g => g.Files).Where(f => f.IsSelected).ToList();
        SelectionText = $"Selectate: {selected.Count} fișiere  ·  {Format.Size(selected.Sum(f => f.Size))}";
    }

    public void RequestSelection(FileRow row, bool selected)
    {
        if (_selection == null) return;
        if (_selection.TrySetSelected(row.File, selected, out string? reason))
        {
            StatusMessage = "";
            UpdateSummary();
            return;
        }
        StatusMessage = reason ?? "";
        // bifa a fost deja schimbata de control: o readucem dupa terminarea evenimentului
        Dispatcher.UIThread.Post(row.RefreshSelection);
    }

    [RelayCommand]
    private void ApplyRule()
    {
        if (_selection == null) return;
        var rule = (KeepRule)RuleIndex;
        _selection.ApplyRule(rule, SearchLocations.ToList());
        StatusMessage = "Auto-selecție aplicată. Copiile din folderele protejate sunt întotdeauna păstrate.";
        UpdateSummary();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        _selection?.ClearAll();
        StatusMessage = "";
        UpdateSummary();
    }

    [RelayCommand]
    private async Task PickSelectionFolder()
    {
        string? folder = await Dialogs.PickFolderAsync("Alege folderul");
        if (folder != null) FolderForSelection = folder;
    }

    [RelayCommand]
    private void UseFolderOfSelectedFile()
    {
        if (SelectedPath is { } path) FolderForSelection = System.IO.Path.GetDirectoryName(path) ?? "";
    }

    [RelayCommand]
    private void SelectFolder()
    {
        if (_selection == null || string.IsNullOrWhiteSpace(FolderForSelection)) return;
        var result = _selection.SelectFolder(FolderForSelection, KeepOneOnFolderSelect ? (KeepRule)RuleIndex : null, SearchLocations.ToList());
        string message = $"Selectate {result.Changed} fișiere din folder.";
        if (result.GroupsKeptOne > 0)
            message += $" În {result.GroupsKeptOne} grupuri cu toate copiile în acest folder s-a păstrat câte una, după regula de auto-selecție aleasă.";
        if (result.GroupsBlockedLastCopy > 0)
            message += $" {result.GroupsBlockedLastCopy} grupuri au fost lăsate neschimbate: selecția ar fi cuprins toate copiile lor, iar în fiecare grup trebuie să rămână cel puțin una.";
        if (result.ProtectedSkipped > 0)
            message += $" {result.ProtectedSkipped} fișiere protejate au fost sărite.";
        StatusMessage = message;
        UpdateSummary();
    }

    [RelayCommand]
    private void DeselectFolder()
    {
        if (_selection == null || string.IsNullOrWhiteSpace(FolderForSelection)) return;
        StatusMessage = $"Deselectate {_selection.DeselectFolder(FolderForSelection)} fișiere din folder.";
        UpdateSummary();
    }

    [RelayCommand]
    private void RevealSelected()
    {
        if (SelectedPath is not { } path) return;
        try { _platform.Shell.RevealInFileManager(path); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            StatusMessage = "Nu pot deschide locația: " + ex.Message;
        }
    }

    private async Task LoadPreviewAsync(FileRow? row)
    {
        int version = ++_previewVersion;
        var old = Preview;
        Preview = null;
        old?.Dispose();
        PreviewNote = "";
        if (row == null) return;

        if (!Previewable.Contains(System.IO.Path.GetExtension(row.Path)))
        {
            PreviewNote = "Fără previzualizare pentru acest tip de fișier.";
            return;
        }
        if (row.File.Size > MaxPreviewBytes)
        {
            PreviewNote = "Fișierul este prea mare pentru previzualizare.";
            return;
        }
        try
        {
            string path = row.Path;
            var bitmap = await Task.Run(() =>
            {
                using var stream = File.OpenRead(path);
                return Bitmap.DecodeToWidth(stream, 420);
            });
            if (version == _previewVersion) Preview = bitmap;
            else bitmap.Dispose();
        }
        catch (Exception)
        {
            if (version == _previewVersion) PreviewNote = "Imaginea nu poate fi afișată.";
        }
    }

    [RelayCommand]
    private async Task ExportReport()
    {
        if (_groups.Count == 0) return;
        string? file = await Dialogs.PickSaveFileAsync("Exportă raportul", "raport-duplicate.csv");
        if (file == null) return;
        try
        {
            var groups = _groups;
            await Task.Run(() => ReportExporter.Write(groups, file));
            StatusMessage = "Raport salvat: " + file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dialogs.ShowAsync("Export eșuat", ex.Message);
        }
    }

    // ------------------------------------------------------------------ stergere

    [RelayCommand]
    private async Task ExecuteDeletion()
    {
        if (IsBusy) return;
        SaveSettings();

        var selected = _groups.SelectMany(g => g.Files).Where(f => f.IsSelected).ToList();
        if (selected.Count == 0)
        {
            await Dialogs.ShowAsync("Nimic selectat", "Selectează întâi fișierele de eliminat.");
            return;
        }
        if (SettingsError != null)
        {
            await Dialogs.ShowAsync("Ștergeri blocate", "Setările nu au putut fi citite, deci lista folderelor protejate este necunoscută. Clarifică situația din bannerul de sus.");
            return;
        }
        RefreshProtectionStatus();
        if (MissingProtectedText != null)
        {
            await Dialogs.ShowAsync("Ștergeri blocate", MissingProtectedText);
            return;
        }

        var mode = (DeleteMode)DeleteModeIndex;
        if (mode == DeleteMode.Quarantine && string.IsNullOrWhiteSpace(QuarantineFolder))
        {
            await Dialogs.ShowAsync("Lipsește folderul", "Alege folderul de carantină.");
            return;
        }

        if (!DryRun)
        {
            string where = mode switch
            {
                DeleteMode.RecycleBin => "mutate în Recycle Bin",
                DeleteMode.Quarantine => "mutate în carantină:\n" + QuarantineFolder,
                _ => "ȘTERSE DEFINITIV",
            };
            if (!await Dialogs.ConfirmAsync("Confirmare", $"{selected.Count} fișiere ({Format.Size(selected.Sum(f => f.Size))}) vor fi {where}.", "Continuă", "Renunță", danger: mode == DeleteMode.Permanent))
                return;
            if (mode == DeleteMode.Permanent
                && !await Dialogs.ConfirmAsync("Atenție: ștergere definitivă", "Fișierele NU vor putea fi recuperate din Recycle Bin. Ești sigur?", "Da, șterge definitiv", "Renunță", danger: true))
                return;
        }

        string logFile = System.IO.Path.Combine(_store.LogDirectory, $"operatii-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        var request = new DeletionRequest(_groups, mode, QuarantineFolder, DryRun);
        var guard = NewGuard();

        BeginWork(DryRun ? "Simulare..." : "Se procesează...");
        var token = _cts!.Token;
        var progress = new LatestProgress<DeletionProgress>(p => _latestDelete = p);
        try
        {
            var report = await Task.Run(() =>
            {
                using var log = new OperationLog(logFile);
                return new DeletionService(guard, _platform.Trash, _platform.Identity, _platform.Paths, log).Execute(request, progress, token);
            });
            ApplyReport(report, logFile);
        }
        catch (OperationCanceledException)
        {
            ProgressText = "Operație anulată. Fișierele procesate până acum sunt în jurnal: " + logFile;
            await RescanHintAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ProgressText = "Operația s-a oprit: " + ex.Message;
        }
        finally
        {
            EndWork();
        }
    }

    private Task RescanHintAsync() => Dialogs.ShowAsync("Operație anulată", "Unele fișiere pot fi deja procesate. Scanează din nou pentru rezultate actuale.");

    private void ApplyReport(DeletionReport report, string logFile)
    {
        OperationLines.Clear();
        foreach (var op in report.Operations)
            OperationLines.Add($"{Label(op.Result)}  {op.File.Path}  –  {op.Message}");

        int done = report.Count(OperationResult.Done), simulated = report.Count(OperationResult.Simulated),
            refused = report.Count(OperationResult.Refused), failed = report.Count(OperationResult.Failed);

        if (done > 0)
        {
            foreach (var op in report.Operations.Where(o => o.Result == OperationResult.Done))
                op.File.Group.Files.Remove(op.File);
            var remaining = _groups.Where(g => g.Files.Count > 1).ToList();
            SetGroups(remaining);
        }

        ProgressText = DryRun
            ? $"Simulare: {simulated} fișiere ar fi procesate, {refused} refuzate. Nu s-a modificat nimic."
            : $"Reușite: {done} ({Format.Size(report.FreedBytes)})  ·  refuzate: {refused}  ·  erori: {failed}";
        StatusMessage = "Jurnal: " + logFile;
        UpdateSummary();
        if (refused + failed > 0 || DryRun) ActiveTab = 2;
    }

    private static string Label(OperationResult r) => r switch
    {
        OperationResult.Done => "[reușit]",
        OperationResult.Simulated => "[simulat]",
        OperationResult.Refused => "[refuzat]",
        _ => "[eroare]",
    };

    [RelayCommand]
    private async Task PickQuarantine()
    {
        string? folder = await Dialogs.PickFolderAsync("Alege folderul de carantină");
        if (folder != null) QuarantineFolder = folder;
    }

    /// <summary>Retine doar ultima valoare; interfata o afiseaza periodic, ca sa nu fie inundata.</summary>
    private sealed class LatestProgress<T> : IProgress<T>
    {
        private readonly Action<T> _store;
        public LatestProgress(Action<T> store) => _store = store;
        public void Report(T value) => _store(value);
    }
}
