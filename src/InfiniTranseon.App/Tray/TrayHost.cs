using System.ComponentModel;
using System.Runtime.InteropServices;
using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.Services;
using InfiniTranseon.Contracts.Runtime;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.ApplicationModel.Resources;
using InfiniTranseon.Core.Diagnostics;

namespace InfiniTranseon.App.Tray;

/// <summary>
/// Native notification-area surface for runtime controls that must remain available while the main
/// window is hidden. The hidden Win32 window owns the icon and menu without activating the game.
/// </summary>
public sealed class TrayHost : IDisposable
{
    private const uint WmApp = 0x8000;
    private const uint TrayCallbackMessage = WmApp + 0x71;
    private const uint WmNull = 0x0000;
    private const uint WmContextMenu = 0x007B;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmLButtonDoubleClick = 0x0203;
    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NimSetVersion = 0x00000004;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NotifyIconVersion4 = 4;
    private const uint MfString = 0x00000000;
    private const uint MfGrayed = 0x00000001;
    private const uint MfChecked = 0x00000008;
    private const uint MfPopup = 0x00000010;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCommand = 0x0100;
    private const int IdiApplication = 32512;
    private const int IdiError = 32513;
    private const int IdiWarning = 32515;
    private const int IdiInformation = 32516;
    private const uint CommandPause = 1;
    private const uint CommandOverlay = 2;
    private const uint CommandManualOcr = 3;
    private const uint CommandTranslationGroupFirst = 100;
    private const uint CommandOpen = 10;
    private const uint CommandExit = 11;

    private static readonly ResourceLoader Strings = new(
        ResourceLoader.GetDefaultResourceFilePath(),
        "Resources");

    private readonly IRuntimeControlService _runtime;
    private readonly AppStatusLog _statusLog;
    private readonly DispatcherQueue _dispatcher;
    private readonly WindowProcedure _windowProcedure;
    private readonly string _windowClassName;
    private readonly nint _moduleHandle;
    private nint _windowHandle;
    private bool _iconAdded;
    private bool _disposed;
    private readonly Dictionary<uint, Guid> _translationGroupCommands = [];

