using System.Runtime.InteropServices;

namespace WattsLeft.Battery;

/// <summary>
/// Reads Sony controllers (DualSense, DualSense Edge, DualShock 4) straight from their HID input reports,
/// since Windows doesn't surface their battery anywhere. Each connected controller gets a background loop
/// that samples one report every few seconds and raises <see cref="ControllersChanged"/> when it changes.
/// </summary>
/// <remarks>
/// Over Bluetooth these controllers send a short report without battery data until the calibration
/// feature report is read, which switches them to the full report until they reconnect. Games that
/// read the short report through DirectInput lose input while that lasts; Steam Input and games with
/// native support read the full report. Report layouts follow Linux's hid-playstation driver.
/// </remarks>
public sealed class PlayStationSource : IBatterySource, IDisposable
{
    public const ushort SonyVendorId = 0x054C;

    private enum Family { DualSense, DualShock4 }

    private static readonly Dictionary<ushort, (string Name, Family Family)> Models = new()
    {
        [0x0CE6] = ("DualSense Wireless Controller", Family.DualSense),
        [0x0DF2] = ("DualSense Edge Wireless Controller", Family.DualSense),
        [0x05C4] = ("DUALSHOCK 4 Wireless Controller", Family.DualShock4),
        [0x09CC] = ("DUALSHOCK 4 Wireless Controller", Family.DualShock4),
    };

    public static IEnumerable<ushort> ProductIds => Models.Keys;

    /// <summary>How often a controller is sampled: a report is read every this often.</summary>
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(5);
    /// <summary>Full reports stream continuously, so a read this slow means no battery data is coming.</summary>
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);

    private const int UsbReportLength = 64;

    /// <summary>Calibration feature report; reading it over Bluetooth turns on the full input report.</summary>
    private const byte CalibrationReportId = 0x05;

    private sealed record Status(int? Percent, ChargeState Power);

    private sealed class Controller(HidCollection collection)
    {
        public readonly HidCollection Collection = collection;
        public string Id => Collection.Path;
        public ushort ProductId => Collection.Product;
        /// <summary>When it's sampled; made once it's known.</summary>
        public PollSchedule? Schedule;
        /// <summary>null until the first report arrives.</summary>
        public volatile Status? Latest;
        /// <summary>Whether the calibration read that turns on the full Bluetooth report has succeeded.</summary>
        public bool FullReportRequested;
        /// <summary>Unknown until the device is first opened.</summary>
        public volatile Connection Connection;
    }

    public string Name => "PlayStation HID";

    public event EventHandler? ControllersChanged;

    private readonly Dictionary<string, Controller> _controllers = [];
    private readonly IDisposable _watch;

    public PlayStationSource()
    {
        // Game controllers are usage page 0x01 (Generic Desktop), usage 0x05 (Game Pad); keep Sony's.
        _watch = HidMonitor.Watch(c => c.Vendor == SonyVendorId && c.UsagePage == 0x01 && c.Usage == 0x05 && Models.ContainsKey(c.Product),
            OnAdded, OnRemoved, out _);
    }

    public IReadOnlyList<ControllerBattery> Read()
    {
        lock (_controllers)
            return _controllers.Values.Select(ToReading).ToList();
    }

    private ControllerBattery ToReading(Controller controller)
    {
        var status = controller.Latest ?? new Status(null, ChargeState.Unknown);
        var level = status.Percent is int p ? ControllerBattery.LevelFromPercent(p) : BatteryLevel.Unknown;
        return new(controller.Id, Models[controller.ProductId].Name, Name, status.Percent, level, status.Power,
            $"{SonyVendorId:X4}:{controller.ProductId:X4}", controller.Connection);
    }

    private void OnAdded(HidInfo device)
    {
        var controller = new Controller(new HidCollection(device));
        lock (_controllers)
        {
            if (!_controllers.TryAdd(device.Path, controller))
                return;
            // Made under the lock, so a controller removed straight away has its schedule stopped too. Its
            // reports stream on their own, so device notices add nothing.
            controller.Schedule = new PollSchedule(Models[controller.ProductId].Name, (_, token) => SampleAsync(controller, token),
                interval: SampleInterval, onDeviceNotices: false);
        }
        ControllersChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnRemoved(string id)
    {
        Controller? controller;
        lock (_controllers)
        {
            if (!_controllers.Remove(id, out controller))
                return;
        }
        controller.Schedule?.Dispose();
        ControllersChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Opens the controller just long enough to read one report, so tools that want it exclusively
    /// (DS4Windows, HidHide) aren't blocked by a handle held open, and passes on any change.
    /// </summary>
    private async Task SampleAsync(Controller controller, CancellationToken token)
    {
        using var link = HidLink.Open(controller.Collection)
            ?? throw new IOException($"open failed: error {Marshal.GetLastWin32Error()}");
        if (controller.Collection.InputLength == 0)
            throw new IOException("couldn't read HID capabilities");

        // Over USB this is just a harmless read (or unsupported, on a DualShock 4).
        if (!controller.FullReportRequested)
            controller.FullReportRequested = link.GetFeature(CalibrationReportId) is not null;

        // Reads come back padded to the longest input report, which is 64 bytes only over USB.
        bool bluetooth = controller.Collection.InputLength > UsbReportLength;
        controller.Connection = bluetooth ? Connection.Bluetooth : Connection.Usb;
        // Short reports can still arrive just after the switch; full ones follow within milliseconds.
        var family = Models[controller.ProductId].Family;
        var report = await link.ReadAsync(r => Parse(family, bluetooth, r) is not null, ReadTimeout, token);
        if (report is null || Parse(family, bluetooth, report) is not { } status || status == controller.Latest)
            return;
        controller.Latest = status;
        ControllersChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <returns>null if the report carries no battery data (Bluetooth before the full report is on).</returns>
    private static Status? Parse(Family family, bool bluetooth, ReadOnlySpan<byte> report)
    {
        // Where the status byte sits: USB uses report 0x01, Bluetooth's full report is 0x31 or 0x11.
        // Bluetooth's short report is also 0x01 but carries no battery data.
        int offset = (family, bluetooth, report[0]) switch
        {
            (Family.DualSense, false, 0x01) => 53,
            (Family.DualSense, true, 0x31) => 54,
            (Family.DualShock4, false, 0x01) => 30,
            (Family.DualShock4, true, 0x11) => 32,
            _ => -1,
        };
        if (offset < 0 || offset >= report.Length)
            return null;

        byte status = report[offset];
        int level = status & 0x0F;
        int percent = Math.Min(level * 10 + 5, 100);

        if (family == Family.DualSense)
        {
            return (status >> 4) switch
            {
                0x0 => new(percent, ChargeState.Discharging),
                0x1 => new(percent, ChargeState.Charging),
                // Full but still on the cable; no bolt, like the Xbox inference at 100%.
                0x2 => new(100, ChargeState.Discharging),
                _ => new(null, ChargeState.Unknown), // voltage, temperature or charging error
            };
        }

        bool cable = (status & 0x10) != 0;
        return (cable, level) switch
        {
            (false, _) => new(percent, ChargeState.Discharging),
            (true, < 10) => new(percent, ChargeState.Charging),
            (true, 10 or 11) => new(100, ChargeState.Discharging),
            _ => new(null, ChargeState.Unknown),
        };
    }

    public void Dispose()
    {
        _watch.Dispose();
        lock (_controllers)
        {
            foreach (var controller in _controllers.Values)
                controller.Schedule?.Dispose();
            _controllers.Clear();
        }
    }
}
