using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using WattsLeft.Battery;
using WattsLeft.Platform.Windows;

namespace WattsLeft.Ui;

/// <summary>
/// The full window behind the flyout's gear: navigation on the left, a page on the right. Follows Windows'
/// app mode, light or dark, and stays in step with the tray menu through <see cref="Settings"/>.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private const double NavWidth = 220, PagePadding = 32;

    private readonly List<NavItem> _nav = [];
    private readonly List<PagePanel> _pages = [];
    private readonly ScrollViewer _scroller = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private int _current;
    private readonly int _controllersPage;
    private Palette _palette;
    /// <summary>Set for the preview render, which fixes the palette instead of following Windows.</summary>
    private readonly bool _fixedPalette;
    /// <summary>Where the history chart's data comes from; the preview swaps in sample data.</summary>
    private readonly Func<DateTime, List<HistorySeries>> _loadHistory;
    /// <summary>Showing sample data: never let it change the real settings.</summary>
    private readonly bool _sampleData;
    /// <summary>Set while controls are filled from Settings, so they don't write the same values back.</summary>
    private bool _loading;

    // The Settings page's controls, kept to refresh them when settings change elsewhere (the tray menu).
    /// <summary>The Show chips, in order, and the device type each stands for.</summary>
    private static readonly DeviceKind[] ShownKinds =
        [DeviceKind.Gamepad, DeviceKind.Mouse, DeviceKind.Keyboard, DeviceKind.Headset, DeviceKind.Other];
    private readonly ChipGroup _shownKinds = new(["Controllers", "Mice", "Keyboards", "Headsets", "Other"], 88);
    private readonly SegmentedControl _iconLimit = new(["1", "2", "One per device"], 130);
    private readonly SegmentedControl _corner = new(["Top left", "Top right", "Bottom left", "Bottom right"], 105);
    private readonly ToggleSwitch _connectionToasts = new();
    private readonly ToggleSwitch _holdInFullscreen = new();
    private readonly ToggleSwitch _fullyCharged = new();
    private readonly SwitchedStepper _low = new(Settings.MinWarning + Settings.WarningStep, Settings.MaxWarning, Settings.WarningStep);
    private readonly SwitchedStepper _critical = new(Settings.MinWarning, Settings.MaxWarning - Settings.WarningStep, Settings.WarningStep);
    private readonly ToggleSwitch _warningSounds = new();
    private readonly ToggleSwitch _shareStatus = new();
    private readonly ToggleSwitch _startWithWindows = new();
    private readonly ToggleSwitch _checkUpdates = new();
    /// <summary>The update check's result, and a way to the new version's page when there is one.</summary>
    private readonly StatusLine _updateStatus = new();
    private readonly ActionButton _openDataFolder = new("Open folder");

    // The Devices page: each device's battery history, its stats, and removing it.
    private readonly SegmentedControl _range = new(["2 hours", "6 hours", "24 hours", "7 days"], 95) { SelectedIndex = 1 };
    private static readonly TimeSpan[] Ranges = [TimeSpan.FromHours(2), TimeSpan.FromHours(6), TimeSpan.FromHours(24), TimeSpan.FromDays(7)];
    private readonly HistoryChart _chart = new();
    private readonly Note _chartNote = new("Click a device's name above the chart to leave its line out, or bring it back.");
    private readonly Note _statsNote = new("Averages and totals cover the range above. Predictions and health use recent history.");
    private readonly Note _historyNote = new($"Recorded whenever a device's battery changes, and kept for {BatteryHistory.Kept.Days} days.");
    private readonly List<StatsCard> _statsCards = [];
    private readonly UndoBar _undoBar = new();
    /// <summary>The device the undo bar would restore.</summary>
    private string? _undoId;
    /// <summary>Beside the dropdown, while any devices are removed: lists them to restore.</summary>
    private readonly ActionButton _restoreButton = new("Restore removed");
    private List<(string Id, string Name, DateTime When)> _removed = [];
    private readonly Note _versionNote = new($"WattsLeft {AppInfo.Version}");
    /// <summary>Which device's figures to show: all of them until the user picks one, then their pick.</summary>
    private readonly DropDown _controllerPicker = new(340);
    private const string AllControllers = "all";
    /// <summary>A device id, or <see cref="AllControllers"/>.</summary>
    private string _pickerChoice = AllControllers;
    /// <summary>What each of the picker's items stands for: a device id, then <see cref="AllControllers"/>.</summary>
    private List<string> _pickerKeys = [];
    /// <summary>Keeps the chart's "now" edge moving while the Devices page is open.</summary>
    private readonly DispatcherTimer _historyTimer = new() { Interval = TimeSpan.FromSeconds(30) };

    private static readonly int[] IconLimits = [1, 2, 0];
    private static readonly ToastCorner[] Corners = [ToastCorner.TopLeft, ToastCorner.TopRight, ToastCorner.BottomLeft, ToastCorner.BottomRight];

    public SettingsWindow(Palette? palette = null, Func<DateTime, List<HistorySeries>>? loadHistory = null)
    {
        _palette = palette ?? Appearance.For(Appearance.AppsUseLight());
        _fixedPalette = palette is not null;
        _loadHistory = loadHistory ?? BatteryHistory.Load;
        _sampleData = loadHistory is not null;
        Title = "WattsLeft";
        using (var icon = typeof(SettingsWindow).Assembly.GetManifestResourceStream("WattsLeft.app.ico")!)
            Icon = new WindowIcon(icon);
        WindowStartupLocation = WindowStartupLocation.Manual;

        var nav = new Canvas { Width = NavWidth };
        var root = new DockPanel();
        DockPanel.SetDock(nav, Dock.Left);
        root.Children.Add(nav);
        root.Children.Add(_scroller);
        Content = root;

        AddPage("Settings", BuildSettingsPage(), nav);
        AddPage("Devices", BuildControllersPage(), nav);
        _controllersPage = _pages.Count - 1;
        UpdateMinimumSize();
        SizeAndCentre();
        ShowPage(0);

        LoadValues();
        WireEvents();
        ApplyPalette();

        Settings.Changed += OnSettingsChanged;
        Appearance.Changed += OnAppearanceChanged;
        Updates.Changed += ShowUpdateStatus;
    }

    /// <summary>The size it opens at, room for every page's widest row and a good stretch of the chart.</summary>
    private const double OpenWidth = 900, OpenHeight = 700;

    /// <summary>
    /// Opens at <see cref="OpenWidth"/> by <see cref="OpenHeight"/>, or as much of it as the screen it opens on has
    /// room for, centred on that screen (the one with the pointer: the tray's).
    /// </summary>
    private void SizeAndCentre()
    {
        var screen = Screens.ScreenFromPoint(Win32.PointerPosition()) ?? Screens.Primary;
        if (screen is null)
        {
            (Width, Height) = (OpenWidth, OpenHeight);
            return;
        }
        double scale = screen.Scaling;
        var area = screen.WorkingArea;
        // The title bar and frame take some room too.
        double width = Math.Max(MinWidth, Math.Min(OpenWidth, area.Width / scale - 16));
        double height = Math.Max(MinHeight, Math.Min(OpenHeight, area.Height / scale - 40));
        (Width, Height) = (width, height);
        Position = new PixelPoint(area.X + Math.Max(0, (int)((area.Width - width * scale) / 2)),
            area.Y + Math.Max(0, (int)((area.Height - (height + 32) * scale) / 2)));
    }

    /// <summary>
    /// The smallest the window can be with nothing cut off: the navigation, the page's padding and scroll bar,
    /// and the widest thing on any page (the device-type chips, a device's card at one column). Narrower than
    /// that, everything wraps or reflows instead.
    /// </summary>
    private void UpdateMinimumSize()
    {
        double content = _pages.SelectMany(p => p.Items).OfType<IPageItem>().Select(c => c.MinimumWidth).DefaultIfEmpty(0).Max();
        MinWidth = NavWidth + PagePadding * 2 + content + 16;
        MinHeight = 420;
    }

    private void AddPage(string title, List<Control> items, Canvas navPanel)
    {
        int index = _pages.Count;
        _pages.Add(new PagePanel(this, items));
        var nav = new NavItem(title) { Width = NavWidth - 24, Height = 36 };
        Canvas.SetLeft(nav, 12);
        Canvas.SetTop(nav, PagePadding + 44 + index * 40); // level with the page title's baseline area
        nav.Click += () => ShowPage(index);
        _nav.Add(nav);
        navPanel.Children.Add(nav);
    }

    public int PageCount => _pages.Count;

    /// <summary>The scrolling area the pages show in, for the preview to scroll through.</summary>
    internal ScrollViewer PageScroller => _scroller;

    /// <summary>Where the page area starts, right of the navigation.</summary>
    internal static double PageLeft => NavWidth;

    public void ShowPage(int index)
    {
        _current = index;
        for (int i = 0; i < _nav.Count; i++)
            _nav[i].Selected = i == index;
        _scroller.Content = _pages[index];
        _scroller.Offset = default;
        if (index == _controllersPage)
        {
            ReloadHistory();
            _historyTimer.Start();
        }
        else
        {
            _historyTimer.Stop();
        }
    }

    private List<Control> BuildSettingsPage()
    {
        var items = new List<Control> { new Heading("Settings", 26) };

        items.Add(new Heading("Devices", 15));
        items.Add(new SettingCard("Show", "Kinds of device to watch. Ones turned off get no icon, pop-ups or history.", _shownKinds, stacked: true));

        items.Add(new Heading("Tray", 15));
        items.Add(new SettingCard("Tray icons", "How many devices get their own icon. The rest are listed when you hover or click.",
            _iconLimit, stacked: true));

        items.Add(new Heading("Pop-ups", 15));
        items.Add(new SettingCard("Position", "Which corner of the screen pop-ups appear in.", _corner, stacked: true));
        items.Add(new SettingCard("Connection pop-ups", "When a device connects or disconnects.", _connectionToasts));
        items.Add(new SettingCard("Fully charged", "When a charging device reaches 100%.", _fullyCharged));
        items.Add(new SettingCard("Hold while a game is fullscreen", "Show them once the game closes.", _holdInFullscreen));

        items.Add(new Heading("Battery warnings", 15));
        items.Add(new Note("Pop-ups when a device's battery runs down, whatever the connection pop-ups setting."));
        items.Add(new Note("A device can have its own levels, set on its card on the Devices page."));
        items.Add(new SettingCard("Low battery", "Warn when it drops to this level.", _low));
        items.Add(new SettingCard("Critical battery", "Warn again at this lower level.", _critical));
        items.Add(new SettingCard("Play a sound", "A short chime, even while a game holds the pop-up.", _warningSounds));

        items.Add(new Heading("Other apps", 15));
        items.Add(new Note($"Battery levels in {StatusFile.DisplayPath}, for other apps to read."));
        items.Add(new SettingCard("Share battery levels", "Such as Rainmeter skins, Stream Deck plugins or scripts.", _shareStatus));

        items.Add(new Heading("Updates", 15));
        items.Add(new SettingCard("Check for updates",
            "Once a day, from GitHub. WattsLeft tells you when there's a new version, and updates itself when you say so.",
            _checkUpdates));
        items.Add(_updateStatus);

        items.Add(new Heading("Startup", 15));
        items.Add(new SettingCard("Start with Windows", "Run WattsLeft when you sign in.", _startWithWindows));

        items.Add(new Heading("Your data", 15));
        items.Add(new SettingCard("Data folder",
            @"Battery history and logs, in %LOCALAPPDATA%\WattsLeft. Settings are kept in the registry. Deleting the folder resets the history.",
            _openDataFolder));

        items.Add(_versionNote);
        return items;
    }

    private List<Control> BuildControllersPage()
    {
        _range.SelectedChanged += (_, _) => ReloadHistory();
        _chart.LegendClicked += (_, id) =>
        {
            if (!_sampleData)
                Settings.SetInChart(id, Settings.LeftOutOfChart.Contains(id));
        };
        _historyTimer.Tick += (_, _) => ReloadHistory();
        _controllerPicker.SelectedChanged += (_, _) =>
        {
            _pickerChoice = _pickerKeys[_controllerPicker.SelectedIndex];
            ReloadHistory();
        };
        _undoBar.ShownChanged += (_, _) => _pages[_controllersPage].InvalidateMeasure();
        _restoreButton.Click += (_, _) => OpenRestoreList();
        return [new Heading("Devices", 26), _undoBar, _range, _chart, _chartNote, _controllerPicker, _restoreButton, _statsNote, _historyNote];
    }

    private void ReloadHistory()
    {
        var now = DateTime.Now;
        var since = now - Ranges[_range.SelectedIndex];
        var full = _loadHistory(now - BatteryHistory.Kept).ToDictionary(s => s.Id);
        // Removed devices stay out until they next connect, when the tray app unhides them.
        var hidden = Settings.HiddenControllers;
        var shown = _loadHistory(since).Where(s => !hidden.ContainsKey(s.Id)).ToList();
        // Restored another way (Restore removed, or it reconnected): there's nothing left to undo.
        if (_undoBar.Showing && _undoId is not null && !hidden.ContainsKey(_undoId))
            _undoBar.Dismiss();
        _removed = RemovedControllers(full);
        _restoreButton.Text = $"Restore removed ({_removed.Count})";
        _restoreButton.IsVisible = _removed.Count > 0;
        _chart.EmptyMessage = shown.Count == 0 && hidden.Count > 0
            ? "Every device with history here has been removed. Use Restore removed below to bring one back."
            : HistoryChart.NoHistoryMessage;
        _chart.SetData(shown, since, now, _sampleData ? new HashSet<string>() : Settings.LeftOutOfChart);

        // A card per device in the chart, its figures worked out from its whole retained history.
        var page = _pages[_controllersPage];

        // The picker lists each device, then all, which it starts on.
        if (_pickerChoice != AllControllers && !shown.Any(s => s.Id == _pickerChoice))
            _pickerChoice = AllControllers; // the chosen device isn't in this range
        string showing = _pickerChoice;
        _pickerKeys = [.. shown.Select(s => s.Id), AllControllers];
        _controllerPicker.SetItems([.. shown.Select(s => s.Name), "All devices"], Math.Max(0, _pickerKeys.IndexOf(showing)));
        _controllerPicker.IsVisible = shown.Count > 0;

        var cards = showing == AllControllers ? shown : shown.Where(s => s.Id == showing).ToList();

        // A device already showing keeps its card, updated in place, so a stepper in use keeps its focus.
        var defaults = Settings.DefaultWarnings;
        var ownLevels = Settings.ControllerWarnings;
        var existing = _statsCards.ToDictionary(c => c.Id);
        _statsCards.Clear();
        foreach (var series in cards)
        {
            bool own = ownLevels.TryGetValue(series.Id, out var levels);
            levels ??= defaults;
            var stats = BatteryStats.Compute(full.GetValueOrDefault(series.Id, series), since, now, levels.Low);
            if (existing.Remove(series.Id, out var card))
            {
                card.Update(series, stats, levels, own);
            }
            else
            {
                card = new StatsCard(series, stats, levels, own) { Palette = _palette };
                string id = series.Id;
                var made = card;
                // Looked up when clicked, since the card outlives the series it was made with.
                card.WarningsChanged += (_, changed) =>
                {
                    if (!_sampleData)
                        Settings.SetWarnings(id, changed);
                };
                card.RenameRequested += (_, _) => Rename(made.Series);
                card.RemoveRequested += (_, _) => Remove(made.Series);
            }
            _statsCards.Add(card);
        }
        page.SetCards(_statsCards, after: _statsNote);
        _statsNote.IsVisible = shown.Count > 0;
        _chartNote.IsVisible = shown.Count > 0;
        UpdateMinimumSize(); // a card's widest tile can be wider than before
    }

    private void LoadValues()
    {
        _loading = true;
        var hiddenKinds = Settings.HiddenKinds;
        for (int i = 0; i < ShownKinds.Length; i++)
            _shownKinds[i] = !hiddenKinds.Contains(ShownKinds[i]);
        _iconLimit.SelectedIndex = Math.Max(0, Array.IndexOf(IconLimits, Settings.TrayIconLimit));
        _corner.SelectedIndex = Math.Max(0, Array.IndexOf(Corners, Settings.ToastCorner));
        _connectionToasts.Checked = Settings.ConnectionToasts;
        _holdInFullscreen.Checked = Settings.HoldInFullscreen;
        _fullyCharged.Checked = Settings.FullyChargedToasts;
        _low.Switch.Checked = Settings.LowWarningEnabled;
        _low.Stepper.IsEnabled = _low.Switch.Checked;
        _critical.Switch.Checked = Settings.CriticalWarningEnabled;
        _critical.Stepper.IsEnabled = _critical.Switch.Checked;
        _low.Stepper.Value = Settings.LowWarning;
        _critical.Stepper.Maximum = _low.Stepper.Value - Settings.WarningStep;
        _critical.Stepper.Value = Settings.CriticalWarning;
        _warningSounds.Checked = Settings.WarningSounds;
        _shareStatus.Checked = Settings.ShareStatus;
        _startWithWindows.Checked = Settings.StartWithWindows;
        _checkUpdates.Checked = Settings.CheckForUpdates == true;
        ShowUpdateStatus();
        _loading = false;
    }

    /// <summary>Saves a change from the window, unless it's showing sample data or filling in values.</summary>
    private void Save(Action save)
    {
        if (!_loading && !_sampleData)
            save();
    }

    private void WireEvents()
    {
        _shownKinds.CheckedChanged += (_, _) => Save(() =>
        {
            for (int i = 0; i < ShownKinds.Length; i++)
                Settings.SetKindShown(ShownKinds[i], _shownKinds[i]);
        });
        _iconLimit.SelectedChanged += (_, _) => Save(() => Settings.TrayIconLimit = IconLimits[_iconLimit.SelectedIndex]);
        _corner.SelectedChanged += (_, _) => Save(() => Settings.ToastCorner = Corners[_corner.SelectedIndex]);
        _connectionToasts.CheckedChanged += (_, _) => Save(() => Settings.ConnectionToasts = _connectionToasts.Checked);
        _holdInFullscreen.CheckedChanged += (_, _) => Save(() => Settings.HoldInFullscreen = _holdInFullscreen.Checked);
        _fullyCharged.CheckedChanged += (_, _) => Save(() => Settings.FullyChargedToasts = _fullyCharged.Checked);
        _low.Stepper.ValueChanged += (_, _) =>
        {
            if (_loading)
                return;
            Save(() => Settings.LowWarning = _low.Stepper.Value);
            // Keep critical below low; lowering low can push critical down with it.
            _critical.Stepper.Maximum = _low.Stepper.Value - Settings.WarningStep;
            if (_critical.Stepper.Value > _critical.Stepper.Maximum)
                _critical.Stepper.Value = _critical.Stepper.Maximum;
            _critical.Stepper.InvalidateVisual();
        };
        _critical.Stepper.ValueChanged += (_, _) => Save(() => Settings.CriticalWarning = _critical.Stepper.Value);
        _low.Switch.CheckedChanged += (_, _) => Save(() => Settings.LowWarningEnabled = _low.Switch.Checked);
        _critical.Switch.CheckedChanged += (_, _) => Save(() => Settings.CriticalWarningEnabled = _critical.Switch.Checked);
        _warningSounds.CheckedChanged += (_, _) =>
        {
            if (_loading)
                return;
            Save(() => Settings.WarningSounds = _warningSounds.Checked);
            // A sample on switching it on, as choosing a pop-up corner shows a sample pop-up.
            if (_warningSounds.Checked && !_sampleData)
                WarningSound.Play(critical: false);
        };
        _shareStatus.CheckedChanged += (_, _) => Save(() => Settings.ShareStatus = _shareStatus.Checked);
        _openDataFolder.Click += (_, _) => OpenDataFolder();
        _startWithWindows.CheckedChanged += (_, _) => Save(() => Settings.StartWithWindows = _startWithWindows.Checked);
        _checkUpdates.CheckedChanged += (_, _) =>
        {
            if (_loading)
                return;
            Save(() => Settings.CheckForUpdates = _checkUpdates.Checked);
            // Turning it on checks straight away, so it's also the way to check now.
            if (_checkUpdates.Checked && !_sampleData)
                Updates.CheckNow();
        };
    }

    /// <summary>Shows the data folder in File Explorer, making it first if nothing has been written yet.</summary>
    private void OpenDataFolder()
    {
        if (_sampleData)
            return;
        try
        {
            Directory.CreateDirectory(Log.Folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Making the data folder failed: {ex.Message}");
            return;
        }
        Win32.Open(Log.Folder);
    }

    /// <summary>
    /// What the update check found, under its switch: always the current version first, then whether WattsLeft is
    /// up to date or has a new version, or why it can't tell.
    /// </summary>
    private void ShowUpdateStatus()
    {
        string current = $"Current version: v{UpdateCheck.Current.ToString(3)}";
        void Show(string status, (string, Action)? primary = null, (string, Action)? secondary = null) =>
            _updateStatus.Show($"{current}\n{status}", primary, secondary);

        if (Settings.CheckForUpdates != true)
        {
            Show("Update checks are off, so WattsLeft doesn't contact GitHub.");
            return;
        }
        if (Updates.Available is { } update)
        {
            string newer = $"v{update.Version.ToString(3)}";
            if (Updates.Installing is double done)
                Show($"WattsLeft has a new version: {newer}. Downloading… {done:P0}, then WattsLeft restarts.");
            else
                Show(Updates.InstallError is { } error ? $"WattsLeft has a new version: {newer}. {error}" : $"WattsLeft has a new version: {newer}.",
                    ("Update now", () => Updates.Offer(this)), ("What's new", () => Updates.OpenPage(update)));
            return;
        }
        if (Updates.Checking)
        {
            Show("Checking for a new version…");
            return;
        }
        if (Settings.LastUpdateCheck is not { } last)
            Show("Not checked yet.");
        else if (last.Answered)
            Show($"WattsLeft is up to date. Checked {When(last.When)}.");
        else
            Show($"Couldn't reach GitHub {When(last.When)}. WattsLeft will try again later.");
    }

    /// <summary>"today at 9:41 AM", "yesterday at …", or "on 3 Nov at …".</summary>
    private static string When(DateTimeOffset time)
    {
        var local = time.LocalDateTime;
        string day = local.Date == DateTime.Today ? "today"
            : local.Date == DateTime.Today.AddDays(-1) ? "yesterday"
            : $"on {local:d MMM}";
        return $"{day} at {local:t}";
    }

    /// <summary>A device connected, disconnected or changed level: refresh the Devices page if it's showing.</summary>
    public void ReadingsChanged()
    {
        if (_current == _controllersPage)
            ReloadHistory();
    }

    /// <summary>Gives a device a name of the user's own, or with an empty name, its own back.</summary>
    private async void Rename(HistorySeries series)
    {
        string? name = await ConfirmDialog.AskText(this, $"Rename {series.Name}",
            $"The new name shows everywhere WattsLeft shows this device. Leave it empty to go back to {series.DeviceName}.",
            series.Name, Settings.MaxNameLength, "Rename", _palette);
        if (name is null || _sampleData)
            return;
        // As with Remove, the card is about to be rebuilt; keep focus on screen.
        _controllerPicker.Focus();
        Settings.Rename(series.Id, name.Trim() == series.DeviceName ? null : name);
    }

    /// <summary>Hides a device, with a few seconds to undo; Restore removed lists it for restoring later.</summary>
    private async void Remove(HistorySeries series)
    {
        if (!await ConfirmDialog.Ask(this, $"Remove {series.Name}?",
                "It'll be hidden from the tray and the Devices page. Its history is kept, and it comes back by itself the next " +
                "time it connects. You can also restore it with Restore removed, on this page.",
                "Remove", _palette) || _sampleData)
            return;
        if (_pickerChoice == series.Id)
            _pickerChoice = AllControllers;
        // The Remove button's card is about to be rebuilt, taking focus with it; keep it on the dropdown, on screen.
        _controllerPicker.Focus();
        Settings.Hide(series.Id);
        _undoId = series.Id;
        _undoBar.Show($"Removed {series.Name}", () => Settings.Unhide(series.Id));
    }

    /// <summary>
    /// The removed devices that still have history, oldest removal first, for the Restore removed button. Entries
    /// whose history has expired are forgotten.
    /// </summary>
    private List<(string Id, string Name, DateTime When)> RemovedControllers(Dictionary<string, HistorySeries> full)
    {
        var hidden = Settings.HiddenControllers;
        // Sample data doesn't hold the real devices, so they'd all look expired: never forget then.
        var expired = hidden.Keys.Where(id => !full.ContainsKey(id)).ToList();
        if (expired.Count > 0 && !_sampleData)
            Settings.ForgetHidden(expired);
        return [.. hidden.Where(kv => full.ContainsKey(kv.Key)).OrderBy(kv => kv.Value).Select(kv => (kv.Key, full[kv.Key].Name, kv.Value))];
    }

    /// <summary>Lists removed devices under the button; picking one restores it, or all of them at once.</summary>
    private void OpenRestoreList()
    {
        var removed = _removed.ToList();
        var labels = removed.Select(r => $"{r.Name}  ·  removed {r.When:d MMM}").ToList();
        if (removed.Count > 1)
            labels.Add("Restore all");
        ListPopup.Open(_restoreButton, labels, checkedIndex: -1, _palette, index =>
        {
            // Restoring the last one hides this button, taking focus with it: keep focus on the dropdown instead.
            if (removed.Count == 1 || index >= removed.Count)
                _controllerPicker.Focus();
            if (!_sampleData)
                Settings.Unhide(index < removed.Count ? [removed[index].Id] : removed.Select(r => r.Id).ToList());
        });
    }

    private void OnSettingsChanged() => Dispatcher.UIThread.Post(() =>
    {
        LoadValues();
        if (_current == _controllersPage)
            ReloadHistory();
    });

    private void OnAppearanceChanged()
    {
        if (_fixedPalette)
            return;
        var palette = Appearance.For(Appearance.AppsUseLight());
        if (palette == _palette)
            return;
        _palette = palette;
        ApplyPalette();
    }

    private void ApplyPalette()
    {
        Background = Draw.Brush(_palette.Background);
        // Avalonia's own parts (the scroll bar, the rename box) follow the same mode.
        RequestedThemeVariant = _palette.Light ? ThemeVariant.Light : ThemeVariant.Dark;
        if (TryGetPlatformHandle()?.Handle is IntPtr hwnd)
            Appearance.SetTitleBar(hwnd, _palette.Light);
        foreach (var control in _nav.Cast<IThemed>().Concat(_pages.SelectMany(p => p.Items).Concat(_statsCards).OfType<IThemed>()))
            control.Palette = _palette;
        InvalidateVisual();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (TryGetPlatformHandle()?.Handle is IntPtr hwnd)
            Appearance.SetTitleBar(hwnd, _palette.Light);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && !e.Handled)
            Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _historyTimer.Stop();
        Settings.Changed -= OnSettingsChanged;
        Appearance.Changed -= OnAppearanceChanged;
        Updates.Changed -= ShowUpdateStatus;
        base.OnClosed(e);
    }

    /// <summary>
    /// A page's items stacked top to bottom at the page's width (up to 760): headings with room above, choice rows
    /// at their own width, Restore removed beside the device list or under it when it won't fit.
    /// </summary>
    private sealed class PagePanel : Panel
    {
        private const double CardGap = 4, SectionGap = 24, MaxWidth_ = 760;
        private readonly SettingsWindow _window;
        private readonly List<Control> _items;

        public PagePanel(SettingsWindow window, List<Control> items)
        {
            _window = window;
            _items = items;
            foreach (var item in items)
                Children.Add(item);
        }

        public IReadOnlyList<Control> Items => _items;

        /// <summary>Puts the device cards after <paramref name="after"/>, in place of the ones there before.</summary>
        public void SetCards(IReadOnlyList<StatsCard> cards, Control after)
        {
            foreach (var old in _items.OfType<StatsCard>().ToList())
            {
                _items.Remove(old);
                if (!cards.Contains(old))
                    Children.Remove(old);
            }
            int at = _items.IndexOf(after) + 1;
            foreach (var card in cards)
            {
                _items.Insert(at++, card);
                if (!Children.Contains(card))
                    Children.Add(card);
            }
            InvalidateMeasure();
        }

        protected override Size MeasureOverride(Size available) =>
            new(double.IsInfinity(available.Width) ? 600 : available.Width, Place(available.Width, arrange: false));

        protected override Size ArrangeOverride(Size size)
        {
            Place(size.Width, arrange: true);
            return size;
        }

        /// <summary>Stacks the items; returns the height they take, with room below the last.</summary>
        private double Place(double pageWidth, bool arrange)
        {
            double width = Math.Min((double.IsInfinity(pageWidth) ? 600 : pageWidth) - PagePadding * 2, MaxWidth_);
            double x = PagePadding, y = PagePadding;
            Control? previous = null;
            var previousBounds = default(Rect);
            foreach (var item in _items)
            {
                if (item is UndoBar { Showing: false })
                {
                    item.IsVisible = false;
                    continue; // takes no room until it has something to say
                }
                if (item is UndoBar)
                    item.IsVisible = true;
                if (item == _window._restoreButton)
                {
                    // Beside the dropdown, or under it if the page is too narrow; hidden while nothing is removed.
                    // Placed from where this pass put the dropdown, so measuring and arranging agree.
                    item.Measure(Size.Infinity);
                    if (item.IsVisible && previous is not null)
                    {
                        bool beside = previous.IsVisible && previousBounds.Right + 8 + item.Width <= x + width;
                        var at = beside
                            ? new Point(previousBounds.Right + 8, previousBounds.Top + (previousBounds.Height - item.Height) / 2)
                            : !previous.IsVisible ? previousBounds.Position : new Point(x, y);
                        if (previous.IsVisible && !beside)
                            y += item.Height + CardGap;
                        if (arrange)
                            item.Arrange(new Rect(at, new Size(item.Width, item.Height)));
                    }
                    continue;
                }
                if (item is Heading && previous is not null)
                    y += SectionGap - CardGap;
                else if (item is DropDown && (previous is HistoryChart || previous == _window._chartNote))
                    y += 18; // the chart, its note and the box below them read as one block without room between
                // Choice rows keep their own width; everything else spans the page, as tall as its text needs there.
                var page = item as IPageItem;
                bool ownWidth = page?.OwnWidth ?? false;
                double itemWidth = ownWidth ? item.Width : width;
                double height = page is not null && !ownWidth ? page.HeightFor(width) : item.Height;
                item.Measure(new Size(itemWidth, height));
                var bounds = new Rect(x, y, itemWidth, height);
                if (arrange)
                    item.Arrange(bounds);
                y += height + CardGap;
                previous = item;
                previousBounds = bounds;
            }
            // Room below the last item, so the page can scroll it clear of the window edge.
            return y + PagePadding;
        }
    }
}