    public TrayHost(
        IRuntimeControlService runtime,
        AppStatusLog statusLog,
        DispatcherQueue dispatcher)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _statusLog = statusLog ?? throw new ArgumentNullException(nameof(statusLog));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _windowProcedure = WindowProc;
        _windowClassName = $"InfiniTranseon.Tray.{Environment.ProcessId}";
        _moduleHandle = GetModuleHandle(null);
        InitializeNativeWindow();
        AddIcon();
        _runtime.StatusChanged += OnRuntimeStatusChanged;
        // Pause and overlay toggles raise TargetsChanged, not StatusChanged; without this the icon
        // and tooltip would keep claiming "running" after the user paused from this very menu.
        _runtime.TargetsChanged += OnRuntimeTargetsChanged;
    }

    public event EventHandler? OpenRequested;
    public event EventHandler? ExitRequested;

    private void InitializeNativeWindow()
    {
        var windowClass = new WindowClass
        {
            Instance = _moduleHandle,
            WindowProcedure = _windowProcedure,
            ClassName = _windowClassName,
        };
        if (RegisterClass(ref windowClass) == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "tray.windowClass.registerFailed");
        }

        _windowHandle = CreateWindowEx(
            0,
            _windowClassName,
            "Infini-Transeon tray",
            0,
            0,
            0,
            0,
            0,
            nint.Zero,
            nint.Zero,
            _moduleHandle,
            nint.Zero);
        if (_windowHandle == nint.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            UnregisterClass(_windowClassName, _moduleHandle);
            throw new Win32Exception(error, "tray.window.createFailed");
        }
    }

    private void AddIcon()
    {
        NotifyIconData data = CreateIconData(NimAdd);
        if (!ShellNotifyIcon(NimAdd, ref data))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "tray.icon.addFailed");
        }

        _iconAdded = true;
        data.VersionOrTimeout = NotifyIconVersion4;
        if (!ShellNotifyIcon(NimSetVersion, ref data))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "tray.icon.versionFailed");
        }
    }

    private NotifyIconData CreateIconData(uint operation)
    {
        EngineRuntimeStatus status = _runtime.Status;
        int iconId = status switch
        {
            EngineRuntimeStatus.Faulted or EngineRuntimeStatus.ExecutableNotFound => IdiError,
            // Paused shares the warning icon with Restarting: while the window is hidden the icon and
            // its tooltip are the only status channel, so "running" must not look identical to "paused".
            EngineRuntimeStatus.Restarting => IdiWarning,
            EngineRuntimeStatus.Running when _runtime.IsPaused => IdiWarning,
            EngineRuntimeStatus.Running => IdiInformation,
            _ => IdiApplication,
        };
        nint icon = LoadIcon(nint.Zero, new nint(iconId));
        if (icon == nint.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "tray.icon.loadFailed");
        }

        return new NotifyIconData
        {
            Size = Marshal.SizeOf<NotifyIconData>(),
            WindowHandle = _windowHandle,
            Id = 1,
            Flags = NifMessage | NifIcon | NifTip,
            CallbackMessage = TrayCallbackMessage,
            Icon = icon,
            Tip = $"{Strings.GetString("TrayTooltip")}\n{StatusText(status)}",
        };
    }

    private string StatusText(EngineRuntimeStatus status) => Strings.GetString(status switch
    {
        EngineRuntimeStatus.Running => _runtime.IsPaused ? "RuntimeStatusPaused" : "RuntimeStatusRunning",
        EngineRuntimeStatus.Locating or EngineRuntimeStatus.Starting or
            EngineRuntimeStatus.Restarting or EngineRuntimeStatus.Stopping => "RuntimeStatusTransitioning",
        EngineRuntimeStatus.Faulted or EngineRuntimeStatus.ExecutableNotFound => "RuntimeStatusNeedsAttention",
        _ => "RuntimeStatusStopped",
    });

    private void OnRuntimeStatusChanged(object? sender, EngineRuntimeStatusChange args) =>
        _dispatcher.TryEnqueue(UpdateIcon);

    private void OnRuntimeTargetsChanged(object? sender, EventArgs args) =>
        _dispatcher.TryEnqueue(UpdateIcon);

    private void UpdateIcon()
    {
        if (!_iconAdded || _disposed)
        {
            return;
        }

        NotifyIconData data = CreateIconData(NimModify);
        if (!ShellNotifyIcon(NimModify, ref data))
        {
            RecordFailure("tray.icon.updateFailed", new Win32Exception(Marshal.GetLastWin32Error()));
        }
    }

    private nint WindowProc(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == TrayCallbackMessage)
        {
            uint notification = unchecked((uint)lParam.ToInt64()) & 0xFFFF;
            if (notification == WmLButtonDoubleClick)
            {
                _dispatcher.TryEnqueue(() => OpenRequested?.Invoke(this, EventArgs.Empty));
            }
            else if (notification is WmRButtonUp or WmContextMenu)
            {
                _dispatcher.TryEnqueue(ShowContextMenu);
            }
            return nint.Zero;
        }

        return DefWindowProc(window, message, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        nint menu = CreatePopupMenu();
        if (menu == nint.Zero)
        {
            RecordFailure("tray.menu.createFailed", new Win32Exception(Marshal.GetLastWin32Error()));
            return;
        }

        try
        {
            // Pause, overlay and manual OCR only mean anything against a running engine. Offering them
            // while it is stopped would make the menu accept clicks that cannot do anything, and the
            // resulting failure would be invisible while the main window is hidden.
            uint runtimeItemFlags = _runtime.Status == EngineRuntimeStatus.Running
                ? MfString
                : MfString | MfGrayed;
            AppendMenu(menu, runtimeItemFlags, CommandPause, Strings.GetString(
                _runtime.IsPaused ? "TrayResumeAll" : "TrayPauseAll"));
            AppendMenu(menu, runtimeItemFlags, CommandOverlay, Strings.GetString(
                _runtime.IsOverlayVisible ? "TrayHideOverlay" : "TrayShowOverlay"));
            AppendMenu(menu, runtimeItemFlags, CommandManualOcr, Strings.GetString("TrayManualOcr"));
            AppendTranslationGroupMenu(menu, runtimeItemFlags);
            AppendMenu(menu, MfSeparator, 0, null);
            AppendMenu(menu, MfString, CommandOpen, Strings.GetString("TrayOpen"));
            AppendMenu(menu, MfString, CommandExit, Strings.GetString("TrayExit"));
            if (!GetCursorPos(out Point point))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "tray.menu.cursorFailed");
            }

            SetForegroundWindow(_windowHandle);
            uint command = TrackPopupMenu(
                menu,
                TpmRightButton | TpmReturnCommand,
                point.X,
                point.Y,
                0,
                _windowHandle,
                nint.Zero);
            PostMessage(_windowHandle, WmNull, nint.Zero, nint.Zero);
            ExecuteCommand(command);
        }
        catch (Exception exception)
        {
            RecordFailure("tray.menu.failed", exception);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void AppendTranslationGroupMenu(nint menu, uint runtimeItemFlags)
    {
        _translationGroupCommands.Clear();
        IReadOnlyList<TranslationGroupOption> groups = _runtime
            .GetActiveTranslationGroupsAsync()
            .ConfigureAwait(true)
            .GetAwaiter()
            .GetResult();
        nint submenu = CreatePopupMenu();
        if (submenu == nint.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "tray.menu.translationGroupsFailed");
        uint flags = runtimeItemFlags;
        if (groups.Count < 2) flags |= MfGrayed;
        for (int index = 0; index < groups.Count; index++)
        {
            TranslationGroupOption group = groups[index];
            uint command = CommandTranslationGroupFirst + (uint)index;
            _translationGroupCommands[command] = group.TranslationGroupId;
            uint itemFlags = flags | (index == 0 ? MfChecked : 0);
            AppendMenu(submenu, itemFlags, command, group.Name);
        }
        if (groups.Count == 0)
            AppendMenu(submenu, MfString | MfGrayed, 0, Strings.GetString("TrayTranslationGroupUnavailable"));
        AppendMenu(menu, MfString | MfPopup, unchecked((uint)submenu.ToInt64()),
            Strings.GetString("TrayTranslationGroup"));
    }

    private void ExecuteCommand(uint command)
    {
        if (_translationGroupCommands.TryGetValue(command, out Guid groupId))
        {
            _ = ExecuteRuntimeActionAsync(
                async () => await _runtime.SwitchTranslationGroupAsync(groupId)
                    .ConfigureAwait(true),
                "tray.translationGroup");
            return;
        }
        switch (command)
        {
            case CommandPause:
                _ = ExecuteRuntimeActionAsync(
                    () => _runtime.SetPausedAsync(!_runtime.IsPaused),
                    "tray.pause");
                break;
            case CommandOverlay:
                _ = ExecuteRuntimeActionAsync(
                    () => _runtime.SetOverlayVisibleAsync(!_runtime.IsOverlayVisible),
                    "tray.overlay");
                break;
            case CommandManualOcr:
                _ = ExecuteRuntimeActionAsync(
                    () => _runtime.RequestManualOcrAsync(),
                    "tray.manualOcr");
                break;
            case CommandOpen:
                OpenRequested?.Invoke(this, EventArgs.Empty);
                break;
            case CommandExit:
                ExitRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private async Task ExecuteRuntimeActionAsync(Func<Task> action, string operation)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            RecordFailure(operation, exception);
        }
    }

    private void RecordFailure(string operation, Exception exception) =>
        _statusLog.Record(new StatusEvent(
            DateTimeOffset.UtcNow,
            "tray",
            operation,
            "status.tray.operation.failed",
            StatusEventSeverity.Error,
            new Dictionary<string, object?>
            {
                ["error"] = exception.Message,
            }));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _runtime.StatusChanged -= OnRuntimeStatusChanged;
        _runtime.TargetsChanged -= OnRuntimeTargetsChanged;
        if (_iconAdded)
        {
            NotifyIconData data = CreateIconData(NimDelete);
            ShellNotifyIcon(NimDelete, ref data);
            _iconAdded = false;
        }
        if (_windowHandle != nint.Zero)
        {
            DestroyWindow(_windowHandle);
            _windowHandle = nint.Zero;
        }
        UnregisterClass(_windowClassName, _moduleHandle);
    }

    private delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Style;
        [MarshalAs(UnmanagedType.FunctionPtr)]
        public WindowProcedure WindowProcedure;
        public int ClassExtraBytes;
        public int WindowExtraBytes;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint BackgroundBrush;
        public string? MenuName;
        public string ClassName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int Size;
        public nint WindowHandle;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string? Info;
        public uint VersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string? InfoTitle;
        public uint InfoFlags;
        public Guid ItemGuid;
        public nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass(ref WindowClass windowClass);

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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint LoadIcon(nint instance, nint iconName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AppendMenu(nint menu, uint flags, uint itemId, string? text);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(
        nint menu,
        uint flags,
        int x,
        int y,
        int reserved,
        nint window,
        nint rectangle);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}
