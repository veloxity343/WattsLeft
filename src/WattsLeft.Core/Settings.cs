using WattsLeft.Battery;
using Microsoft.Win32;

namespace WattsLeft;

/// <summary>A controller's battery warning levels, in percent; null when that warning is off.</summary>
internal sealed record WarningLevels(int? Low, int? Critical);

/// <summary>Which corner of the screen pop-ups appear in.</summary>
internal enum ToastCorner { TopRight, TopLeft, BottomRight, BottomLeft }

/// <summary>
/// User preferences, kept under HKCU\Software\WattsLeft. The tray menu and the settings window both
/// write here, and <see cref="Changed"/> tells the tray app to apply them.
/// </summary>
internal static class Settings
{
    internal const string KeyPath = @"Software\WattsLeft";
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "WattsLeft";

    /// <summary>Raised on the writing thread after any setting changes.</summary>
    public static event Action? Changed;

    public static ToastCorner ToastCorner
    {
        get => Enum.TryParse(Read(nameof(ToastCorner)) as string, out ToastCorner corner) && Enum.IsDefined(corner)
            ? corner
            : ToastCorner.TopRight;
        set => Write(nameof(ToastCorner), value.ToString());
    }

    /// <summary>How many controllers get their own tray icon: 1, 2, or 0 for one per controller.</summary>
    public static int TrayIconLimit
    {
        // 3 and 4 were options before; anything above 2 now means one per controller.
        get => Read(nameof(TrayIconLimit)) is int value && value >= 0 ? (value > 2 ? 0 : value) : 1;
        set => Write(nameof(TrayIconLimit), value);
    }

    /// <summary>Whether connecting and disconnecting show a pop-up; battery warnings always do.</summary>
    public static bool ConnectionToasts
    {
        get => Read(nameof(ConnectionToasts)) is not int value || value != 0;
        set => Write(nameof(ConnectionToasts), value ? 1 : 0);
    }

    /// <summary>Whether a controller that charges to 100% gets a pop-up.</summary>
    public static bool FullyChargedToasts
    {
        get => Read(nameof(FullyChargedToasts)) is not int value || value != 0;
        set => Write(nameof(FullyChargedToasts), value ? 1 : 0);
    }

    /// <summary>
    /// Device types WattsLeft ignores: no icon, pop-ups or history. Empty by default, so everything with a
    /// battery shows.
    /// </summary>
    public static IReadOnlySet<DeviceKind> HiddenKinds =>
        Read(nameof(HiddenKinds)) is string[] names
            ? names.Select(n => Enum.TryParse<DeviceKind>(n, out var kind) ? kind : (DeviceKind?)null).OfType<DeviceKind>().ToHashSet()
            : [];

    public static void SetKindShown(DeviceKind kind, bool shown)
    {
        var hidden = HiddenKinds.ToHashSet();
        if (shown ? hidden.Remove(kind) : hidden.Add(kind))
            Write(nameof(HiddenKinds), hidden.Select(k => k.ToString()).ToArray());
    }

    /// <summary>Keep status.json up to date, for other apps to read the battery levels.</summary>
    public static bool ShareStatus
    {
        get => Read(nameof(ShareStatus)) is int value && value != 0;
        set => Write(nameof(ShareStatus), value ? 1 : 0);
    }

    /// <summary>
    /// Whether to check GitHub once a day for a newer version: null until the user has been asked, which happens
    /// once, on the first start.
    /// </summary>
    public static bool? CheckForUpdates
    {
        get => Read(nameof(CheckForUpdates)) is int value ? value != 0 : null;
        set
        {
            if (value is bool on)
                Write(nameof(CheckForUpdates), on ? 1 : 0);
        }
    }

    /// <summary>When updates were last checked for, and whether GitHub answered.</summary>
    public static (DateTimeOffset When, bool Answered)? LastUpdateCheck =>
        Read(nameof(LastUpdateCheck)) is string text && text.Split('\t') is [var when, var answered] &&
        DateTimeOffset.TryParse(when, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var time)
            ? (time, answered == "1")
            : null;

    /// <summary>The newer version the last answer found, if any (kept through a failed check).</summary>
    public static Version? UpdateAvailable => UpdateCheck.ParseVersion(Read(nameof(UpdateAvailable)) as string) is { } version &&
        version > UpdateCheck.Current ? version : null;

    /// <summary>The newer version the user has had a pop-up about, so each gets one.</summary>
    public static Version? UpdateAnnounced
    {
        get => UpdateCheck.ParseVersion(Read(nameof(UpdateAnnounced)) as string);
        set => Write(nameof(UpdateAnnounced), value?.ToString(3) ?? "");
    }

