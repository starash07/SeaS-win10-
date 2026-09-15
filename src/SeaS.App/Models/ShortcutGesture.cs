using System.Windows.Input;

namespace SeaS.App.Models;

public readonly record struct ShortcutGesture(Key ShortcutKey, ModifierKeys Modifiers)
{
    public static bool TryParse(string? value, out ShortcutGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var tokens = value
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (tokens.Count == 0)
        {
            return false;
        }

        var modifiers = ModifierKeys.None;
        for (var index = 0; index < tokens.Count - 1; index++)
        {
            if (!TryParseModifier(tokens[index], out var modifier))
            {
                return false;
            }

            modifiers |= modifier;
        }

        if (!TryParseKey(tokens[^1], out var key) || IsModifierKey(key))
        {
            return false;
        }

        gesture = new ShortcutGesture(key, modifiers);
        return true;
    }

    public static bool TryFromKeyEvent(KeyEventArgs e, out ShortcutGesture gesture)
    {
        gesture = default;
        var key = GetEventKey(e);
        if (key == Key.None || IsModifierKey(key))
        {
            return false;
        }

        var modifiers = Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt);
        gesture = new ShortcutGesture(key, modifiers);
        return true;
    }

    public bool Matches(KeyEventArgs e)
    {
        return GetEventKey(e) == ShortcutKey
               && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt)) == Modifiers;
    }

    public string ToDisplayString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        parts.Add(GetKeyDisplayName(ShortcutKey));
        return string.Join("+", parts);
    }

    public bool NeedsModifier()
    {
        return Modifiers == ModifierKeys.None
               && ((ShortcutKey >= Key.A && ShortcutKey <= Key.Z)
                   || (ShortcutKey >= Key.D0 && ShortcutKey <= Key.D9));
    }

    private static Key GetEventKey(KeyEventArgs e)
    {
        return e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.DeadCharProcessed => e.DeadCharProcessedKey,
            _ => e.Key
        };
    }

    private static bool TryParseModifier(string token, out ModifierKeys modifier)
    {
        modifier = token.ToLowerInvariant() switch
        {
            "ctrl" or "control" => ModifierKeys.Control,
            "shift" => ModifierKeys.Shift,
            "alt" => ModifierKeys.Alt,
            _ => ModifierKeys.None
        };
        return modifier != ModifierKeys.None;
    }

    private static bool TryParseKey(string token, out Key key)
    {
        key = token.ToLowerInvariant() switch
        {
            "backspace" => Key.Back,
            "esc" => Key.Escape,
            "del" => Key.Delete,
            _ => Enum.TryParse(token, ignoreCase: true, out Key parsed) ? parsed : Key.None
        };
        return key != Key.None;
    }

    private static string GetKeyDisplayName(Key key)
    {
        return key switch
        {
            Key.Back => "Backspace",
            Key.Escape => "Esc",
            Key.Delete => "Delete",
            _ => key.ToString()
        };
    }

    private static bool IsModifierKey(Key key)
    {
        return key is Key.LeftCtrl
            or Key.RightCtrl
            or Key.LeftShift
            or Key.RightShift
            or Key.LeftAlt
            or Key.RightAlt
            or Key.LWin
            or Key.RWin;
    }
}
