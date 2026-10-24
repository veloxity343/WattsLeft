using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using WattsLeft.Platform.Windows;

namespace WattsLeft.Ui;

/// <summary>
/// A borderless window that pops up over everything, like Windows' own flyouts and menus: rounded corners,
/// kept out of Alt+Tab, closed by Esc, by losing focus, and by any click outside it. Clicks are watched
/// as well as focus because a tray click can leave a new window without focus, and a window that never had
/// focus is never deactivated.
/// </summary>
internal partial class PopupWindow : Window
{
    private readonly Appearance.Corners _corners;
    private readonly Color _border;
    private bool _closing;

    /// <summary>The open pop-ups, newest last; a click outside all of them closes them all.</summary>
    private static readonly List<PopupWindow> Open = [];

    public PopupWindow(Palette palette, Appearance.Corners corners)
    {
        _corners = corners;
        _border = palette.Border;
        WindowDecorations = WindowDecorations.None;
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        Background = new SolidColorBrush(palette.Background);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Deactivated += (_, _) =>
        {
            if (CloseOnDeactivate)
                Dismiss();
        };
    }

    /// <summary>The window's handle, once it's been shown.</summary>
    protected IntPtr Hwnd => TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        var hwnd = Hwnd;
        Appearance.StyleWindow(hwnd, _corners, _border);
        Win32.MakeToolWindow(hwnd);
        SetForegroundWindow(hwnd);
        Open.Add(this);
        InstallHook();
    }

    protected override void OnClosed(EventArgs e)
    {
        Open.Remove(this);
        if (Open.Count == 0)
            RemoveHook();
        base.OnClosed(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            Dismiss();
            e.Handled = true;
        }
    }

    /// <summary>Whether losing focus closes it. A menu with a submenu open hands focus to the submenu.</summary>
    protected virtual bool CloseOnDeactivate => true;

    /// <summary>Closes it, with a quick fade if animations are on. Safe to call more than once.</summary>
    public virtual void Dismiss()
    {
        if (_closing)
            return;
        _closing = true;
        if (!Appearance.AnimationsEnabled())
        {
            Close();
            return;
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            double t = Math.Min(1, clock.Elapsed.TotalMilliseconds / 100);
            Opacity = 1 - t;
            if (t >= 1)
            {
                timer.Stop();
                Close();
            }
        };
        timer.Start();
    }

    public bool IsClosing => _closing;

    /// <summary>Whether a point on screen (physical pixels) is inside this window.</summary>
    private bool Contains(int x, int y) => GetWindowRect(Hwnd, out var r) && x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;

    // The low-level mouse hook, installed while any pop-up is open.
    private const int WhMouseLowLevel = 14;
    private const int WmLButtonDown = 0x0201, WmRButtonDown = 0x0204, WmMButtonDown = 0x0207, WmXButtonDown = 0x020B;
    private static IntPtr s_hook;

    private static unsafe void InstallHook()
    {
        if (s_hook == IntPtr.Zero)
            s_hook = SetWindowsHookEx(WhMouseLowLevel, &OnMouse, Win32.ThisModule, 0);
    }

    private static void RemoveHook()
    {
        if (s_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(s_hook);
            s_hook = IntPtr.Zero;
        }
    }

    /// <summary>Runs on the UI thread, which installed the hook; keep it quick and defer the work.</summary>
    [UnmanagedCallersOnly]
    private static IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (int)wParam is WmLButtonDown or WmRButtonDown or WmMButtonDown or WmXButtonDown)
        {
            // The hook's data starts with the cursor position, in physical pixels.
            int x = Marshal.ReadInt32(lParam), y = Marshal.ReadInt32(lParam, 4);
            if (Open.Count > 0 && !Open.Any(p => p.Contains(x, y)))
            {
                var open = Open.ToList();
                Dispatcher.UIThread.Post(() =>
                {
                    foreach (var popup in open)
                        popup.Dismiss();
                });
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    // Win32.

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static unsafe partial IntPtr SetWindowsHookEx(int hookId, delegate* unmanaged<int, IntPtr, IntPtr, IntPtr> callback,
        IntPtr module, uint threadId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(IntPtr hook);

    [LibraryImport("user32.dll")]
    private static partial IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

}
