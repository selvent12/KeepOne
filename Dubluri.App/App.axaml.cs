using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Dubluri.App.ViewModels;
using Dubluri.Core.Settings;

namespace Dubluri.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var store = new SettingsStore(SettingsDirectoryArgument(desktop.Args));
            var viewModel = new MainViewModel(PlatformServices.Create(), store);
            var window = new MainWindow { DataContext = viewModel };
            viewModel.Dialogs = window;
            desktop.MainWindow = window;
            window.Closing += (_, _) => viewModel.SaveSettings();
        }
        base.OnFrameworkInitializationCompleted();
    }

    // la repornirea ca administrator, folderul de setari se transmite explicit
    private static string? SettingsDirectoryArgument(string[]? args)
    {
        if (args == null) return null;
        int index = Array.IndexOf(args, "--settings-dir");
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
