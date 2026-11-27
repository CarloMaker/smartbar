namespace SmartBar.Infrastructure;

/// <summary>
/// Nomi e cartelle dell'istanza. Le prove automatiche lanciano l'app con "--test": hanno cosi' mutex, comandi e
/// cartella dati separati, e non mischiano le finestre parcheggiate dell'utente con quelle di prova.
/// Con "--only" l'istanza si occupa solo delle finestre dei processi indicati.
/// </summary>
internal static class Runtime
{
    private static string _suffix = string.Empty;
    private static HashSet<uint>? _onlyProcesses;
    private static bool _reserve;

    public static string MutexName => $@"Local\SmartBar.Instance{_suffix}";

    /// <summary>Vuoto per l'istanza dell'utente, ".Test" per quella delle prove.</summary>
    public static string Suffix => _suffix;

    public static string RestoreAllEvent => $@"Local\SmartBar.Command.RestoreAll{_suffix}";

    public static string ExitEvent => $@"Local\SmartBar.Command.Exit{_suffix}";

    /// <summary>Dove stanno log ed elenco delle finestre parcheggiate.</summary>
    public static string DataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SmartBar" + _suffix);

    public static void UseTestInstance() => _suffix = ".Test";

    /// <summary>
    /// La barra occupa davvero la sua striscia del bordo, che Windows toglie alle finestre. L'istanza di prova lo fa
    /// solo con "--reserve": la striscia viene tolta a tutte le finestre, anche a quelle dell'utente.
    /// </summary>
    public static bool ReservesStrip => _suffix.Length == 0 || _reserve;

    /// <summary>Per l'istanza di prova: la barra occupa la sua striscia del bordo (vedi <see cref="ReservesStrip"/>).</summary>
    public static void ReserveStrip() => _reserve = true;

    /// <summary>Solo le finestre di questi processi ricevono il pulsante e si possono parcheggiare.</summary>
    public static void AllowOnly(IEnumerable<uint> processIds) => _onlyProcesses = processIds.ToHashSet();

    public static bool IsAllowedProcess(uint processId) => _onlyProcesses is null || _onlyProcesses.Contains(processId);
}
