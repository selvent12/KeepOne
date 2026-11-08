using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Dubluri.Core.Scanning;

namespace Dubluri.App.ViewModels;

public abstract class RowBase : ObservableObject { }

/// <summary>Antetul unui grup de duplicate in lista de rezultate.</summary>
public sealed class GroupRow : RowBase
{
    public GroupRow(DuplicateGroup group, int number)
    {
        Group = group;
        Number = number;
    }

    public DuplicateGroup Group { get; }
    public int Number { get; }
    public string Title => $"Grupul {Number}  ·  {Group.Files.Count} copii  ·  {Format.Size(Group.FileSize)} fiecare  ·  recuperabil {Format.Size(Group.RecoverableBytes)}";
    public bool HasProtected => Group.HasProtectedCopy;
}

/// <summary>Un fisier din rezultate. Selectia trece prin SelectionManager, care poate refuza.</summary>
public sealed class FileRow : RowBase
{
    private readonly DuplicateFile _file;
    private readonly MainViewModel _owner;

    public FileRow(DuplicateFile file, MainViewModel owner)
    {
        _file = file;
        _owner = owner;
        file.PropertyChanged += OnFileChanged;
    }

    public DuplicateFile File => _file;
    public string Path => _file.Path;
    public string SizeText => Format.Size(_file.Size);
    public string DateText => _file.LastWriteUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
    public string Location => _file.Location;
    public bool IsProtected => _file.IsProtected;
    public bool CanSelect => !_file.IsProtected;

    public bool IsSelected
    {
        get => _file.IsSelected;
        set
        {
            if (value != _file.IsSelected) _owner.RequestSelection(this, value);
        }
    }

    public void RefreshSelection() => OnPropertyChanged(nameof(IsSelected));

    private void OnFileChanged(object? sender, PropertyChangedEventArgs e) => RefreshSelection();
}

public static class Format
{
    private static readonly CultureInfo Ro = CultureInfo.GetCultureInfo("ro-RO");

    public static string Size(long bytes)
    {
        string[] units = { "octeți", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} octeți" : value.ToString("0.##", Ro) + " " + units[unit];
    }
}