    /// <summary>Records a check: when, whether GitHub answered, and if it did, the newer version (or none).</summary>
    public static void RecordUpdateCheck(DateTimeOffset when, bool answered, Version? newer)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
        {
            key.SetValue(nameof(LastUpdateCheck), $"{when.ToString("O", System.Globalization.CultureInfo.InvariantCulture)}\t{(answered ? 1 : 0)}");
            if (answered)
                key.SetValue(nameof(UpdateAvailable), newer?.ToString(3) ?? "");
        }
        Changed?.Invoke();
    }

    /// <summary>Play Windows' battery alarm sound with each battery warning.</summary>
    public static bool WarningSounds
    {
        get => Read(nameof(WarningSounds)) is int value && value != 0;
        set => Write(nameof(WarningSounds), value ? 1 : 0);
    }

    /// <summary>Hold pop-ups while a fullscreen app runs; they're always held in exclusive fullscreen.</summary>
    public static bool HoldInFullscreen
    {
        get => Read(nameof(HoldInFullscreen)) is int value && value != 0;
        set => Write(nameof(HoldInFullscreen), value ? 1 : 0);
    }

    public const int MinWarning = 5, MaxWarning = 50, WarningStep = 5;

    /// <summary>The "Battery low" warning level, in percent.</summary>
    public static int LowWarning
    {
        get => ReadWarning(nameof(LowWarning), 20, MinWarning + WarningStep, MaxWarning);
        set => Write(nameof(LowWarning), value);
    }

    /// <summary>The "Battery critical" warning level, in percent; always below <see cref="LowWarning"/>.</summary>
    public static int CriticalWarning
    {
        get => ReadWarning(nameof(CriticalWarning), 10, MinWarning, LowWarning - WarningStep);
        set => Write(nameof(CriticalWarning), value);
    }

    /// <summary>Whether the "Battery low" pop-up shows at all.</summary>
    public static bool LowWarningEnabled
    {
        get => Read(nameof(LowWarningEnabled)) is not int value || value != 0;
        set => Write(nameof(LowWarningEnabled), value ? 1 : 0);
    }

    /// <summary>Whether the "Battery critical" pop-up shows at all.</summary>
    public static bool CriticalWarningEnabled
    {
        get => Read(nameof(CriticalWarningEnabled)) is not int value || value != 0;
        set => Write(nameof(CriticalWarningEnabled), value ? 1 : 0);
    }

    private static int ReadWarning(string name, int fallback, int min, int max) =>
        Read(name) is int value ? Math.Clamp(value, min, max) : Math.Clamp(fallback, min, max);

    /// <summary>
    /// Controllers the user removed from the Controllers page, with when. Removing hides rather than deletes:
    /// the history stays in battery.csv until it expires, and the tray app unhides a controller when it next
    /// connects.
    /// </summary>
    public static IReadOnlyDictionary<string, DateTime> HiddenControllers
    {
        get
        {
            var hidden = new Dictionary<string, DateTime>();
            if (Read(nameof(HiddenControllers)) is string[] entries)
            {
                foreach (var entry in entries)
                {
                    // "id<TAB>time"; ids are device paths or Bluetooth addresses, which never contain a tab.
                    int tab = entry.LastIndexOf('\t');
                    if (tab > 0 && DateTime.TryParse(entry[(tab + 1)..], System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.RoundtripKind, out var when))
                        hidden[entry[..tab]] = when;
                }
            }
            return hidden;
        }
    }

    public static void Hide(string id) => WriteHidden(HiddenControllers.Append(new(id, DateTime.Now)));

    public static void Unhide(string id) => Unhide([id]);

    public static void Unhide(IReadOnlyCollection<string> ids)
    {
        var hidden = HiddenControllers;
        if (hidden.Keys.Any(ids.Contains))
            WriteHidden(hidden.Where(kv => !ids.Contains(kv.Key)));
    }

    /// <summary>Drops entries for controllers whose history has expired, so the list doesn't keep strangers.</summary>
    public static void ForgetHidden(IEnumerable<string> ids)
    {
        var gone = ids.ToHashSet();
        var hidden = HiddenControllers;
        if (hidden.Keys.Any(gone.Contains))
            WriteHidden(hidden.Where(kv => !gone.Contains(kv.Key)));
    }

    private static void WriteHidden(IEnumerable<KeyValuePair<string, DateTime>> hidden) =>
        Write(nameof(HiddenControllers), hidden
            .GroupBy(kv => kv.Key).Select(g => g.Last()) // a re-hidden controller keeps its latest time
            .Select(kv => $"{kv.Key}\t{kv.Value.ToString("O", System.Globalization.CultureInfo.InvariantCulture)}")
            .ToArray());

    /// <summary>The warning levels from Settings, each null when that warning is off.</summary>
    public static WarningLevels DefaultWarnings =>
        new(LowWarningEnabled ? LowWarning : null, CriticalWarningEnabled ? CriticalWarning : null);

    /// <summary>
    /// Controllers with warning levels of their own, by controller id. The rest follow
    /// <see cref="DefaultWarnings"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, WarningLevels> ControllerWarnings
    {
        get
        {
            var levels = new Dictionary<string, WarningLevels>();
            if (Read(nameof(ControllerWarnings)) is string[] entries)
            {
                foreach (var entry in entries)
                {
                    // "id<TAB>low<TAB>critical", each level a number or "off".
                    var parts = entry.Split('\t');
                    if (parts.Length == 3 && parts[0].Length > 0)
                        levels[parts[0]] = new(ParseLevel(parts[1]), ParseLevel(parts[2]));
                }
            }
            return levels;
        }
    }

    /// <summary>Gives a controller warning levels of its own, or with null, has it follow Settings again.</summary>
    public static void SetWarnings(string id, WarningLevels? levels)
    {
        var all = ControllerWarnings.Where(kv => kv.Key != id).ToDictionary();
        if (levels is not null)
            all[id] = levels;
        Write(nameof(ControllerWarnings), all
            .Select(kv => $"{kv.Key}\t{FormatLevel(kv.Value.Low)}\t{FormatLevel(kv.Value.Critical)}")
            .ToArray());
    }

    private static int? ParseLevel(string text) =>
        int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int level)
            ? Math.Clamp(level, MinWarning, MaxWarning)
            : null;

    private static string FormatLevel(int? level) =>
        level?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "off";

    /// <summary>
    /// Controllers left out of the Controllers page's chart, by id. Only the chart: their history is still
    /// recorded, and their stats still show.
    /// </summary>
    public static IReadOnlySet<string> LeftOutOfChart =>
        Read(nameof(LeftOutOfChart)) is string[] ids ? ids.ToHashSet() : [];

    public static void SetInChart(string id, bool inChart)
    {
        var ids = LeftOutOfChart.ToHashSet();
        if (inChart ? ids.Remove(id) : ids.Add(id))
            Write(nameof(LeftOutOfChart), ids.ToArray());
    }

    /// <summary>Names the user gave controllers, by controller id; a renamed controller shows this everywhere.</summary>
    public static IReadOnlyDictionary<string, string> ControllerNames
    {
        get
        {
            var names = new Dictionary<string, string>();
            if (Read(nameof(ControllerNames)) is string[] entries)
            {
                foreach (var entry in entries)
                {
                    // "id<TAB>name"; names are cleaned of tabs and line breaks when saved.
                    int tab = entry.IndexOf('\t');
                    if (tab > 0 && tab < entry.Length - 1)
                        names[entry[..tab]] = entry[(tab + 1)..];
                }
            }
            return names;
        }
    }

    public const int MaxNameLength = 40;

    /// <summary>Gives a controller a name of the user's own; an empty name restores the device's.</summary>
    public static void Rename(string id, string? name)
    {
        // Tabs, line breaks and runs of spaces become single spaces: names are stored as "id<TAB>name".
        name = string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length > MaxNameLength)
            name = name[..MaxNameLength].TrimEnd();
        var names = ControllerNames.Where(kv => kv.Key != id).ToDictionary();
        if (name.Length > 0)
            names[id] = name;
        Write(nameof(ControllerNames), names.Select(kv => $"{kv.Key}\t{kv.Value}").ToArray());
    }

    /// <summary>The reading with the user's name for its controller, if they've given it one.</summary>
    public static ControllerBattery Named(ControllerBattery reading, IReadOnlyDictionary<string, string> names) =>
        names.TryGetValue(reading.Id, out var name) ? reading with { Name = name, DeviceName = reading.DeviceName } : reading;

    /// <summary>Whether Windows starts the app at sign-in, via the current user's Run key.</summary>
    public static bool StartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) is string;
        }
        set
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (value)
                    key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\"");
                else
                    key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Keeps Start with Windows working after the app's folder is moved: if it's on but starts an exe that's no
    /// longer there, it's pointed at this one. An exe that's still there is left alone, so another copy, a test
    /// build say, never takes it over.
    /// </summary>
    public static void FollowMovedApp()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(RunValueName) is not string command)
                return;
            string path = command.Trim().Trim('"');
            if (File.Exists(path) || string.Equals(path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                return;
            key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\"");
            Log.Write($"Start with Windows now starts {Environment.ProcessPath}, as {path} is gone", Log.HistoryFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Write($"Couldn't update Start with Windows: {e.Message}");
        }
    }

    private static object? Read(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(name);
    }

    private static void Write(string name, object value)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
            key.SetValue(name, value);
        Changed?.Invoke();
    }
}
