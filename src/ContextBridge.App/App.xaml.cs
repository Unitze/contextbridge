using System.IO;
using System.Windows;
using System.Windows.Threading;
using ContextBridge.App.Services;
using ContextBridge.App.Resources;
using ContextBridge.App.Views;

namespace ContextBridge.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        UiLanguage.Apply(AppPreferences.Current.Language);
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        // Command-line helpers used by the installer and for scripting:
        //   ContextBridge.exe --import <file>   open the import confirmation for a share file
        //   ContextBridge.exe --status          exit code 0 when the package is registered and current
        string? importPath = null;
        for (int i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i] == "--status")
            {
                Shutdown(PackageService.Query().State == PackageState.Registered ? 0 : 1);
                return;
            }
            if (e.Args[i] == "--import" && i + 1 < e.Args.Length) importPath = e.Args[++i];
            else if (File.Exists(e.Args[i]) && e.Args[i].EndsWith(".json", StringComparison.OrdinalIgnoreCase)) importPath = e.Args[i];
        }

        var window = new MainWindow();
        if (!window.Initialize())
        {
            Shutdown();
            return;
        }
        MainWindow = window;
        window.Show();
        if (importPath is not null) window.ViewModel.ImportFile(importPath);
    }

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppDialog.Show(string.Format(Strings.Msg_Unexpected, e.Exception.Message), "ContextBridge",
                        MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
