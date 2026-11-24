namespace SmartBar.Infrastructure;

/// <summary>Log su file nella cartella dati di SmartBar. Non deve mai far cadere l'app.</summary>
internal static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();
    private static string _path = string.Empty;

    public static string Folder => Runtime.DataFolder;

    public static void Init()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            _path = Path.Combine(Folder, "log.txt");
            if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes)
            {
                File.WriteAllText(_path, string.Empty);
            }

            File.AppendAllText(_path, $"--- sessione {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---{Environment.NewLine}");
        }
        catch
        {
            _path = string.Empty;
        }
    }

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}{Environment.NewLine}{exception}");

    private static void Write(string level, string message)
    {
        if (_path.Length == 0)
        {
            return;
        }

        lock (Gate)
        {
            try
            {
                File.AppendAllText(_path, $"{DateTime.Now:HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
            catch
            {
                // un log che non si puo' scrivere viene semplicemente saltato
            }
        }
    }
}
