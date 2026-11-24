using System.IO;
using System.IO.Pipes;
using System.Text;

namespace SmartBar.Infrastructure;

/// <summary>
/// Canale di comando dell'istanza: gli altri processi (la riga di comando, le prove automatiche) gli parlano
/// attraverso una pipe con nome. Un comando per riga, una risposta per riga.
/// </summary>
internal static class CommandServer
{
    private static string PipeName => "SmartBar" + Runtime.Suffix;

    /// <summary>Avvia l'ascolto in un thread in background. <paramref name="handle"/> gira sul thread dell'interfaccia.</summary>
    public static void Start(Func<string, string> handle)
    {
        SynchronizationContext context = SynchronizationContext.Current
                                         ?? throw new InvalidOperationException("Serve il contesto di sincronizzazione dell'interfaccia.");
        var thread = new Thread(() => Listen(context, handle))
        {
            IsBackground = true,
            Name = "SmartBar comandi",
        };
        thread.Start();
    }

    private static void Listen(SynchronizationContext context, Func<string, string> handle)
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.None);
                server.WaitForConnection();

                string? line;
                using (var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true))
                {
                    line = reader.ReadLine();
                }

                string reply = "err nessun comando";
                if (!string.IsNullOrWhiteSpace(line))
                {
                    // il comando gira sul thread dell'interfaccia, dove vivono le finestre: qui si aspetta la risposta
                    var done = new ManualResetEventSlim(false);
                    context.Send(_ =>
                    {
                        try
                        {
                            reply = handle(line);
                        }
                        catch (Exception ex)
                        {
                            reply = "err " + ex.Message;
                        }
                        finally
                        {
                            done.Set();
                        }
                    }, null);
                    if (!done.Wait(TimeSpan.FromSeconds(10)))
                    {
                        reply = "err nessuna risposta dall'interfaccia";
                    }
                }

                byte[] bytes = Encoding.UTF8.GetBytes(reply + "\n");
                server.Write(bytes, 0, bytes.Length);
                server.Flush();
            }
            catch (Exception ex)
            {
                Log.Warn($"Comando non gestito: {ex.Message}");
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>Manda un comando all'istanza in esecuzione. Null se non c'e' nessuno che risponde.</summary>
    public static string? Send(string command)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.None);
            client.Connect(500);
            byte[] request = Encoding.UTF8.GetBytes(command + "\n");
            client.Write(request, 0, request.Length);
            client.Flush();
            using var reader = new StreamReader(client, Encoding.UTF8, false, 1024, leaveOpen: true);
            return reader.ReadLine();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            return null;
        }
    }
}
