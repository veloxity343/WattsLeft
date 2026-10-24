using Avalonia;
using Avalonia.Threading;
using WattsLeft.Battery;
using WattsLeft.Platform.Windows;
using WattsLeft.Ui;

namespace WattsLeft;

/// <summary>
/// Reads the sources and keeps the tray up to date: one icon per device (or a placeholder when none are
/// connected), its menu, the flyout, pop-ups, battery history and the status file.
/// </summary>
internal sealed class TrayController : IDisposable
{
    // Connects, disconnects and Bluetooth battery changes arrive as events. Polling only catches battery
    // drift on sources without change events (USB / Xbox Wireless Adapter) and anything an event missed.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    // Device events come in bursts (a reconnect fires a run of Bluetooth updates). An event waits for a short
    // quiet spell, so a burst costs one refresh; a steady stream still refreshes at least once a second.
    private static readonly TimeSpan EventQuiet = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan EventMaxWait = TimeSpan.FromSeconds(1);

    private readonly IReadOnlyList<IBatterySource> _sources;
    private readonly Action _exit;
    private readonly NotifyIcons _tray = new();
    /// <summary>
    /// Tray icons are slots reused as devices come and go, so they keep their place in the taskbar. The first
    /// slot always exists and shows the placeholder when nothing is connected.
    /// </summary>
    private readonly List<TraySlot> _slots = [];
    private int _iconLimit = Settings.TrayIconLimit;
    private readonly DispatcherTimer _poll = new() { Interval = PollInterval };
    private readonly DispatcherTimer _eventTimer = new() { Interval = EventQuiet };
    private DateTime? _eventPendingSince;
    private readonly DispatcherTimer _settleTimer = new() { Interval = ControllerNotifier.SettleTime + TimeSpan.FromMilliseconds(100) };
    private readonly ControllerNotifier _notifier = new();
    /// <summary>
    /// Something connected or went: every source is read again then, not just at the next poll. Sources that
    /// have to ask their devices also poll on these themselves (<see cref="PollSchedule"/>).
    /// </summary>
    private readonly IDisposable _deviceNotices;
    private readonly BatteryHistory _history = new();
    /// <summary>Every device connected, removed ones included: history keeps recording them.</summary>
    private IReadOnlyList<ControllerBattery> _connected = [];
    /// <summary>The devices shown: connected, minus any removed, which stay out of the tray until they next connect.</summary>
    private IReadOnlyList<ControllerBattery> _readings = [];
    private IReadOnlySet<string> _hiddenIds = Settings.HiddenControllers.Keys.ToHashSet();
    /// <summary>Each connected device's time left (to the low level, or to full while charging), when known.</summary>
    private Dictionary<string, string> _timeLeft = [];
    private bool _lightTaskbar = Appearance.SystemUsesLight();
    private Flyout? _flyout;
    private TrayMenu? _menu;
    private SettingsWindow? _settingsWindow;
    private DateTime _flyoutClosedAt;
    /// <summary>Whether status.json is being kept; null until the settings are first applied.</summary>
    private bool? _shareStatus;
    private IReadOnlyDictionary<string, string> _names = Settings.ControllerNames;
    private IReadOnlySet<DeviceKind> _hiddenKinds = Settings.HiddenKinds;

    public TrayController(IReadOnlyList<IBatterySource> sources, Action exit)
    {
        _sources = sources;
        _exit = exit;
        ApplySettings();
        Settings.Changed += OnSettingsChanged;
        BatteryHistory.Trim();
        BatteryHistory.CloseOpenSessions();

        _tray.Clicked += OnTrayClicked;
        _tray.SettingChanged += OnSystemSettingChanged;
        _slots.Add(new TraySlot(_tray));
        _slots[0].Update(null, _lightTaskbar);

        foreach (var source in _sources)
            source.ControllersChanged += OnControllersChanged;
        _deviceNotices = DeviceNotices.Watch(() => OnControllersChanged(null, EventArgs.Empty));

        _eventTimer.Tick += (_, _) => RefreshForEvents();
        // Re-check once a newly connected device has settled, so its toast shows the settled reading.
        _settleTimer.Tick += (_, _) =>
        {
            _settleTimer.Stop();
            Refresh();
        };
        _poll.Tick += (_, _) => Refresh();
        _poll.Start();
        Refresh();

        Updates.Start(_notifier.ShowUpdate);
        AskAboutUpdatesOnce();
    }

