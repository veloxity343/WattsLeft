using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;

namespace WattsLeft.Platform.Windows;

/// <summary>The small Windows calls the interface shares: one place each, rather than a copy per window.</summary>
internal static partial class Win32
{
    /// <summary>The pointer's position on screen, in physical pixels.</summary>
    public static PixelPoint PointerPosition() => GetCursorPos(out var point) ? new PixelPoint(point.X, point.Y) : default;

    /// <summary>This program's module, which window classes and hooks are registered against.</summary>
    public static IntPtr ThisModule => GetModuleHandle(null);

    /// <summary>
    /// Keeps a window out of Alt+Tab and the taskbar; with <paramref name="neverActivate"/>, it never takes
    /// focus either, so it can't pull a game out of fullscreen.
    /// </summary>
    public static void MakeToolWindow(IntPtr hwnd, bool neverActivate = false)
    {
        long style = GetWindowLongPtr(hwnd, GwlExStyle) | WsExToolWindow;
        if (neverActivate)
            style |= WsExNoActivate;
        SetWindowLongPtr(hwnd, GwlExStyle, style);
    }

    /// <summary>
    /// Opens a file, folder or web address as a double-click would: in File Explorer, the browser, or the
    /// file's app. False, and logged, if Windows couldn't.
    /// </summary>
    public static bool Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"Opening {target} failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080, WsExNoActivate = 0x08000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out NativePoint point);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr GetModuleHandle(string? name);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial long GetWindowLongPtr(IntPtr hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial long SetWindowLongPtr(IntPtr hwnd, int index, long value);
}
