using System.Text.Json;

namespace SmartBar.Infrastructure;

/// <summary>Da che parte dello schermo principale sta la barra, con lo spazio delle finestre aperte.</summary>
internal enum BarSide
{
    Left,
    Right,
}

/// <summary>
/// Le scelte dell'utente, tenute nella cartella dati (settings.json) per ritrovarle al riavvio.
/// Il lato della barra si cambia dal menu dell'icona accanto all'orologio.
/// </summary>
internal static class Settings
{
    private sealed record Saved(string? Side);

    private static readonly object Gate = new();
    private static BarSide? _side;

    private static string FilePath => Path.Combine(Log.Folder, "settings.json");

    /// <summary>
    /// Da che parte sta la barra. Di default a sinistra: con un secondo schermo a destra del principale e' l'unico
    /// bordo libero, e le finestre parcheggiate scorrono dentro di lato (l'utente l'ha chiesto il 2026-11-25).
    /// </summary>
    public static BarSide Side
    {
        get
        {
            lock (Gate)
            {
                _side ??= Read();
                return _side.Value;
            }
        }
    }

    public static void Set(BarSide side)
    {
        lock (Gate)
        {
            if (_side == side)
            {
                return;
            }

            _side = side;
            Write(side);
        }
    }

    private static BarSide Read()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(FilePath)) is { Side: { } text }
                && Enum.TryParse(text, ignoreCase: true, out BarSide side) && Enum.IsDefined(side))
            {
                return side;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Impostazioni illeggibili, valgono quelle di default: {ex.Message}");
        }

        return BarSide.Left;
    }

    private static void Write(BarSide side)
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new Saved(side.ToString())));
        }
        catch (Exception ex)
        {
            Log.Warn($"Impostazioni non salvate: {ex.Message}");
        }
    }
}
