using SmartBar.Core;
using SmartBar.Infrastructure;
using SmartBar.Native;

namespace SmartBar;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // "--test" (istanza separata per le prove automatiche) e "--only" (solo certi processi)
        if (!InstanceCommands.TryReadOptions(args))
        {
            return 2;
        }

        Log.Init();

        // un comando lanciato mentre l'app e' aperta lo esegue l'istanza gia' in esecuzione;
        // se non c'e', il recupero delle finestre parcheggiate lo fa da solo
        if (InstanceCommands.TryHandle(args))
        {
            return 0;
        }

        using var instance = new Mutex(true, Runtime.MutexName, out bool first);
        if (!first)
        {
            Log.Info("SmartBar è già in esecuzione: la nuova istanza esce.");
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("Errore non gestito nell'interfaccia", e.Exception);
        Log.Info($"Avvio SmartBar: .NET {Environment.Version}, {Win32.GetDpiForSystem()} dpi di sistema");

        AppContext context;
        try
        {
            context = new AppContext();
        }
        catch (Exception ex)
        {
            Log.Error("Impossibile avviare l'app", ex);
            return 1;
        }

        using (context)
        {
            try
            {
                Application.Run(context);
                return 0;
            }
            catch (Exception ex)
            {
                // le finestre parcheggiate sono oltre il bordo: non si devono perdere con l'app
                Log.Error("Errore irreversibile: ripristino le finestre parcheggiate ed esco.", ex);
                context.EmergencyRestore();
                return 1;
            }
        }
    }
}