    /// <summary>
    /// On the first start, asks once whether to check for updates. The answer is kept, and Settings changes it;
    /// closing the question answers no.
    /// </summary>
    private static void AskAboutUpdatesOnce()
    {
        if (Settings.CheckForUpdates is not null)
            return;
        // A moment after start-up, once the tray icon is in place.
        DispatcherTimer.RunOnce(async () =>
        {
            bool check = await ConfirmDialog.AskOnItsOwn("Check for updates?",
                "WattsLeft can ask GitHub once a day whether there's a newer version, and tell you when there is. " +
                "It never downloads or installs anything itself.\n\n" +
                "The check sends nothing about you or your devices, though GitHub sees your IP address, as any website " +
                "does. You can change this in Settings at any time.",
                "Check for updates", "Don't check", Appearance.For(Appearance.AppsUseLight()));
            Settings.CheckForUpdates = check;
            if (check)
                Updates.CheckNow();
        }, TimeSpan.FromSeconds(3));
    }

    private void OnControllersChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(OnControllersChangedUi);

    private void OnControllersChangedUi()
    {
        var now = DateTime.UtcNow;
        _eventPendingSince ??= now;
        if (now - _eventPendingSince >= EventMaxWait)
        {
            RefreshForEvents();
            return;
        }
        // Restart the quiet spell.
        _eventTimer.Stop();
        _eventTimer.Start();
    }

    private void RefreshForEvents()
    {
        _eventTimer.Stop();
        _eventPendingSince = null;
        Refresh();
    }

    /// <summary>The taskbar may have switched between light and dark.</summary>
    private void OnSystemSettingChanged()
    {
        Appearance.RaiseChanged();
        bool light = Appearance.SystemUsesLight();
        if (light == _lightTaskbar)
            return;
        _lightTaskbar = light;
        _flyout?.Dismiss(); // reopens in the new colours
        _menu?.Dismiss();
        Refresh();
    }

    private void Refresh()
    {
        var previous = _connected;
        var previouslyShown = _readings.Select(r => r.Id).ToHashSet();
        _connected = ReadAllSources();
        if (!_connected.SequenceEqual(previous))
        {
            LogHistory(_connected);
            _history.Record(_connected);

            // A removed device that connects (newly appears) is in use again: show it again. Only on
            // connecting, so one removed while connected stays hidden until it next connects.
            var hidden = Settings.HiddenControllers;
            var before = previous.Select(r => r.Id).ToHashSet();
            foreach (var reading in _connected.Where(r => !before.Contains(r.Id) && hidden.ContainsKey(r.Id)))
                Settings.Unhide(reading.Id);

            // An open Devices page shows connection state in its cards, so update it straight away.
            _settingsWindow?.ReadingsChanged();
        }

        // Removed devices leave the tray, the flyout, the menu and the pop-ups without a "disconnected" pop-up;
        // one restored while it was connected all along comes back without a "connected" one.
        _hiddenIds = Settings.HiddenControllers.Keys.ToHashSet();
        _readings = _hiddenIds.Count == 0 ? _connected : _connected.Where(r => !_hiddenIds.Contains(r.Id)).ToList();
        var stillConnected = previous.Select(r => r.Id).ToHashSet();
        _notifier.Forget(_connected.Where(r => _hiddenIds.Contains(r.Id)).Select(r => r.Id));
        _notifier.Adopt(_readings.Where(r => !previouslyShown.Contains(r.Id) && stillConnected.Contains(r.Id)));
        BatteryHistory.Heartbeat();

        _timeLeft = WorkOutTimeLeft();
        if (_shareStatus == true)
            StatusFile.Write(_readings, _timeLeft);
        UpdateSlots();
        _flyout?.SetReadings(Prioritise(_readings).ToList(), _timeLeft);

        if (_notifier.Update(_readings))
            _settleTimer.Start();
    }

