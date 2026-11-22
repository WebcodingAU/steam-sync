using System.Runtime.InteropServices;

namespace SteamSync;

/// <summary>
/// System-wide keyboard shortcuts (Shift+F1 opens YouTube, Shift+F2 Twitch), through RegisterHotKey on a hidden message
/// window. Windows lets the app that received a hotkey bring its window to the front, so the shortcut works from any
/// app. A combination another app already holds can't be registered; that's logged and the menu doesn't show it.
/// </summary>
internal sealed class GlobalHotkeys : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_SHIFT = 0x0004, MOD_NOREPEAT = 0x4000;

    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 1;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public GlobalHotkeys() => CreateHandle(new CreateParams { Parent = new IntPtr(-3) }); // HWND_MESSAGE: never shown

    /// <summary>Registers Shift+<paramref name="key"/>; false if another app already holds it.</summary>
    public bool RegisterShift(Keys key, Action action)
    {
        var id = _nextId++;
        if (!RegisterHotKey(Handle, id, MOD_SHIFT | MOD_NOREPEAT, (uint)key))
        {
            Log.Info($"Shortcut Shift+{key} is taken by another app (error {Marshal.GetLastWin32Error()})");
            return false;
        }
        _actions[id] = action;
        return true;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && _actions.TryGetValue((int)m.WParam, out var action)) action();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(Handle, id);
        _actions.Clear();
        DestroyHandle();
    }
}
