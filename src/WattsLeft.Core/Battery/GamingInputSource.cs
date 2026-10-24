namespace WattsLeft.Battery;

/// <summary>
/// Reads Xbox-compatible gamepads through Windows.Gaming.Input (see <see cref="GamingInput"/>), which sees
/// them over USB, the Xbox Wireless Adapter and Bluetooth. For Bluetooth pads the percentage comes from
/// <see cref="BluetoothLeBattery"/> instead, since Windows.Gaming.Input's report is wrong there.
/// </summary>
public sealed class GamingInputSource : IBatterySource, IDisposable
{
    public string Name => "Windows.Gaming.Input";

    public event EventHandler? ControllersChanged;

    private readonly IDisposable? _padWatcher;
    private readonly ChargeTrends _trends = new();
    /// <summary>Held while reading: the tray and the diagnostics report can read at once, and both update the trends.</summary>
    private readonly object _readGate = new();

    public GamingInputSource()
    {
        try
        {
            // Watching starts enumeration; already-connected pads arrive as additions.
            _padWatcher = GamingInput.WatchPads(() => ControllersChanged?.Invoke(this, EventArgs.Empty));
        }
        catch (Exception ex)
        {
            Log.Once($"Windows.Gaming.Input watch failed: {ex.GetType().Name}: {ex.Message}");
        }

    }

    public IReadOnlyList<ControllerBattery> Read()
    {
        lock (_readGate)
            return ReadLocked();
    }

    private List<ControllerBattery> ReadLocked()
    {
        var pads = Pads();
        var bluetooth = QueryBluetoothBatteries().Where(IsXboxController).ToList();
        if (pads.Count == 0 && bluetooth.Count == 0)
            return [];

        _trends.KeepOnly(bluetooth.Select(b => b.Address));

        var readings = new List<ControllerBattery>();
        foreach (var pad in pads)
        {
            if (ReadPad(pad, bluetooth) is { } reading)
                readings.Add(reading);
        }

        // Windows.Gaming.Input's list can go stale in a long-running process: after a restart, a
        // DualSense disconnecting has taken a Bluetooth Xbox pad out of it until that pad reconnected.
        // Windows still knows the pad is connected, so show it from its Bluetooth reading alone.
        foreach (var ble in bluetooth)
        {
            readings.Add(new ControllerBattery(BluetoothId(ble), ble.Name, "Bluetooth LE", ble.Percent,
                ControllerBattery.LevelFromPercent(ble.Percent),
                InferCharging(ble) ? ChargeState.Charging : ChargeState.Discharging,
                $"{ble.VendorId:X4}:{ble.ProductId:X4}", Connection.Bluetooth));
        }
        return readings;
    }

    /// <summary>Keyed by Bluetooth address, so a pad keeps its identity whether or not Windows.Gaming.Input lists it.</summary>
    private static string BluetoothId(BleBattery ble) => $"ble:{ble.Address}";

    /// <summary>The Bluetooth batteries include mice, headsets and the like; keep Xbox controllers.</summary>
    private static bool IsXboxController(BleBattery ble) =>
        ble.VendorId == MicrosoftVendorId && ble.Name.Contains("Xbox", StringComparison.OrdinalIgnoreCase);

    /// <summary>The pads listed now; none if Windows.Gaming.Input can't be reached.</summary>
    private static List<PadInfo> Pads()
    {
        try
        {
            return GamingInput.Pads();
        }
        catch (Exception ex)
        {
            Log.Once($"Windows.Gaming.Input failed: {ex.GetType().Name}: {ex.Message}");
            return [];
        }
    }