    /// <summary>
    /// The Devices page's Until figure for each connected device, from its history: cheap, since the history
    /// is cached and only new rows are read.
    /// </summary>
    private Dictionary<string, string> WorkOutTimeLeft()
    {
        if (_readings.Count == 0)
            return [];
        var now = DateTime.Now;
        var history = BatteryHistory.Load(now - BatteryHistory.Kept).ToDictionary(s => s.Id);
        var timeLeft = new Dictionary<string, string>();
        foreach (var reading in _readings)
        {
            int? low = _notifier.WarningsFor(reading.Id).Low;
            if (history.TryGetValue(reading.Id, out var series) &&
                BatteryStats.TimeLeft(BatteryStats.Compute(series, now - TimeSpan.FromDays(7), now, low)) is { } text)
                timeLeft[reading.Id] = text;
        }
        return timeLeft;
    }

    private void OnTrayClicked(NotifyIcons.Icon icon, bool left, PixelPoint point)
    {
        if (left)
            ToggleFlyout(point);
        else
            ShowMenu(point);
    }

    private void ToggleFlyout(PixelPoint point)
    {
        if (_flyout is not null)
        {
            _flyout.Dismiss();
            return;
        }
        // Clicking the tray icon while the flyout is open closes it (it loses focus) just before the click
        // arrives; don't reopen it straight away.
        if (DateTime.UtcNow - _flyoutClosedAt < TimeSpan.FromMilliseconds(300))
            return;

        _menu?.Dismiss();
        var flyout = new Flyout(Prioritise(_readings).ToList(), _timeLeft, Appearance.For(_lightTaskbar));
        flyout.Closed += (_, _) =>
        {
            _flyoutClosedAt = DateTime.UtcNow;
            if (_flyout == flyout)
                _flyout = null;
        };
        flyout.SettingsRequested += ShowSettings;
        _flyout = flyout;
        flyout.ShowAt(point);
    }

    private void ShowMenu(PixelPoint point)
    {
        _menu?.Dismiss();
        _flyout?.Dismiss();
        var menu = new TrayMenu(BuildMenu(), Appearance.For(_lightTaskbar));
        menu.Closed += (_, _) =>
        {
            if (_menu == menu)
                _menu = null;
        };
        _menu = menu;
        menu.ShowAt(point);
    }

    /// <summary>Opens the settings window, or brings it forward if it's already open.</summary>
    private void ShowSettings()
    {
        if (_settingsWindow is null)
        {
            var window = new SettingsWindow();
            window.Closed += (_, _) =>
            {
                if (_settingsWindow == window)
                    _settingsWindow = null;
            };
            _settingsWindow = window;
            window.Show();
        }
        if (_settingsWindow.WindowState == Avalonia.Controls.WindowState.Minimized)
            _settingsWindow.WindowState = Avalonia.Controls.WindowState.Normal;
        _settingsWindow.Activate();
    }

    private bool _writingDiagnostics;

    /// <summary>
    /// Writes the diagnostics report and opens it, for bug reports. Off the UI thread: a live Bluetooth read
    /// can take a few seconds.
    /// </summary>
    private async void WriteDiagnostics()
    {
        if (_writingDiagnostics)
            return;
        _writingDiagnostics = true;
        try
        {
            string path = await Task.Run(() => Diagnostics.WriteReport(_sources));
            Win32.Open(path);
        }
        catch (Exception ex)
        {
            Log.Write($"Diagnostics report failed: {ex}");
        }
        finally
        {
            _writingDiagnostics = false;
        }
    }

