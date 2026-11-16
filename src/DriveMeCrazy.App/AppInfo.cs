using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using System.Windows;

namespace DriveMeCrazy.App;

public static class AppInfo
{
    public const string RepoUrl = "https://github.com/themysticg/DriveMeCrazy";

    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";

    public static bool IsAdmin { get; } =
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't open {target}:\n{ex.Message}", "DriveMeCrazy", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public static void ApplyTheme(string theme)
    {
        Application.Current.ThemeMode = theme switch
        {
            "Light" => ThemeMode.Light,
            "Dark" => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
    }
}
