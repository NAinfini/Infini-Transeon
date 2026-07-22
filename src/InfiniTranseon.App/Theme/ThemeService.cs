using Microsoft.UI.Xaml;

namespace InfiniTranseon.App.Theme;

public enum ThemeMode
{
    System,
    Light,
    Dark,
}

/// <summary>
/// Applies the requested theme to the window root without reconstructing the navigation frame.
/// Persistence arrives with the real settings service (frontend Task 1/12).
/// </summary>
public sealed class ThemeService
{
    public static ThemeService Instance { get; } = new();

    public ThemeMode Mode { get; private set; } = ThemeMode.System;

    public event EventHandler<ThemeMode>? ThemeChanged;

    public void Apply(ThemeMode mode, FrameworkElement root)
    {
        Mode = mode;
        root.RequestedTheme = mode switch
        {
            ThemeMode.Light => ElementTheme.Light,
            ThemeMode.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        ThemeChanged?.Invoke(this, mode);
    }
}
