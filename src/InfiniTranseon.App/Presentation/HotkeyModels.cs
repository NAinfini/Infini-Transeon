using Windows.System;

namespace InfiniTranseon.App.Presentation;

public enum AppHotkeyAction
{
    ToggleOverlay,
    PauseAll,
    ManualOcr,
    RetranslateCurrent,
    EmergencyStop,
}

public enum AppHotkeyScope
{
    AllRunningTargets,
}

public sealed record AppHotkeyBinding(
    AppHotkeyAction Action,
    string Gesture,
    bool Enabled = true,
    AppHotkeyScope Scope = AppHotkeyScope.AllRunningTargets);

[Flags]
public enum AppHotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008,
    NoRepeat = 0x4000,
}

public readonly record struct ParsedHotkeyGesture(
    AppHotkeyModifiers Modifiers,
    VirtualKey Key)
{
    public string DisplayText => HotkeyGesture.Format(Modifiers, Key);
}

public static class HotkeyDefaults
{
    public static IReadOnlyList<AppHotkeyBinding> Create() =>
    [
        new(AppHotkeyAction.ToggleOverlay, "Ctrl + Alt + T"),
        new(AppHotkeyAction.PauseAll, "Ctrl + Alt + P"),
        new(AppHotkeyAction.ManualOcr, "Ctrl + Alt + O"),
        new(AppHotkeyAction.RetranslateCurrent, "Ctrl + Alt + R"),
        new(AppHotkeyAction.EmergencyStop, "Ctrl + Alt + Escape"),
    ];
}

public static class HotkeyGesture
{
    public static bool TryParse(string? gesture, out ParsedHotkeyGesture parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(gesture))
        {
            return false;
        }

        string[] tokens = gesture
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length < 2)
        {
            return false;
        }

        AppHotkeyModifiers modifiers = AppHotkeyModifiers.None;
        for (int index = 0; index < tokens.Length - 1; index++)
        {
            AppHotkeyModifiers modifier = tokens[index].ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => AppHotkeyModifiers.Control,
                "ALT" => AppHotkeyModifiers.Alt,
                "SHIFT" => AppHotkeyModifiers.Shift,
                "WIN" or "WINDOWS" => AppHotkeyModifiers.Windows,
                _ => AppHotkeyModifiers.None,
            };
            if (modifier == AppHotkeyModifiers.None || modifiers.HasFlag(modifier))
            {
                return false;
            }
            modifiers |= modifier;
        }

        if (modifiers == AppHotkeyModifiers.None ||
            !TryParseKey(tokens[^1], out VirtualKey key) ||
            IsModifierKey(key))
        {
            return false;
        }

        parsed = new ParsedHotkeyGesture(modifiers, key);
        return true;
    }

    public static string Format(AppHotkeyModifiers modifiers, VirtualKey key)
    {
        var tokens = new List<string>(5);
        if (modifiers.HasFlag(AppHotkeyModifiers.Control)) tokens.Add("Ctrl");
        if (modifiers.HasFlag(AppHotkeyModifiers.Alt)) tokens.Add("Alt");
        if (modifiers.HasFlag(AppHotkeyModifiers.Shift)) tokens.Add("Shift");
        if (modifiers.HasFlag(AppHotkeyModifiers.Windows)) tokens.Add("Win");
        tokens.Add(FormatKey(key));
        return string.Join(" + ", tokens);
    }

    public static bool IsModifierKey(VirtualKey key) =>
        key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl or
        VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu or
        VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift or
        VirtualKey.LeftWindows or VirtualKey.RightWindows;

    private static bool TryParseKey(string token, out VirtualKey key)
    {
        string normalized = token.Trim();
        if (normalized.Length == 1)
        {
            char character = char.ToUpperInvariant(normalized[0]);
            if (character is >= 'A' and <= 'Z')
            {
                key = (VirtualKey)character;
                return true;
            }
            if (character is >= '0' and <= '9')
            {
                key = (VirtualKey)character;
                return true;
            }
        }

        return Enum.TryParse(normalized, ignoreCase: true, out key) &&
            Enum.IsDefined(key);
    }

    private static string FormatKey(VirtualKey key)
    {
        int value = (int)key;
        if (value is >= 'A' and <= 'Z' or >= '0' and <= '9')
        {
            return ((char)value).ToString();
        }
        return key.ToString();
    }
}
