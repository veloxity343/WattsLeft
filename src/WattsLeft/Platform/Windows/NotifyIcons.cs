using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace WattsLeft.Platform.Windows;

/// <summary>
/// Icons in Windows' notification area, through Shell_NotifyIcon. A hidden window of its own receives their
/// clicks, and Windows' notices that the taskbar was recreated (Explorer restarted, so the icons are added
/// again) and that a setting changed (light or dark mode). It runs on the UI thread: Avalonia's message loop
/// dispatches to every window on it.
/// </summary>
internal sealed unsafe partial class NotifyIcons : IDisposable
{
    /// <summary>One icon. Its id is fixed for its life; the image and tooltip change.</summary>
    public sealed class Icon
    {
        public uint Id { get; init; }
        internal IntPtr Handle;
        internal string Tooltip = "";
    }

    /// <summary>An icon was clicked: which, which button, and where (physical pixels).</summary>
    public event Action<Icon, bool, PixelPoint>? Clicked;

    /// <summary>Windows' light or dark mode, or another setting, changed.</summary>
    public event Action? SettingChanged;

    private static NotifyIcons? s_instance;
    private readonly IntPtr _window;
    private readonly List<Icon> _icons = [];
    private uint _nextId = 1;
    private readonly uint _taskbarCreated;

    private const string WindowClass = "WattsLeft.NotifyIcons";
    private const uint CallbackMessage = 0x8000 + 1; // WM_APP + 1

    public NotifyIcons()
    {
        s_instance = this;
        var module = Win32.ThisModule;
        fixed (char* name = WindowClass)
        {
            var cls = new WndClassEx
            {
                Size = (uint)sizeof(WndClassEx),
                WndProc = &WndProc,
                Instance = module,
                ClassName = name,
            };
            RegisterClassEx(cls);
        }
        _window = CreateWindowEx(0, WindowClass, "WattsLeft", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, module, IntPtr.Zero);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    }

    public Icon Add()
    {
        var icon = new Icon { Id = _nextId++ };
        _icons.Add(icon);
        Send(NimAdd, icon);
        return icon;
    }

    public void Remove(Icon icon)
    {
        Send(NimDelete, icon);
        _icons.Remove(icon);
        if (icon.Handle != IntPtr.Zero)
            DestroyIcon(icon.Handle);
        icon.Handle = IntPtr.Zero;
    }

    /// <summary>Shows a new image (an icon handle this takes ownership of) and tooltip.</summary>
    public void Update(Icon icon, IntPtr handle, string tooltip)
    {
        var old = icon.Handle;
        icon.Handle = handle;
        icon.Tooltip = tooltip;
        Send(NimModify, icon);
        if (old != IntPtr.Zero && old != handle)
            DestroyIcon(old);
    }

    /// <summary>The size Windows draws notification icons at, in pixels.</summary>
    public static int IconSize => GetSystemMetrics(SmCxSmIcon);

    private void Send(uint message, Icon icon)
    {
        var data = new NotifyIconData
        {
            Size = (uint)sizeof(NotifyIconData),
            Window = _window,
            Id = icon.Id,
            Flags = NifMessage | NifIcon | NifTip,
            CallbackMessage = CallbackMessage,
            Icon = icon.Handle,
        };
        // Windows shows at most 127 characters of a tooltip.
        var tip = icon.Tooltip.AsSpan(0, Math.Min(icon.Tooltip.Length, 127));
        tip.CopyTo(new Span<char>(data.Tip, 128));
        Shell_NotifyIcon(message, ref data);
    }

