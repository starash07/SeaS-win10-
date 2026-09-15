namespace SeaS.App.Models;

public enum ReaderShortcutAction
{
    BackToShelf,
    ToggleBookmarks,
    AddBookmark,
    ToggleImmersiveMode,
    ToggleImmersiveMaximize
}

public sealed record ReaderShortcutDefinition(
    ReaderShortcutAction Action,
    string FunctionName,
    string DefaultShortcut);

public sealed class ReaderShortcutPreferences
{
    public string BackToShelf { get; set; } = "Backspace";
    public string ToggleBookmarks { get; set; } = "Ctrl+B";
    public string AddBookmark { get; set; } = "Ctrl+D";
    public string ToggleImmersiveMode { get; set; } = "F10";
    public string ToggleImmersiveMaximize { get; set; } = "F11";

    public static IReadOnlyList<ReaderShortcutDefinition> Definitions { get; } =
    [
        new(ReaderShortcutAction.BackToShelf, "返回书架", "Backspace"),
        new(ReaderShortcutAction.ToggleBookmarks, "打开/关闭书签列表", "Ctrl+B"),
        new(ReaderShortcutAction.AddBookmark, "添加书签", "Ctrl+D"),
        new(ReaderShortcutAction.ToggleImmersiveMode, "专注模式开关", "F10"),
        new(ReaderShortcutAction.ToggleImmersiveMaximize, "专注模式下全屏窗口/取消全屏", "F11")
    ];

    public ReaderShortcutPreferences Clone()
    {
        return new ReaderShortcutPreferences
        {
            BackToShelf = BackToShelf,
            ToggleBookmarks = ToggleBookmarks,
            AddBookmark = AddBookmark,
            ToggleImmersiveMode = ToggleImmersiveMode,
            ToggleImmersiveMaximize = ToggleImmersiveMaximize
        };
    }

    public string GetShortcut(ReaderShortcutAction action)
    {
        return action switch
        {
            ReaderShortcutAction.BackToShelf => BackToShelf,
            ReaderShortcutAction.ToggleBookmarks => ToggleBookmarks,
            ReaderShortcutAction.AddBookmark => AddBookmark,
            ReaderShortcutAction.ToggleImmersiveMode => ToggleImmersiveMode,
            ReaderShortcutAction.ToggleImmersiveMaximize => ToggleImmersiveMaximize,
            _ => string.Empty
        };
    }

    public void SetShortcut(ReaderShortcutAction action, string shortcut)
    {
        switch (action)
        {
            case ReaderShortcutAction.BackToShelf:
                BackToShelf = shortcut;
                break;
            case ReaderShortcutAction.ToggleBookmarks:
                ToggleBookmarks = shortcut;
                break;
            case ReaderShortcutAction.AddBookmark:
                AddBookmark = shortcut;
                break;
            case ReaderShortcutAction.ToggleImmersiveMode:
                ToggleImmersiveMode = shortcut;
                break;
            case ReaderShortcutAction.ToggleImmersiveMaximize:
                ToggleImmersiveMaximize = shortcut;
                break;
        }
    }

    public bool Normalize()
    {
        var changed = false;
        if (string.Equals(ToggleImmersiveMode, "Shift+F11", StringComparison.OrdinalIgnoreCase))
        {
            ToggleImmersiveMode = "F10";
            changed = true;
        }

        foreach (var definition in Definitions)
        {
            var current = GetShortcut(definition.Action);
            if (ShortcutGesture.TryParse(current, out var gesture))
            {
                var normalized = gesture.ToDisplayString();
                if (!string.Equals(current, normalized, StringComparison.Ordinal))
                {
                    SetShortcut(definition.Action, normalized);
                    changed = true;
                }
            }
            else
            {
                SetShortcut(definition.Action, definition.DefaultShortcut);
                changed = true;
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in Definitions)
        {
            var shortcut = GetShortcut(definition.Action);
            if (seen.Add(shortcut))
            {
                continue;
            }

            SetShortcut(definition.Action, definition.DefaultShortcut);
            changed = true;
        }

        return changed;
    }
}
