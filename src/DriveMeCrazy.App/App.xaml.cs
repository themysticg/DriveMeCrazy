using System.Windows;
using System.Windows.Threading;
using DriveMeCrazy.Core.Services;

namespace DriveMeCrazy.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        Log.Info($"DriveMeCrazy {AppInfo.Version} starting (admin: {AppInfo.IsAdmin})");

        var options = StartupOptions.Parse(e.Args);
        var window = new MainWindow(options);
        MainWindow = window;
        window.Show();
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled error", e.Exception);
        MessageBox.Show($"Something went wrong:\n\n{e.Exception.Message}\n\nDetails were written to the log folder.",
            "DriveMeCrazy", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}

/// <summary>Command-line options. --screenshot DIR renders every page to PNG and exits (used for docs/CI).</summary>
public sealed record StartupOptions(string? ScreenshotDir, bool ScanOnStart, string? Theme, bool VerifyCatalog = false)
{
    public static StartupOptions Parse(string[] args)
    {
        string? shots = null, theme = null;
        bool scan = true, verify = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--screenshot" when i + 1 < args.Length: shots = args[++i]; break;
                case "--theme" when i + 1 < args.Length: theme = args[++i]; break;
                case "--no-scan": scan = false; break;
                case "--verify-catalog": verify = true; break;
            }
        }
        return new StartupOptions(shots, scan, theme, verify);
    }
}