    /// <summary>
    /// Shows the devices that most need attention, up to the icon limit: lowest battery first, charging ones
    /// last. The rest are listed in the last icon's tooltip and in the menu.
    /// </summary>
    private void UpdateSlots()
    {
        var shown = Prioritise(_readings).ToList();
        var hidden = new List<ControllerBattery>();
        if (_iconLimit > 0 && shown.Count > _iconLimit)
        {
            hidden = shown[_iconLimit..];
            shown = shown[.._iconLimit];
        }

        int count = Math.Max(1, shown.Count);
        while (_slots.Count < count)
            _slots.Add(new TraySlot(_tray));
        while (_slots.Count > count)
        {
            _slots[^1].Dispose();
            _slots.RemoveAt(_slots.Count - 1);
        }

        // Devices still shown keep their slot; newcomers fill the free ones.
        var assigned = new ControllerBattery?[count];
        var unplaced = new List<ControllerBattery>();
        foreach (var reading in shown)
        {
            int slot = _slots.FindIndex(s => s.Id == reading.Id);
            if (slot >= 0 && assigned[slot] is null)
                assigned[slot] = reading;
            else
                unplaced.Add(reading);
        }
        foreach (var reading in unplaced)
            assigned[Array.IndexOf(assigned, null)] = reading;

        string? more = hidden.Count == 0
            ? null
            : $"+{hidden.Count} more: {string.Join("; ", hidden.Select(r => r.Describe()))}";
        for (int i = 0; i < count; i++)
        {
            string? timeLeft = assigned[i] is { } reading ? _timeLeft.GetValueOrDefault(reading.Id) : null;
            _slots[i].Update(assigned[i], _lightTaskbar, i == count - 1 ? more : null, timeLeft);
        }
    }

    /// <summary>
    /// Another device takes a shown one's icon once it's this many points lower, so batteries that report in
    /// 1% steps don't trade places on every step, while 10% steps switch straight away.
    /// </summary>
    private const int TakeOverMargin = 3;

    private IEnumerable<ControllerBattery> Prioritise(IEnumerable<ControllerBattery> readings) =>
        readings
            .Select((reading, index) => (reading, index, shown: _slots.Any(s => s.Id == reading.Id)))
            .OrderBy(r => r.reading.Power is ChargeState.Charging or ChargeState.Wired ? 2
                : r.reading.EffectivePercent is null ? 1 : 0)
            // Half a point under the margin, so exactly the margin lower takes over.
            .ThenBy(r => (r.reading.EffectivePercent ?? 0) - (r.shown ? TakeOverMargin - 0.5 : 0))
            .ThenBy(r => r.shown ? 0 : 1)
            .ThenBy(r => r.index)
            .Select(r => r.reading);

    /// <summary>Records every change so battery behaviour (step sizes, update latency) can be checked later.</summary>
    private static void LogHistory(IReadOnlyList<ControllerBattery> readings) =>
        Log.Write(readings.Count == 0 ? "no devices" : string.Join(" | ", readings.Select(r => r.Describe())), Log.HistoryFile);

    /// <summary>
    /// How many reads in a row a failing source's last readings stand in for it. A failed read says nothing about
    /// what's connected, so its devices don't flicker off and on (with pop-ups) over a passing failure; one that
    /// keeps failing lets them go.
    /// </summary>
    private const int FailedReadsCovered = 3;

    /// <summary>Each source's last good readings, and how many reads have failed since.</summary>
    private readonly Dictionary<IBatterySource, (IReadOnlyList<ControllerBattery> Readings, int Failures)> _lastRead = [];

    /// <summary>Each source covers different devices; overlapping ones are grouped in a <see cref="FirstAvailableSource"/>.</summary>
    private List<ControllerBattery> ReadAllSources()
    {
        var readings = new List<ControllerBattery>();
        foreach (var source in _sources)
        {
            try
            {
                var read = source.Read();
                _lastRead[source] = (read, 0);
                readings.AddRange(read);
            }
            catch (Exception ex)
            {
                Log.Once($"{source.Name} failed: {ex.GetType().Name}: {ex.Message}");
                if (_lastRead.TryGetValue(source, out var last) && last.Failures < FailedReadsCovered)
                {
                    _lastRead[source] = (last.Readings, last.Failures + 1);
                    readings.AddRange(last.Readings);
                }
            }
        }
        // Types turned off in Settings are left out before anything else sees them: no icon, pop-ups or history.
        var hiddenKinds = Settings.HiddenKinds;
        if (hiddenKinds.Count > 0)
            readings.RemoveAll(r => hiddenKinds.Contains(r.Kind));
        var names = Settings.ControllerNames;
        return names.Count == 0 ? readings : [.. readings.Select(r => Settings.Named(r, names))];
    }

