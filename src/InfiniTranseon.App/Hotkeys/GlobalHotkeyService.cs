using System.ComponentModel;
using System.Runtime.InteropServices;
using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.Services;
using InfiniTranseon.Core.Diagnostics;
using Microsoft.UI.Dispatching;

namespace InfiniTranseon.App.Hotkeys;

/// <summary>
/// Owns a hidden Win32 message window and registers the user's global, no-repeat hotkeys.
/// Hotkey callbacks are dispatched onto the WinUI queue and invoke only real runtime operations.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const uint WmHotkey = 0x0312;
    private const uint GaRoot = 2;
    private const uint MonitorDefaultToNull = 0;
    private static readonly nint MessageOnlyWindow = new(-3);

    private readonly HotkeyCommandRouter _router;
    private readonly AppStatusLog _statusLog;
    private readonly WindowProcedure _windowProcedure;
    private readonly Dictionary<int, AppHotkeyBinding> _bindings = [];
    private readonly Dictionary<AppHotkeyAction, string> _statusCodes = [];
    private DispatcherQueue? _dispatcher;
    private string? _className;
    private nint _module;
    private nint _window;
    private ushort _classAtom;
    private bool _disposed;

    public GlobalHotkeyService(
        IRuntimeControlService runtime,
        AppStatusLog statusLog)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(statusLog);
        _router = new HotkeyCommandRouter(runtime);
        _statusLog = statusLog;
        _windowProcedure = WindowProc;
    }

    public IReadOnlyDictionary<AppHotkeyAction, string> StatusCodes => _statusCodes;

    public event EventHandler? Changed;

    public void Initialize(DispatcherQueue dispatcher)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(dispatcher);
        if (_window != 0)
        {
            return;
        }

        _dispatcher = dispatcher;
        _module = GetModuleHandle(null);
        _className = $"InfiniTranseon.Hotkeys.{Environment.ProcessId}.{Guid.NewGuid():N}";
        var windowClass = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(),
            WindowProcedure = _windowProcedure,
            Instance = _module,
            ClassName = _className,
        };
        _classAtom = RegisterClassEx(ref windowClass);
        if (_classAtom == 0)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Unable to register the global-hotkey message window class.");
        }

        _window = CreateWindowEx(
            0,
            _className,
            string.Empty,
            0,
            0,
            0,
            0,
            0,
            MessageOnlyWindow,
            0,
            _module,
            0);
        if (_window == 0)
        {
            int error = Marshal.GetLastWin32Error();
            UnregisterClass(_className, _module);
            _classAtom = 0;
            throw new Win32Exception(
                error,
                "Unable to create the global-hotkey message window.");
        }
    }

    public void Apply(IReadOnlyList<AppHotkeyBinding> bindings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(bindings);
        if (_window == 0)
        {
            throw new InvalidOperationException(
                "Global hotkeys must be initialized before they are applied.");
        }

        UnregisterAll();
        _statusCodes.Clear();
        int id = 1;
        foreach (AppHotkeyBinding configuredBinding in bindings)
        {
            AppHotkeyBinding binding = HotkeyBindingRules.Normalize(configuredBinding);
            if (binding.Action == AppHotkeyAction.RetranslateCurrent)
            {
                _statusCodes[binding.Action] = "comingSoon";
                continue;
            }
            if (!binding.Enabled)
            {
                _statusCodes[binding.Action] = "disabled";
                continue;
            }
            try
            {
                HotkeyBindingRules.Validate(binding);
            }
            catch (ArgumentException)
            {
                _statusCodes[binding.Action] =
                    binding.Scope == AppHotkeyScope.SpecificTargetGroup
                        ? "selectionRequired"
                        : "invalid";
                continue;
            }
            if (!HotkeyGesture.TryParse(binding.Gesture, out ParsedHotkeyGesture gesture))
            {
                _statusCodes[binding.Action] = "invalid";
                continue;
            }

            bool registered = RegisterHotKey(
                _window,
                id,
                (uint)(gesture.Modifiers | AppHotkeyModifiers.NoRepeat),
                (uint)gesture.Key);
            if (!registered)
            {
                _statusCodes[binding.Action] = "conflict";
                continue;
            }

            _bindings[id] = binding;
            _statusCodes[binding.Action] = "registered";
            id++;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        UnregisterAll();
        if (_window != 0)
        {
            DestroyWindow(_window);
            _window = 0;
        }
        if (_classAtom != 0 && _className is not null)
        {
            UnregisterClass(_className, _module);
            _classAtom = 0;
        }
    }

    private nint WindowProc(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == WmHotkey &&
            _bindings.TryGetValue(wParam.ToInt32(), out AppHotkeyBinding? binding))
        {
            ForegroundTargetSnapshot foreground = CaptureForegroundTarget();
            _dispatcher?.TryEnqueue(() => _ = ExecuteAsync(binding, foreground));
            return 0;
        }
        return DefWindowProc(window, message, wParam, lParam);
    }

    private async Task ExecuteAsync(
        AppHotkeyBinding binding,
        ForegroundTargetSnapshot foreground)
    {
        try
        {
            RuntimeScopedControlResult result =
                await _router.ExecuteAsync(binding, foreground).ConfigureAwait(true);

            _statusLog.Record(new StatusEvent(
                DateTimeOffset.UtcNow,
                "hotkey",
                result.ReasonCode,
                result.Applied
                    ? "status.hotkey.executed"
                    : "status.hotkey.noMatchingTarget",
                result.Applied
                    ? StatusEventSeverity.Information
                    : StatusEventSeverity.Warning,
                new Dictionary<string, object?>
                {
                    ["action"] = binding.Action.ToString(),
                    ["scope"] = binding.Scope.ToString(),
                    ["resolvedTargetCount"] = result.ResolvedTargetCount,
                    ["applied"] = result.Applied,
                }));
        }
        catch (Exception exception)
        {
            _statusLog.Record(new StatusEvent(
                DateTimeOffset.UtcNow,
                "hotkey",
                $"hotkey.{binding.Action}.failed",
                "status.hotkey.failed",
                StatusEventSeverity.Error,
                new Dictionary<string, object?>
                {
                    ["action"] = binding.Action.ToString(),
                    ["scope"] = binding.Scope.ToString(),
                    ["error"] = exception.Message,
                }));
        }
    }

    private static ForegroundTargetSnapshot CaptureForegroundTarget()
    {
        nint foreground = GetForegroundWindow();
        if (foreground == 0)
        {
            return ForegroundTargetSnapshot.Empty;
        }
        nint root = GetAncestor(foreground, GaRoot);
        if (root == 0)
        {
            root = foreground;
        }
        nint monitor = MonitorFromWindow(root, MonitorDefaultToNull);
        return new ForegroundTargetSnapshot(
            unchecked((ulong)(nuint)root),
            unchecked((ulong)(nuint)monitor));
    }

    private void UnregisterAll()
    {
        if (_window != 0)
        {
            foreach (int id in _bindings.Keys)
            {
                UnregisterHotKey(_window, id);
            }
        }
        _bindings.Clear();
    }

    private delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public WindowProcedure WindowProcedure;
        public int ClassExtraBytes;
        public int WindowExtraBytes;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint BackgroundBrush;
        public string? MenuName;
        public string ClassName;
        public nint SmallIcon;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string className, nint instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint window, int id);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint window, uint flags);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);
}
