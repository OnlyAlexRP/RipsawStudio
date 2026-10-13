using System.Runtime.InteropServices;

namespace RipsawStudio.UI;

/// <summary>
/// System-wide hotkeys, so a capture can be triggered while the game - not Ripsaw Studio -
/// has focus. Only the capture actions are ever registered: binding Esc, M or the arrow keys
/// system-wide would take them away from every other program on the machine.
///
/// A registration can legitimately fail when another application already owns the chord
/// (Win+key combinations, and whatever the user's other overlay software grabbed first), so
/// failures are collected and reported rather than thrown.
/// </summary>
internal sealed class GlobalHotkeys : IDisposable
{
    /// <summary>
    /// The actions eligible for a system-wide binding - the ones that make sense to fire from
    /// inside a game. This is exactly the "Recording" group on the shortcuts page.
    /// </summary>
    public static bool IsEligible(ShortcutAction action) =>
        ShortcutCatalog.Definition(action).IsRecording;

    private readonly Form _owner;
    private readonly Action<ShortcutAction> _run;
    private readonly Dictionary<int, ShortcutAction> _byId = new();
    private readonly HotkeyWindow _window;
    private int _nextId = 1;
    private bool _enabled;

    public GlobalHotkeys(Form owner, Action<ShortcutAction> run)
    {
        _owner = owner;
        _run = run;
        _window = new HotkeyWindow(OnHotkey);
    }

    /// <summary>Chords that could not be taken, formatted for display, from the last Apply.</summary>
    public IReadOnlyList<string> Rejected { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// Brings the registrations in line with the current bindings. Safe to call on every
    /// change - it tears down whatever was registered and puts the current set back.
    /// </summary>
    public void Apply(bool enabled, ShortcutMap shortcuts)
    {
        _enabled = enabled;
        Unregister();
        Rejected = Array.Empty<string>();
        if (!enabled) return;
        if (!_owner.IsHandleCreated) return;   // Reapplied from OnShown once there is a handle.

        var rejected = new List<string>();
        var taken = new HashSet<(uint, uint)>();

        foreach (var definition in ShortcutCatalog.All)
        {
            if (!IsEligible(definition.Action)) continue;
            foreach (var slot in new[] { ShortcutSlot.Primary, ShortcutSlot.Alternate })
            {
                var keys = shortcuts.Get(definition.Action, slot);
                if (keys == Keys.None || !ShortcutCatalog.IsBindable(keys)) continue;

                var (modifiers, code) = Split(keys);
                // The same chord can sit in two slots; registering it twice fails the second time.
                if (!taken.Add((modifiers, code))) continue;

                int id = _nextId++;
                if (RegisterHotKey(_window.Handle, id, modifiers | MOD_NOREPEAT, code))
                    _byId[id] = definition.Action;
                else
                    rejected.Add(ShortcutCatalog.Describe(keys));
            }
        }
        Rejected = rejected;
    }

    private void Unregister()
    {
        if (!_window.IsHandleCreated) { _byId.Clear(); return; }
        foreach (int id in _byId.Keys) UnregisterHotKey(_window.Handle, id);
        _byId.Clear();
        // Every id is free again, so numbering can start over - ids have a ceiling, and a
        // long session of toggling this setting would otherwise walk towards it.
        _nextId = 1;
    }

    private void OnHotkey(int id)
    {
        if (_enabled && _byId.TryGetValue(id, out var action)) _run(action);
    }

    private static (uint Modifiers, uint Code) Split(Keys keys)
    {
        uint modifiers = 0;
        if (keys.HasFlag(Keys.Alt)) modifiers |= MOD_ALT;
        if (keys.HasFlag(Keys.Control)) modifiers |= MOD_CONTROL;
        if (keys.HasFlag(Keys.Shift)) modifiers |= MOD_SHIFT;
        return (modifiers, (uint)(keys & Keys.KeyCode));
    }

    public void Dispose()
    {
        Unregister();
        _window.DestroyHandle();
    }

    /// <summary>
    /// WM_HOTKEY is posted to the window that registered the chord, and it is a thread
    /// message rather than anything MainForm's own WndProc would see cleanly alongside the
    /// video window, so the registrations live on a small window of their own.
    /// </summary>
    private sealed class HotkeyWindow : NativeWindow
    {
        private const int WM_HOTKEY = 0x0312;
        private readonly Action<int> _onHotkey;

        public HotkeyWindow(Action<int> onHotkey)
        {
            _onHotkey = onHotkey;
            CreateHandle(new CreateParams());
        }

        public bool IsHandleCreated => Handle != IntPtr.Zero;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY) _onHotkey((int)m.WParam);
            base.WndProc(ref m);
        }
    }

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    /// <summary>Holding the key down fires once, not a stream of repeats.</summary>
    private const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
