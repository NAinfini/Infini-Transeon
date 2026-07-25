using InfiniTranseon.App.Presentation;
using Windows.System;

namespace InfiniTranseon.App.Tests;

public sealed class HotkeyModelTests
{
    [Theory]
    [InlineData("Ctrl + Alt + T", AppHotkeyModifiers.Control | AppHotkeyModifiers.Alt, VirtualKey.T)]
    [InlineData("Shift + F8", AppHotkeyModifiers.Shift, VirtualKey.F8)]
    [InlineData("Win + Escape", AppHotkeyModifiers.Windows, VirtualKey.Escape)]
    public void Parser_accepts_supported_global_shortcuts(
        string text,
        AppHotkeyModifiers modifiers,
        VirtualKey key)
    {
        Assert.True(HotkeyGesture.TryParse(text, out ParsedHotkeyGesture parsed));
        Assert.Equal(modifiers, parsed.Modifiers);
        Assert.Equal(key, parsed.Key);
        Assert.Equal(text, parsed.DisplayText);
    }

    [Theory]
    [InlineData("")]
    [InlineData("T")]
    [InlineData("Ctrl + Control + T")]
    [InlineData("Ctrl + Alt")]
    [InlineData("Ctrl + NotAKey")]
    public void Parser_rejects_shortcuts_that_could_intercept_normal_input(string text) =>
        Assert.False(HotkeyGesture.TryParse(text, out _));

    [Fact]
    public void Defaults_cover_every_runtime_safe_action_without_conflicts()
    {
        IReadOnlyList<AppHotkeyBinding> bindings = HotkeyDefaults.Create();

        Assert.Equal(Enum.GetValues<AppHotkeyAction>().Length, bindings.Count);
        Assert.Equal(bindings.Count, bindings.Select(binding => binding.Action).Distinct().Count());
        Assert.Equal(
            bindings.Count,
            bindings.Select(binding => binding.Gesture).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(bindings, binding =>
            Assert.True(HotkeyGesture.TryParse(binding.Gesture, out _)));
    }
}
