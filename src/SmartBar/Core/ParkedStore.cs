using System.Text.Json;
using SmartBar.Infrastructure;

namespace SmartBar.Core;

internal sealed record SavedWindow(
    string ProcessName, string Title, int ShowCmd, int Left, int Top, int Right, int Bottom, bool WasTopmost);

/// <summary>Elenco delle finestre parcheggiate, salvato per ritrovarle se l'app si chiude male.</summary>
internal static class ParkedStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static string FilePath => Path.Combine(Log.Folder, "parked.json");

    public static List<SavedWindow> Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<SavedWindow>>(File.ReadAllText(FilePath), Options) ?? new List<SavedWindow>()
                : new List<SavedWindow>();
        }
        catch (Exception ex)
        {
            Log.Warn($"Elenco delle finestre parcheggiate illeggibile: {ex.Message}");
            return new List<SavedWindow>();
        }
    }

    public static void Save(IEnumerable<SavedWindow> windows)
    {
        try
        {
            string path = FilePath;
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(windows, Options));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Elenco delle finestre parcheggiate non salvato: {ex.Message}");
        }
    }

    public static void Clear()
    {
        try
        {
            File.Delete(FilePath);
        }
        catch (Exception ex)
        {
            Log.Warn($"Elenco delle finestre parcheggiate non cancellato: {ex.Message}");
        }
    }
}
