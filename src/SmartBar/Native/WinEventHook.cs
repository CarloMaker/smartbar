namespace SmartBar.Native;

/// <summary>Un evento di una finestra di un programma qualunque, non nostra.</summary>
internal readonly record struct WinEvent(uint Id, IntPtr Hwnd);

internal static class WinEventIds
{
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A;
    public const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
    public const uint EVENT_OBJECT_SHOW = 0x8002;
    public const uint EVENT_OBJECT_HIDE = 0x8003;
    public const uint EVENT_OBJECT_DESTROY = 0x8007;
    public const uint EVENT_OBJECT_STATECHANGE = 0x800A;
    public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    public const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
    public const uint EVENT_OBJECT_CLOAKED = 0x8017;
    public const uint EVENT_OBJECT_UNCLOAKED = 0x8018;
}

/// <summary>
/// Riceve gli eventi di tutte le finestre del desktop. Le chiamate arrivano sul thread che ha creato l'hook,
/// mentre fa girare il ciclo dei messaggi: qui e' il thread dell'interfaccia.
/// </summary>
internal sealed class WinEventHook : IDisposable
{
    private const uint WINEVENT_SKIPOWNPROCESS = 2;
    private const uint First = WinEventIds.EVENT_SYSTEM_FOREGROUND;
    private const uint Last = WinEventIds.EVENT_OBJECT_UNCLOAKED;

    public delegate void WinEventProc(IntPtr hook, uint id, IntPtr hwnd, int idObject, int idChild, uint threadId, uint time);

    private readonly WinEventProc _callback;
    private IntPtr _hook;

    public event Action<WinEvent>? Raised;

    public WinEventHook()
    {
        _callback = OnWinEvent;
        _hook = Win32.SetWinEventHook(First, Last, IntPtr.Zero, _callback, 0, 0, WINEVENT_SKIPOWNPROCESS);
    }

    public bool IsAlive => _hook != IntPtr.Zero;

    private void OnWinEvent(IntPtr hook, uint id, IntPtr hwnd, int idObject, int idChild, uint threadId, uint time)
    {
        // interessano solo gli eventi della finestra stessa, non dei suoi controlli interni
        if (hwnd == IntPtr.Zero || idObject != 0 || idChild != 0)
        {
            return;
        }

        Raised?.Invoke(new WinEvent(id, hwnd));
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            _ = Win32.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }

        GC.KeepAlive(_callback);
    }
}
