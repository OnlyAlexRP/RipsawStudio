namespace RipsawStudio.UI;

/// <summary>
/// Everything that can be bound to a key. The names are persisted in settings.json, so
/// renaming one silently drops whatever the user had bound to it.
/// </summary>
internal enum ShortcutAction
{
    TogglePanel,
    Fullscreen,
    Snapshot,
    RecordToggle,
    SaveReplay,
    ReplayToggle,
    OpenFolder,
    MicMute,
    Resync,
    VolumeUp,
    VolumeDown,
    Mute,
    NextProfile,
}

internal sealed record ShortcutDefinition(
    ShortcutAction Action, string Label, Keys Primary, Keys Alternate, bool IsRecording);

/// <summary>The catalogue of actions and the keys they start out on.</summary>
internal static class ShortcutCatalog
{
    /// <summary>
    /// Order here is the order the list is shown in. The primary defaults for panel,
    /// fullscreen, snapshot, volume and mute are VideoGameCapture's, so muscle memory
    /// carries over; the alternates are the Windows conventions for the same thing.
    /// </summary>
    public static readonly ShortcutDefinition[] All =
    {
        new(ShortcutAction.RecordToggle, "Start / stop recording", Keys.F10, Keys.None, true),
        new(ShortcutAction.Snapshot, "Snapshot", Keys.F9, Keys.F12, true),
        new(ShortcutAction.SaveReplay, "Save the instant replay", Keys.F8, Keys.None, true),
        new(ShortcutAction.ReplayToggle, "Arm / disarm the replay buffer", Keys.Shift | Keys.F8, Keys.None, true),
        new(ShortcutAction.MicMute, "Mute / unmute the microphone", Keys.Control | Keys.Shift | Keys.M, Keys.None, true),
        new(ShortcutAction.OpenFolder, "Open the recordings folder", Keys.Control | Keys.O, Keys.None, true),

        new(ShortcutAction.TogglePanel, "Open / close this panel", Keys.Escape, Keys.None, false),
        new(ShortcutAction.Fullscreen, "Fullscreen", Keys.F5, Keys.F11, false),
        new(ShortcutAction.Resync, "Resync: flush video, restart audio", Keys.F6, Keys.None, false),
        new(ShortcutAction.VolumeUp, "Volume up", Keys.Up, Keys.None, false),
        new(ShortcutAction.VolumeDown, "Volume down", Keys.Down, Keys.None, false),
        new(ShortcutAction.Mute, "Mute the monitor", Keys.M, Keys.Control | Keys.M, false),
        new(ShortcutAction.NextProfile, "Switch to the next profile", Keys.Control | Keys.Tab, Keys.None, false),
    };

    public static ShortcutDefinition Definition(ShortcutAction action) =>
        All.First(d => d.Action == action);

    /// <summary>
    /// A readable chord, drawn in the panel and written into the docs. Deliberately not
    /// KeysConverter, whose output is localised and has a stray comma in it.
    /// </summary>
    public static string Describe(Keys keys)
    {
        if (keys == Keys.None) return "not bound";
        var parts = new List<string>(4);
        if (keys.HasFlag(Keys.Control)) parts.Add("Ctrl");
        if (keys.HasFlag(Keys.Shift)) parts.Add("Shift");
        if (keys.HasFlag(Keys.Alt)) parts.Add("Alt");
        parts.Add(DescribeCode(keys & Keys.KeyCode));
        return string.Join("+", parts);
    }

    private static string DescribeCode(Keys code) => code switch
    {
        Keys.Up => "Up",
        Keys.Down => "Down",
        Keys.Left => "Left",
        Keys.Right => "Right",
        Keys.Escape => "Esc",
        Keys.Space => "Space",
        Keys.Back => "Backspace",
        Keys.Return => "Enter",
        Keys.Prior => "PgUp",
        Keys.Next => "PgDn",
        Keys.Oemtilde => "`",
        Keys.OemMinus => "-",
        Keys.Oemplus => "=",
        Keys.OemOpenBrackets => "[",
        Keys.OemCloseBrackets => "]",
        Keys.OemSemicolon => ";",
        Keys.OemQuotes => "'",
        Keys.Oemcomma => ",",
        Keys.OemPeriod => ".",
        Keys.OemQuestion => "/",
        Keys.OemPipe => "\\",
        >= Keys.D0 and <= Keys.D9 => ((char)('0' + (code - Keys.D0))).ToString(),
        >= Keys.NumPad0 and <= Keys.NumPad9 => "Num " + (code - Keys.NumPad0),
        _ => code.ToString(),
    };

