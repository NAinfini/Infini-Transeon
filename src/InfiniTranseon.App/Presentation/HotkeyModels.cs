using Windows.System;

namespace InfiniTranseon.App.Presentation;

public enum AppHotkeyAction
{
    ToggleOverlay,
    PauseAll,
    ManualOcr,
    CycleTranslationGroup,
    RetranslateCurrent,
    EmergencyStop,
}

public enum AppHotkeyScope
{
    AllRunningTargets,
    ForegroundMatchingTarget,
    SpecificTargetGroup,
}

public sealed record AppHotkeyTargetReference(Guid ProfileId, Guid ProfileTargetId);

public sealed record AppHotkeyBinding(
    AppHotkeyAction Action,
    string Gesture,
    bool Enabled = true,
    AppHotkeyScope Scope = AppHotkeyScope.AllRunningTargets,
    IReadOnlyList<AppHotkeyTargetReference>? SpecificTargets = null)
{
    public IReadOnlyList<AppHotkeyTargetReference> EffectiveSpecificTargets =>
        SpecificTargets ?? [];
}

public static class HotkeyBindingRules
{
    public static AppHotkeyBinding Normalize(AppHotkeyBinding binding) => binding.Action switch
    {
        AppHotkeyAction.EmergencyStop => binding with
        {
            Scope = AppHotkeyScope.AllRunningTargets,
            SpecificTargets = [],
        },
        AppHotkeyAction.CycleTranslationGroup => binding with
        {
            Scope = AppHotkeyScope.AllRunningTargets,
            SpecificTargets = [],
        },
        AppHotkeyAction.RetranslateCurrent => binding with { Enabled = false },
        _ => binding,
    };

    public static void Validate(AppHotkeyBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!Enum.IsDefined(binding.Action) || !Enum.IsDefined(binding.Scope))
            throw new ArgumentException("The hotkey action or scope is invalid.", nameof(binding));
        if (binding.Action == AppHotkeyAction.EmergencyStop &&
            binding.Scope != AppHotkeyScope.AllRunningTargets)
        {
            throw new ArgumentException("Emergency stop must apply to all running targets.", nameof(binding));
        }
        if (binding.Action == AppHotkeyAction.CycleTranslationGroup &&
            binding.Scope != AppHotkeyScope.AllRunningTargets)
        {
            throw new ArgumentException("Translation-group cycling applies to all running targets.", nameof(binding));
        }

        IReadOnlyList<AppHotkeyTargetReference> targets = binding.EffectiveSpecificTargets;
        if (targets.Count > 128 || targets.Any(target =>
                target.ProfileId == Guid.Empty || target.ProfileTargetId == Guid.Empty) ||
            targets.Distinct().Count() != targets.Count)
        {
            throw new ArgumentException("Specific hotkey targets are invalid.", nameof(binding));
        }
        if (binding.Scope == AppHotkeyScope.SpecificTargetGroup && targets.Count == 0)
        {
            throw new ArgumentException("A specific-target hotkey requires at least one target.", nameof(binding));
        }
    }
}

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
        new(AppHotkeyAction.CycleTranslationGroup, "Ctrl + Alt + G"),
        new(AppHotkeyAction.RetranslateCurrent, "Ctrl + Alt + R", Enabled: false),
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
