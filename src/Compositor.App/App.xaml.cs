using Microsoft.UI.Xaml;

namespace Compositor.App;

public partial class App : Application
{
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

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
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