    private ControllerBattery? ReadPad(PadInfo pad, List<BleBattery> bluetooth)
    {
        if (pad.VendorId == 0 && pad.NonRoamableId is null)
        {
            // No raw controller behind it: seen while a DualSense disconnects; a real Xbox pad always has one.
            Log.Once("Windows.Gaming.Input pad without a RawGameController skipped");
            return null;
        }
        // PlayStationSource reads these from their HID reports, which carry the real battery.
        if (pad.VendorId == PlayStationSource.SonyVendorId)
            return null;

        string id = pad.NonRoamableId is { Length: > 0 } nid ? nid : $"wgi:{pad.Identity:X}";
        string hardware = $"{pad.VendorId:X4}:{pad.ProductId:X4}";
        var reading = ToReading(id, DisplayName(pad), pad.Battery) with
        {
            Hardware = hardware,
            // Bluetooth pads are matched below; any other wireless pad is on the Xbox Wireless Adapter.
            Connection = pad.IsWireless ? Connection.WirelessAdapter : Connection.Usb,
        };

        // Windows.Gaming.Input doesn't say which device a pad is, so pair by VID/PID. Two identical
        // controllers, one on Bluetooth and one not, could be swapped; accepted for now.
        var ble = bluetooth.FirstOrDefault(b => b.VendorId == pad.VendorId && b.ProductId == pad.ProductId);
        if (ble is null)
            return reading;

        bluetooth.Remove(ble);
        return reading with
        {
            Id = BluetoothId(ble),
            Name = ble.Name,
            Connection = Connection.Bluetooth,
            Percent = ble.Percent,
            Level = ControllerBattery.LevelFromPercent(ble.Percent),
            Power = reading.Power == ChargeState.Charging || InferCharging(ble)
                ? ChargeState.Charging
                : ChargeState.Discharging,
        };
    }

    /// <summary>
    /// Over Bluetooth, Windows exposes no charging flag (the controller's own battery report is filtered
    /// out by the xinputhid driver), so charging is inferred from the percentage rising.
    /// </summary>
    private bool InferCharging(BleBattery ble) => _trends.Charging(ble.Address, ble.Percent);

    private static List<BleBattery> QueryBluetoothBatteries()
    {
        try
        {
            return BluetoothLeBattery.QueryConnected();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Bluetooth LE battery query failed: {ex}");
            return [];
        }
    }

    /// <summary>Raw battery report fields for every pad, for the --list diagnostic.</summary>
    public static IEnumerable<string> DescribeRawReports()
    {
        foreach (var pad in Pads())
        {
            var r = pad.Battery;
            yield return r is null
                ? "raw: (no report)"
                : $"raw: status={r.Status} remaining={r.RemainingMilliwattHours?.ToString() ?? "null"}mWh " +
                  $"full={r.FullChargeMilliwattHours?.ToString() ?? "null"}mWh " +
                  $"design={r.DesignMilliwattHours?.ToString() ?? "null"}mWh " +
                  $"rate={r.ChargeRateMilliwatts?.ToString() ?? "null"}mW";
        }
    }

    private const ushort MicrosoftVendorId = 0x045E;

    /// <summary>Bluetooth pads often report the generic HID name, so fall back to the vendor.</summary>
    private static string DisplayName(PadInfo pad)
    {
        string? name = pad.DisplayName;
        bool generic = string.IsNullOrWhiteSpace(name) || name.Contains("HID-compliant", StringComparison.OrdinalIgnoreCase);
        if (!generic)
            return name!;
        return pad.VendorId == MicrosoftVendorId ? "Xbox Controller" : name ?? "Game Controller";
    }

    private ControllerBattery ToReading(string id, string name, PadBatteryReport? report)
    {
        if (report is null)
            return new(id, name, Name, null, BatteryLevel.Unknown, ChargeState.Unknown);

        if (report.Status == PadBatteryStatus.NotPresent)
            return new(id, name, Name, null, BatteryLevel.Unknown, ChargeState.Wired);

        int? percent = null;
        if (report.RemainingMilliwattHours is int remaining && report.FullChargeMilliwattHours is int full && full > 0)
            percent = Math.Clamp((int)Math.Round(remaining * 100.0 / full), 0, 100);

        var level = percent is int p ? ControllerBattery.LevelFromPercent(p) : BatteryLevel.Unknown;
        var power = report.Status == PadBatteryStatus.Charging ? ChargeState.Charging : ChargeState.Discharging;
        return new(id, name, Name, percent, level, power);
    }

    public void Dispose()
    {
        _padWatcher?.Dispose();
    }
}
