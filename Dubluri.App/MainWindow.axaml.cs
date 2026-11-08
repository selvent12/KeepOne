using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Dubluri.App.ViewModels;

namespace Dubluri.App;

public partial class MainWindow : Window, IUserDialogs
{
    public MainWindow() => InitializeComponent();

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSaveFileAsync(string title, string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = "csv",
            FileTypeChoices = new[] { new FilePickerFileType("CSV") { Patterns = new[] { "*.csv" } } },
        });
        return file?.TryGetLocalPath();
    }

    public Task<bool> ConfirmAsync(string title, string message, string yesText, string noText, bool danger) =>
        BuildDialog(title, message, yesText, noText, danger).ShowDialog<bool>(this);

    public Task ShowAsync(string title, string message) =>
        BuildDialog(title, message, "OK", null, danger: false).ShowDialog<bool>(this);

    private static Window BuildDialog(string title, string message, string yesText, string? noText, bool danger)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var yes = new Button { Content = yesText, Padding = new Thickness(18, 6), IsDefault = noText == null };
        if (danger)
        {
            yes.Background = new SolidColorBrush(Color.Parse("#C62828"));
            yes.Foreground = Brushes.White;
        }
        yes.Click += (_, _) => dialog.Close(true);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        buttons.Children.Add(yes);
        if (noText != null)
        {
            // butonul implicit e cel sigur
            var no = new Button { Content = noText, Padding = new Thickness(18, 6), IsDefault = true, IsCancel = true };
            no.Click += (_, _) => dialog.Close(false);
            buttons.Children.Add(no);
        }

        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 16 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(buttons);
        dialog.Content = panel;
        return dialog;
    }
}
