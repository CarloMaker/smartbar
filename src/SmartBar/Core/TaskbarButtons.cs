using System.Runtime.InteropServices;
using SmartBar.Infrastructure;
using SmartBar.Native;

namespace SmartBar.Core;

/// <summary>Toglie e rimette il pulsante di una finestra nella barra delle applicazioni.</summary>
internal sealed class TaskbarButtons : IDisposable
{
    private ITaskbarList? _list;

    public TaskbarButtons()
    {
        try
        {
            var list = (ITaskbarList)new TaskbarListClass();
            list.HrInit();
            _list = list;
        }
        catch (Exception ex)
        {
            Log.Warn($"Barra delle applicazioni non disponibile: {ex.Message}");
        }
    }

    public void Hide(IntPtr hwnd) => Call(list => list.DeleteTab(hwnd));

    public void Show(IntPtr hwnd) => Call(list => list.AddTab(hwnd));

    private void Call(Action<ITaskbarList> action)
    {
        if (_list is null)
        {
            return;
        }

        try
        {
            action(_list);
        }
        catch (Exception ex)
        {
            Log.Warn($"Barra delle applicazioni: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_list is null)
        {
            return;
        }

        _ = Marshal.ReleaseComObject(_list);
        _list = null;
    }
}
