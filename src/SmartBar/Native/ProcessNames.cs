using System.Runtime.InteropServices;
using System.Text;

namespace SmartBar.Native;

/// <summary>Nome dell'eseguibile di un processo, letto senza pesare: serve per riconoscere le finestre parcheggiate.</summary>
internal static class ProcessNames
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref uint size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// Nome dell'eseguibile senza estensione ("Notepad", "charmap"), o null se non si puo' leggere.
    /// Non usa System.Diagnostics.Process: quello ogni volta rilegge l'elenco di tutti i processi del sistema,
    /// e quando ce ne sono tanti l'avvio arrivava a perdere secondi.
    /// </summary>
    public static string? NameOf(uint processId)
    {
        string? path = PathOf(processId);
        return path is null ? null : Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>Il percorso dell'eseguibile del processo, o null se non si puo' leggere.</summary>
    public static string? PathOf(uint processId)
    {
        if (processId == 0)
        {
            return null;
        }

        IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var text = new StringBuilder(1024);
            uint size = (uint)text.Capacity;
            return QueryFullProcessImageNameW(process, 0, text, ref size) ? text.ToString() : null;
        }
        finally
        {
            _ = CloseHandle(process);
        }
    }
}