    /// <summary>A chord is only usable if it carries a real key, not a lone modifier.</summary>
    public static bool IsBindable(Keys keys)
    {
        var code = keys & Keys.KeyCode;
        return code is not (Keys.None or Keys.ControlKey or Keys.ShiftKey or Keys.Menu or
                            Keys.LWin or Keys.RWin or Keys.Capital or Keys.NumLock);
    }
}

/// <summary>Which slot of an action a chord sits in.</summary>
internal enum ShortcutSlot { Primary, Alternate }

/// <summary>
/// The live binding table. Owns the mapping in both directions, so a keypress resolves to
/// an action in one dictionary lookup rather than a walk down a switch statement, and a
/// rebind can find and clear whatever already held that chord.
/// </summary>
internal sealed class ShortcutMap
{
    private readonly Dictionary<(ShortcutAction, ShortcutSlot), Keys> _bindings = new();
    private readonly Dictionary<Keys, ShortcutAction> _byKey = new();

    public ShortcutMap(AppSettings settings) => LoadFrom(settings);

    public void LoadFrom(AppSettings settings)
    {
        _bindings.Clear();
        foreach (var definition in ShortcutCatalog.All)
        {
            settings.Shortcuts.TryGetValue(definition.Action.ToString(), out var stored);
            Put(definition.Action, ShortcutSlot.Primary, Parse(stored?.Primary) ?? definition.Primary);
            Put(definition.Action, ShortcutSlot.Alternate, Parse(stored?.Alternate) ?? definition.Alternate);
        }
        Reindex();
    }

    public void SaveInto(AppSettings settings)
    {
        settings.Shortcuts = ShortcutCatalog.All.ToDictionary(
            d => d.Action.ToString(),
            d => new ShortcutBinding
            {
                Primary = Store(Get(d.Action, ShortcutSlot.Primary)),
                Alternate = Store(Get(d.Action, ShortcutSlot.Alternate)),
            });
    }

    public Keys Get(ShortcutAction action, ShortcutSlot slot) =>
        _bindings.TryGetValue((action, slot), out var keys) ? keys : Keys.None;

    /// <summary>The action a chord runs, or null if nothing is bound to it.</summary>
    public ShortcutAction? Lookup(Keys keys) =>
        _byKey.TryGetValue(keys, out var action) ? action : null;

    /// <summary>
    /// Binds a chord, taking it off whatever held it before. Two actions on one key would
    /// leave one of them permanently dead, which is worse than saying so and moving it.
    /// </summary>
    public ShortcutAction? Set(ShortcutAction action, ShortcutSlot slot, Keys keys)
    {
        ShortcutAction? displaced = null;
        if (keys != Keys.None)
        {
            foreach (var ((otherAction, otherSlot), bound) in _bindings.ToArray())
            {
                if (bound != keys || (otherAction == action && otherSlot == slot)) continue;
                _bindings[(otherAction, otherSlot)] = Keys.None;
                if (otherAction != action) displaced = otherAction;
            }
        }
        Put(action, slot, keys);
        Reindex();
        return displaced;
    }

    public void ResetToDefaults()
    {
        _bindings.Clear();
        foreach (var definition in ShortcutCatalog.All)
        {
            Put(definition.Action, ShortcutSlot.Primary, definition.Primary);
            Put(definition.Action, ShortcutSlot.Alternate, definition.Alternate);
        }
        Reindex();
    }

    /// <summary>Both chords for an action, formatted for display - "F9 / F12".</summary>
    public string DescribeBoth(ShortcutAction action)
    {
        var primary = Get(action, ShortcutSlot.Primary);
        var alternate = Get(action, ShortcutSlot.Alternate);
        if (primary == Keys.None && alternate == Keys.None) return "not bound";
        if (alternate == Keys.None) return ShortcutCatalog.Describe(primary);
        if (primary == Keys.None) return ShortcutCatalog.Describe(alternate);
        return ShortcutCatalog.Describe(primary) + " / " + ShortcutCatalog.Describe(alternate);
    }

    private void Put(ShortcutAction action, ShortcutSlot slot, Keys keys) => _bindings[(action, slot)] = keys;

    private void Reindex()
    {
        _byKey.Clear();
        foreach (var ((action, _), keys) in _bindings)
            if (keys != Keys.None) _byKey[keys] = action;
    }

    // Round-tripped through the invariant enum name ("Control, M"), not the display form,
    // so a settings file stays readable by a later build with a different formatter.
    private static Keys? Parse(string? text) =>
        !string.IsNullOrWhiteSpace(text) && Enum.TryParse<Keys>(text, out var keys) ? keys : null;

    private static string? Store(Keys keys) => keys == Keys.None ? null : keys.ToString();
}