    [UnmanagedCallersOnly]
    private static IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (s_instance is { } self && hwnd == self._window)
            {
                if (message == CallbackMessage)
                {
                    int mouse = (int)lParam & 0xFFFF;
                    if (mouse is WmLButtonUp or WmRButtonUp && self._icons.FirstOrDefault(i => i.Id == (uint)wParam) is { } icon)
                    {
                        self.Clicked?.Invoke(icon, mouse == WmLButtonUp, Win32.PointerPosition());
                    }
                    return 0;
                }
                if (message == self._taskbarCreated)
                {
                    foreach (var icon in self._icons)
                        self.Send(NimAdd, icon);
                    return 0;
                }
                if (message == WmSettingChange)
                    self.SettingChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Log.Once($"Tray icon message failed: {ex.GetType().Name}: {ex.Message}");
        }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        foreach (var icon in _icons.ToList())
            Remove(icon);
        DestroyWindow(_window);
        if (s_instance == this)
            s_instance = null;
    }

    /// <summary>
    /// A drawn bitmap as an icon handle, for the notification area. The bitmap's pixels are premultiplied;
    /// an icon's are not. Zero if Windows couldn't make one (it's out of drawing resources).
    /// </summary>
    public static IntPtr ToIcon(Bitmap bitmap)
    {
        int width = bitmap.PixelSize.Width, height = bitmap.PixelSize.Height, stride = width * 4;
        var pixels = new byte[stride * height];
        fixed (byte* buffer = pixels)
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), (IntPtr)buffer, pixels.Length, stride);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte a = pixels[i + 3];
            if (a is 0 or 255)
                continue;
            for (int c = 0; c < 3; c++)
                pixels[i + c] = (byte)Math.Min(255, pixels[i + c] * 255 / a);
        }

        var header = new BitmapInfoHeader
        {
            Size = (uint)sizeof(BitmapInfoHeader), Width = width, Height = -height, Planes = 1, BitCount = 32,
        };
        IntPtr color = CreateDIBSection(IntPtr.Zero, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
        // The mask is all zeros: the colour bitmap's alpha decides what shows.
        var mask = new byte[(width + 15) / 16 * 2 * height];
        IntPtr maskBitmap;
        fixed (byte* m = mask)
            maskBitmap = CreateBitmap(width, height, 1, 1, (IntPtr)m);
        IntPtr icon = IntPtr.Zero;
        if (color != IntPtr.Zero && bits != IntPtr.Zero && maskBitmap != IntPtr.Zero)
        {
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            var info = new IconInfo { IsIcon = 1, Mask = maskBitmap, Color = color };
            icon = CreateIconIndirect(ref info);
        }
        if (icon == IntPtr.Zero)
            Log.Once("Couldn't make a tray icon image");
        if (color != IntPtr.Zero)
            DeleteObject(color);
        if (maskBitmap != IntPtr.Zero)
            DeleteObject(maskBitmap);
        return icon;
    }

    /// <summary>The app icon at <paramref name="size"/> pixels, from its nearest frame in the embedded .ico.</summary>
    public static IntPtr AppIcon(int size)
    {
        using var stream = typeof(NotifyIcons).Assembly.GetManifestResourceStream("WattsLeft.app.ico")!;
        var ico = new byte[stream.Length];
        stream.ReadExactly(ico);
        // The directory: a count, then 16-byte entries of width, height, ..., byte count and offset.
        int count = BitConverter.ToUInt16(ico, 4), best = -1, bestWidth = 0;
        for (int i = 0; i < count; i++)
        {
            int w = ico[6 + i * 16] == 0 ? 256 : ico[6 + i * 16];
            // The smallest frame at least this big, else the biggest there is.
            if (best < 0 || (w >= size && (bestWidth < size || w < bestWidth)) || (bestWidth < size && w > bestWidth))
                (best, bestWidth) = (i, w);
        }
        int length = BitConverter.ToInt32(ico, 6 + best * 16 + 8), offset = BitConverter.ToInt32(ico, 6 + best * 16 + 12);
        fixed (byte* frame = &ico[offset])
            return CreateIconFromResourceEx(frame, (uint)length, true, 0x00030000, size, size, 0);
    }

    // Win32.
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2;
    private const uint NifMessage = 1, NifIcon = 2, NifTip = 4;
    private const int WmLButtonUp = 0x0202, WmRButtonUp = 0x0205;
    private const uint WmSettingChange = 0x001A;
    private const int SmCxSmIcon = 49;

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id, Flags, CallbackMessage;
        public IntPtr Icon;
        public fixed char Tip[128];
        public uint State, StateMask;
        public fixed char Info[256];
        public uint TimeoutOrVersion;
        public fixed char InfoTitle[64];
        public uint InfoFlags;
        public Guid Item;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WndClassEx
    {
        public uint Size, Style;
        public delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr> WndProc;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public char* MenuName, ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int IsIcon, HotspotX, HotspotY;
        public IntPtr Mask, Color;
    }


    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW")]
    private static partial ushort RegisterClassEx(in WndClassEx cls);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y,
        int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(IntPtr hwnd);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static partial IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessage(string name);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr icon);

    [LibraryImport("user32.dll")]
    private static partial IntPtr CreateIconIndirect(ref IconInfo info);

    [LibraryImport("user32.dll")]
    private static partial IntPtr CreateIconFromResourceEx(byte* bits, uint size, [MarshalAs(UnmanagedType.Bool)] bool icon,
        uint version, int width, int height, uint flags);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader header, uint usage, out IntPtr bits,
        IntPtr section, uint offset);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateBitmap(int width, int height, uint planes, uint bitCount, IntPtr bits);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr obj);

}