    private List<MenuEntry> BuildMenu()
    {
        var items = new List<MenuEntry>();
        if (_readings.Count == 0)
            items.Add(new MenuEntry("No devices connected", Enabled: false));
        foreach (var reading in _readings)
            items.Add(new MenuEntry(reading.Describe(), Enabled: false));
        if (Updates.Available is { } update)
        {
            items.Add(MenuEntry.Separator);
            items.Add(new MenuEntry($"Update to WattsLeft {update.Version.ToString(3)}", () => Updates.Offer(null), Enabled: Updates.Installing is null));
        }

        items.Add(MenuEntry.Separator);
        items.Add(new MenuEntry("Refresh now", Refresh));
        items.Add(new MenuEntry("Tray icons", Submenu:
            [.. IconLimitLabels.Select(l => new MenuEntry(l.Label, () => Settings.TrayIconLimit = l.Limit, Checked: _iconLimit == l.Limit))]));
        items.Add(new MenuEntry("Pop-up position", Submenu:
            [.. CornerLabels.Select(c => new MenuEntry(c.Label, () => Settings.ToastCorner = c.Corner, Checked: _notifier.Corner == c.Corner))]));
        items.Add(new MenuEntry("Connection pop-ups", () => Settings.ConnectionToasts = !_notifier.ConnectionToasts,
            Checked: _notifier.ConnectionToasts));
        // Both battery warnings at once; Settings has them separately. Ticked while either is on, so a click
        // turns everything off, and a second click turns both back on. The levels are left as they were.
        bool anyWarning = Settings.LowWarningEnabled || Settings.CriticalWarningEnabled;
        items.Add(new MenuEntry("Battery warnings", () =>
        {
            Settings.LowWarningEnabled = !anyWarning;
            Settings.CriticalWarningEnabled = !anyWarning;
        }, Checked: anyWarning));
        bool startWithWindows = Settings.StartWithWindows;
        items.Add(new MenuEntry("Start with Windows", () => Settings.StartWithWindows = !startWithWindows, Checked: startWithWindows));
        items.Add(MenuEntry.Separator);
        items.Add(new MenuEntry("Settings", ShowSettings));
        items.Add(new MenuEntry("Diagnostics", WriteDiagnostics));
        items.Add(new MenuEntry("Exit", _exit));
        return items;
    }

    private static readonly (int Limit, string Label)[] IconLimitLabels = [(1, "1"), (2, "2"), (0, "One per device")];

    private static readonly (ToastCorner Corner, string Label)[] CornerLabels =
    [
        (ToastCorner.TopRight, "Top right"), (ToastCorner.TopLeft, "Top left"),
        (ToastCorner.BottomRight, "Bottom right"), (ToastCorner.BottomLeft, "Bottom left"),
    ];

    private void OnSettingsChanged() => Dispatcher.UIThread.Post(ApplySettings);

