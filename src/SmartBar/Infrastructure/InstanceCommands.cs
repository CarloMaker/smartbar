using SmartBar.Core;

namespace SmartBar.Infrastructure;

/// <summary>
/// Comandi per l'istanza in esecuzione, passati lanciando SmartBar.exe con un argomento:
///   --restore-orphans, --restore-all   rimette a schermo tutte le finestre parcheggiate
///   --exit                             idem, poi chiude l'app
///   --park=HWND, --open=HWND, --pin=HWND, --release=HWND, --collapse-all, --freeze=on|off
///                                      comandi per le prove automatiche
/// "--restore-orphans" funziona anche senza l'app in esecuzione.
/// </summary>
internal static class InstanceCommands
{
    /// <summary>Vero se gli argomenti erano un comando e è stato eseguito (in un modo o nell'altro).</summary>
    public static bool TryHandle(string[] args)
    {
        string? command = Parse(args);
        if (command is null)
        {
            return false;
        }

        // l'app è aperta: se ne occupa lei. Se sta ancora partendo il suo canale compare tra un attimo,
        // e nel frattempo questo processo non deve mettersi ad agire per conto suo
        bool running = Mutex.TryOpenExisting(Runtime.MutexName, out Mutex? opened);
        opened?.Dispose();

        for (int attempt = 0; ; attempt++)
        {
            if (CommandServer.Send(command) is not null)
            {
                return true;
            }

            if (!running || attempt >= 50)
            {
                break;
            }

            Thread.Sleep(200);
        }

        // nessuna istanza: il recupero delle finestre parcheggiate lo fa da solo
        if (command.StartsWith("restore-all", StringComparison.Ordinal))
        {
            int restored = ParkedOrphanRecovery.Run();
            Log.Info($"Recupero da riga di comando: {restored} finestre riportate a schermo");
        }

        return true;
    }

    /// <summary>Legge "--test" e "--only pid,pid". Falso se l'elenco dei pid non si capisce.</summary>
    public static bool TryReadOptions(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--test")
            {
                Runtime.UseTestInstance();
                continue;
            }

            if (!args[i].StartsWith("--only", StringComparison.Ordinal))
            {
                continue;
            }

            string list = args[i].Length > "--only".Length ? args[i]["--only=".Length..]
                         : i + 1 < args.Length ? args[++i]
                         : string.Empty;
            var processIds = new List<uint>();
            foreach (string item in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!uint.TryParse(item, out uint processId))
                {
                    return false;
                }

                processIds.Add(processId);
            }

            Runtime.AllowOnly(processIds);
        }

        return true;
    }

    /// <summary>Dagli argomenti alla riga di comando da mandare all'istanza. Null se non sono un comando.</summary>
    private static string? Parse(string[] args)
    {
        foreach (string arg in args)
        {
            int equal = arg.IndexOf('=');
            string name = equal < 0 ? arg : arg[..equal];
            string value = equal < 0 ? string.Empty : arg[(equal + 1)..];

            switch (name)
            {
                case "--restore-orphans":
                case "--restore-all":
                    return "restore-all";
                case "--exit":
                    return "exit";
                case "--park":
                case "--open":
                case "--pin":
                case "--release":
                    return name[2..] + " " + value;
                case "--collapse-all":
                    return "collapse-all";
                case "--freeze":
                    return "freeze " + value;
            }
        }

        return null;
    }
}
