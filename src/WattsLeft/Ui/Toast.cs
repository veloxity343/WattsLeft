using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using WattsLeft.Battery;
using WattsLeft.Platform.Windows;

namespace WattsLeft.Ui;

/// <summary>What a toast shows: the device's battery icon, its name, and one line of status.</summary>
/// <param name="Battery">Drawn as the icon; null draws the app icon, used for "disconnected".</param>
/// <param name="Accent">Colour of the thin bar on the left edge.</param>
/// <param name="Clicked">What clicking it does, besides closing it.</param>
internal sealed record ToastContent(string Title, string Message, ControllerBattery? Battery, Color Accent, Action? Clicked = null);

/// <summary>Stacks toasts in a corner of the primary screen, newest nearest the corner.</summary>
internal sealed class ToastManager : IDisposable
{
    private const int MaxVisible = 4, Margin = 12, Gap = 12;
    private readonly List<ToastWindow> _open = [];
    /// <summary>Toasts pushed out by newer ones: fading where they are, no longer part of the stack.</summary>
    private readonly List<ToastWindow> _leaving = [];
    private ToastCorner _corner = ToastCorner.TopRight;

    public ToastCorner Corner
    {
        get => _corner;
        set
        {
            _corner = value;
            Layout();
        }
    }

    public void Show(ToastContent content)
    {
        if (_open.Count >= MaxVisible)
        {
            // The oldest makes way at once, so the stack never grows past the limit; it fades out in its place.
            _leaving.Add(_open[0]);
            _open[0].Dismiss();
            _open.RemoveAt(0);
        }

        var toast = new ToastWindow(content);
        toast.Closed += (_, _) =>
        {
            _leaving.Remove(toast);
            if (_open.Remove(toast))
                Layout();
        };
        _open.Add(toast);
        Layout();
        toast.Show();
    }

    private void Layout()
    {
        if (_open.Count == 0)
            return;
        var screen = _open[0].Screens.Primary ?? _open[0].Screens.All[0];
        var area = screen.WorkingArea;
        double scale = screen.Scaling;
        int margin = (int)Math.Round(Margin * scale), gap = (int)Math.Round(Gap * scale);
        int width = (int)Math.Round(ToastWindow.LogicalWidth * scale), height = (int)Math.Round(ToastWindow.LogicalHeight * scale);
        bool top = _corner is ToastCorner.TopRight or ToastCorner.TopLeft;
        bool right = _corner is ToastCorner.TopRight or ToastCorner.BottomRight;
        int x = right ? area.Right - width - margin : area.X + margin;
        int y = top ? area.Y + margin : area.Bottom - height - margin;
        for (int i = _open.Count - 1; i >= 0; i--)
        {
            _open[i].Position = new PixelPoint(x, y);
            y += top ? height + gap : -(height + gap);
        }
    }

    public void Dispose()
    {
        foreach (var toast in _open.Concat(_leaving).ToList())
            toast.Close();
        _open.Clear();
        _leaving.Clear();
    }
}

/// <summary>
/// A small borderless, topmost window that never takes focus from the game or app underneath. Fades in,
/// waits, fades out; clicking dismisses it early.
/// </summary>
internal sealed class ToastWindow : Window
{
    /// <summary>Size at 96 DPI: a single line, half the height of a Steam notification.</summary>
    public const int LogicalWidth = 380, LogicalHeight = 30;

    private static readonly TimeSpan HoldTime = TimeSpan.FromSeconds(4);
    private const double MaxOpacity = 0.96, FadeStep = 0.12;

    private readonly DispatcherTimer _animation = new() { Interval = TimeSpan.FromMilliseconds(15) };
    private DateTime _holdUntil;
    private bool _fadingOut;

    private readonly Action? _clicked;

    public ToastWindow(ToastContent content)
    {
        _clicked = content.Clicked;
        WindowDecorations = WindowDecorations.None;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = LogicalWidth;
        Height = LogicalHeight;
        Opacity = 0;
        Background = Draw.Brush(ToastView.Background);
        Cursor = new Cursor(StandardCursorType.Hand);
        Content = new ToastView(content);
        _animation.Tick += (_, _) => Animate();
    }

    public void Dismiss() => _fadingOut = true;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (TryGetPlatformHandle()?.Handle is IntPtr hwnd)
        {
            Appearance.StyleWindow(hwnd, Appearance.Corners.Round);
            // Never activated, and kept out of Alt+Tab.
            Win32.MakeToolWindow(hwnd, neverActivate: true);
        }
        _holdUntil = DateTime.UtcNow + HoldTime;
        _animation.Start();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton == MouseButton.Left)
            _clicked?.Invoke();
        Dismiss();
    }

    private void Animate()
    {
        if (_fadingOut)
        {
            Opacity = Math.Max(0, Opacity - FadeStep / 2);
            if (Opacity <= 0)
            {
                _animation.Stop();
                Close();
            }
        }
        else if (Opacity < MaxOpacity)
        {
            Opacity = Math.Min(MaxOpacity, Opacity + FadeStep);
        }
        else if (DateTime.UtcNow >= _holdUntil)
        {
            _fadingOut = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _animation.Stop();
        base.OnClosed(e);
    }

}

/// <summary>A toast's contents: the accent bar, the icon, then the title and message on one line.</summary>
internal sealed class ToastView(ToastContent content) : Control
{
    public static readonly Color Background = Color.FromRgb(32, 32, 32);
    private static readonly Color TitleColor = Colors.White, MessageColor = Color.FromRgb(190, 190, 190);
    private Bitmap? _glyph;

    public override void Render(DrawingContext g)
    {
        var bounds = new Rect(Bounds.Size);
        g.FillRectangle(Draw.Brush(Background), bounds);
        g.FillRectangle(Draw.Brush(content.Accent), new Rect(0, 0, 3, bounds.Height));

        const int glyphSize = 22;
        int pixels = (int)Math.Round(glyphSize * (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1));
        if (_glyph?.PixelSize.Width != pixels)
        {
            _glyph?.Dispose();
            _glyph = content.Battery is { } battery ? BatteryIcon.Render(battery, lightBackground: false, pixels) : BatteryIcon.AppIcon(pixels);
        }
        g.DrawImage(_glyph, new Rect(10, (bounds.Height - glyphSize) / 2, glyphSize, glyphSize));

        // One line: the title, then the message in grey. The title gives way first if space runs out.
        double textX = 10 + glyphSize + 9, textRight = bounds.Width - 12, gap = 8;
        var titleFont = Draw.Font(semibold: true);
        double messageWidth = Math.Min(Draw.Width(content.Message, Draw.Font(), 12), textRight - textX);
        double titleWidth = Math.Min(Draw.Width(content.Title, titleFont, 12.5), Math.Max(0, textRight - textX - messageWidth - gap));
        Draw.Text(g, content.Title, titleFont, 12.5, TitleColor, new Rect(textX, 0, titleWidth + 1, bounds.Height));
        Draw.Text(g, content.Message, Draw.Font(), 12, MessageColor, new Rect(textX + titleWidth + gap, 0, messageWidth + 1, bounds.Height));
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _glyph?.Dispose();
        _glyph = null;
    }
}
