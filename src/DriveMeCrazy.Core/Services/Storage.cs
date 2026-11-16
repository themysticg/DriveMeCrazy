using System.Text.Json;
using DriveMeCrazy.Core.Models;

namespace DriveMeCrazy.Core.Services;

public static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DriveMeCrazy");

    public static string LogsDir => Path.Combine(DataDir, "logs");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string HistoryFile => Path.Combine(DataDir, "history.json");
}

public static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.LogsDir);
                File.AppendAllText(Path.Combine(AppPaths.LogsDir, $"{DateTime.Now:yyyy-MM-dd}.log"),
                    $"{DateTime.Now:HH:mm:ss} {level} {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never crash the app */ }
    }
}

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static T Load<T>(string path, Func<T> fallback)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? fallback() : fallback();
        }
        catch (Exception ex)
        {
            Log.Error($"Could not read {path}", ex);
            return fallback();
        }
    }

    public static void Save<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(value, Options));
        }
        catch (Exception ex) { Log.Error($"Could not write {path}", ex); }
    }
}

public sealed class SettingsService
{
    public AppSettings Load() => Json.Load(AppPaths.SettingsFile, () => new AppSettings());
    public void Save(AppSettings settings) => Json.Save(AppPaths.SettingsFile, settings);
}

public sealed class HistoryService
{
    private const int MaxEntries = 500;

    public List<HistoryEntry> Load() => Json.Load(AppPaths.HistoryFile, () => new List<HistoryEntry>());

    public void Save(IEnumerable<HistoryEntry> entries) =>
        Json.Save(AppPaths.HistoryFile, entries.OrderByDescending(e => e.Timestamp).Take(MaxEntries).ToList());
}