    /// <summary>Applies the saved settings, from the tray menu or the settings window.</summary>
    private void ApplySettings()
    {
        _notifier.ConnectionToasts = Settings.ConnectionToasts;
        _notifier.HoldInFullscreen = Settings.HoldInFullscreen;
        _notifier.FullyChargedToasts = Settings.FullyChargedToasts;
        _notifier.WarningSounds = Settings.WarningSounds;
        var defaults = Settings.DefaultWarnings;
        var ownLevels = Settings.ControllerWarnings;
        _notifier.WarningsFor = id => ownLevels.GetValueOrDefault(id, defaults);

        var corner = Settings.ToastCorner;
        if (corner != _notifier.Corner)
        {
            _notifier.Corner = corner;
            // At startup, before the tray icons exist, the saved corner is just restored without a sample.
            if (_slots.Count > 0)
                _notifier.ShowSample();
        }

        bool share = Settings.ShareStatus;
        if (share != _shareStatus)
        {
            _shareStatus = share;
            if (share)
                StatusFile.Write(_readings, _timeLeft);
            else
                StatusFile.Delete();
        }

        // A rename, or a device type turned on or off, shows straight away: in the tray, the panel and the
        // status file.
        var names = Settings.ControllerNames;
        var hiddenKinds = Settings.HiddenKinds;
        if (!names.OrderBy(kv => kv.Key).SequenceEqual(_names.OrderBy(kv => kv.Key)) || !hiddenKinds.SetEquals(_hiddenKinds))
        {
            _names = names;
            _hiddenKinds = hiddenKinds;
            if (_slots.Count > 0)
                Refresh();
        }

        // A device removed or restored in the settings window leaves or joins the tray straight away.
        if (!Settings.HiddenControllers.Keys.ToHashSet().SetEquals(_hiddenIds) && _slots.Count > 0)
            Refresh();

        int limit = Settings.TrayIconLimit;
        if (limit != _iconLimit)
        {
            _iconLimit = limit;
            UpdateSlots();
        }
    }

    public void Dispose()
    {
        _poll.Stop();
        _deviceNotices.Dispose();
        Updates.Stop();
        _eventTimer.Stop();
        _settleTimer.Stop();
        _notifier.Dispose();
        Settings.Changed -= OnSettingsChanged;
        foreach (var source in _sources)
        {
            source.ControllersChanged -= OnControllersChanged;
            (source as IDisposable)?.Dispose();
        }
        _flyout?.Close();
        _menu?.Close();
        _settingsWindow?.Close();
        _history.Record([]); // close every device's session on a clean exit
        if (_shareStatus == true)
            StatusFile.Write([], new Dictionary<string, string>(), running: false);
        foreach (var slot in _slots)
            slot.Dispose();
        _tray.Dispose();
    }
}

/// <summary>A single tray icon and the reading it currently shows.</summary>
internal sealed class TraySlot(NotifyIcons tray) : IDisposable
{
    private readonly NotifyIcons.Icon _icon = tray.Add();
    private ControllerBattery? _shown;
    private bool? _shownLightTaskbar;
    private string? _shownMore;
    private string? _shownTimeLeft;
    private bool _drawn;

    /// <summary>The device it shows, or null for the placeholder.</summary>
    public string? Id => _shown?.Id;

    /// <param name="battery">null shows the "no devices" placeholder.</param>
    /// <param name="more">Devices without an icon of their own, appended to the tooltip.</param>
    /// <param name="timeLeft">e.g. "3 h to 20%", added to the tooltip when known.</param>
    public void Update(ControllerBattery? battery, bool lightTaskbar, string? more = null, string? timeLeft = null)
    {
        if (_drawn && battery == _shown && lightTaskbar == _shownLightTaskbar && more == _shownMore && timeLeft == _shownTimeLeft)
            return;

        int size = NotifyIcons.IconSize;
        IntPtr image;
        if (battery is null)
        {
            image = NotifyIcons.AppIcon(size);
        }
        else
        {
            using var bitmap = BatteryIcon.Render(battery, lightTaskbar, size);
            image = NotifyIcons.ToIcon(bitmap);
        }
        // Without an image, keep showing the last one, and try again at the next refresh.
        if (image == IntPtr.Zero)
            return;

        string text = battery?.Describe() ?? "WattsLeft: no devices connected";
        if (battery is not null && timeLeft is not null)
            text += $", about {timeLeft}";
        if (more is not null)
            text += "\n" + more;
        tray.Update(_icon, image, text);

        _shown = battery;
        _shownLightTaskbar = lightTaskbar;
        _shownMore = more;
        _shownTimeLeft = timeLeft;
        _drawn = true;
    }

    public void Dispose() => tray.Remove(_icon);
}
