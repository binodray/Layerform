using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Compositor.App;

public partial class App : Application
{
    private const string InstanceKey = "LayerForm.PrimaryInstance";
    private MainWindow? window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            Diagnostics.Log("Unhandled: " + e.Exception);
            e.Handled = true;
            window?.ShowError("Something went wrong", e.Exception.Message);
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var current = AppInstance.GetCurrent();
        var primary = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!primary.IsCurrent)
        {
            await primary.RedirectActivationToAsync(current.GetActivatedEventArgs());
            Exit();
            return;
        }

        primary.Activated += (_, _) =>
        {
            var existing = window;
            if (existing == null) return;
            existing.DispatcherQueue.TryEnqueue(existing.ActivateExistingInstance);
        };

        var files = Environment.GetCommandLineArgs().Skip(1).Where(a => !a.StartsWith("--")).ToArray();
        window = new MainWindow(files);
        window.Activate();
    }
}

internal static class Diagnostics
{
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "LayerForm.log");
    public static void Log(string message)
    {
        try { File.AppendAllText(LogPath, $"{DateTime.Now:O} {message}{Environment.NewLine}"); } catch { }
    }
}
