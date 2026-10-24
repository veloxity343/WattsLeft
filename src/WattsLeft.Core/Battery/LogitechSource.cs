using System.Text;

namespace WattsLeft.Battery;

/// <summary>
/// Logitech wireless mice, keyboards and headsets, through Logitech's HID++ 2.0 protocol: on a Unifying,
/// Bolt or Lightspeed receiver (up to six devices each), or on their own USB cable. Windows doesn't record
/// these batteries, so each device is asked over the receiver's vendor interface, the way Logitech's own
/// software does. Only reads are sent; nothing on the device is changed.
/// </summary>
/// <remarks>
/// The protocol follows Logitech's HID++ 2.0 documentation and Solaar's implementation. A request is a
/// 20-byte long report: 11, device slot, feature index, function &lt;&lt; 4 | software id, parameters. The root
/// feature (index 0) finds the index of any other feature by its id. Replies echo the header; errors come
/// back as feature index 8F (short reports) or FF (long), with the error code after the original header.
/// </remarks>
public sealed class LogitechSource : IBatterySource, IDisposable
{
    public const ushort LogitechVendorId = 0x046D;

    public string Name => "Logitech HID++";

    public event EventHandler? ControllersChanged;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    /// <summary>
    /// A receiver answers as soon as it's plugged in, but its devices take a moment longer to reconnect to it,
    /// so the first poll finds their slots unreachable. For this long after a link appears it's polled every
    /// <see cref="SettlingInterval"/>, so a device shows within seconds of reconnecting, not at the next poll.
    /// A device reconnecting later (a mouse woken by a click, say) is caught by the device notice its arrival
    /// brings, which polls straight away.
    /// </summary>
    private static readonly TimeSpan SettlingTime = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SettlingInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromMilliseconds(600);
    /// <summary>
    /// A dozing radio can take half a second or so to answer the first request. A slot that's never had a
    /// device gets a shorter wait, since an empty slot just doesn't answer, and a receiver has up to six.
    /// </summary>
    private static readonly TimeSpan FirstReplyTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan EmptySlotTimeout = TimeSpan.FromMilliseconds(700);
    /// <summary>
    /// A device asleep doesn't answer at all. It keeps its last reading this long, so a mouse that dozes
    /// between games doesn't come and go; one switched off says so, and goes straight away.
    /// </summary>
    private static readonly TimeSpan AsleepKeep = TimeSpan.FromMinutes(30);

    private const byte LongReport = 0x11, ErrorShort = 0x8F, ErrorLong = 0xFF;
    private const int LongLength = 20;
    private const byte DirectSlot = 0xFF;

    // Feature ids.
    private const ushort Root = 0x0000, DeviceInfo = 0x0003, DeviceName = 0x0005;
    private const ushort UnifiedBattery = 0x1004, BatteryStatus = 0x1000, BatteryVoltage = 0x1001;
    /// <summary>Headsets' battery: millivolts, then flags (bit 0 the headset is connected, bit 1 charging).</summary>
    private const ushort AdcMeasurement = 0x1F20;

    /// <summary>
    /// Headsets that speak HID++ directly (no receiver slots), on their long collection: FF43:0202, or
    /// 000C:0001 on the G535. Names for when a headset doesn't report one.
    /// </summary>
    private static readonly Dictionary<ushort, string> Headsets = new()
    {
        [0x0A66] = "Logitech G533", [0x0AC4] = "Logitech G535", [0x0A5C] = "Logitech G633", [0x0A89] = "Logitech G635",
        [0x0A5B] = "Logitech G933", [0x0A87] = "Logitech G935", [0x0AB5] = "Logitech G733", [0x0AFE] = "Logitech G733",
        [0x0B1F] = "Logitech G733", [0x0AA7] = "Logitech G PRO", [0x0AAA] = "Logitech G PRO X", [0x0ABA] = "Logitech G PRO X",
        [0x0AFB] = "Logitech G PRO X 2", [0x0AFC] = "Logitech G PRO X 2",
    };

    // Error codes.
    private const byte OldProtocol = 0x01, ConnectFailed = 0x04, EmptySlot = 0x08, Unreachable = 0x09;

    /// <summary>Receiver product ids (Solaar's list): devices are paired in slots 1 to 6 behind these.</summary>
    private static readonly HashSet<ushort> Receivers =
    [
        0xC548,                                                  // Bolt
        0xC52B, 0xC532,                                          // Unifying
        0xC52F, 0xC518, 0xC51A, 0xC51B, 0xC521, 0xC525, 0xC526,  // Nano
        0xC52E, 0xC531, 0xC534, 0xC535, 0xC537,
        0xC539, 0xC53A, 0xC53D, 0xC53F, 0xC541, 0xC545, 0xC547,  // Lightspeed
        0xC54D,
    ];

    /// <summary>One receiver, or one device on its cable: the HID interface its devices are reached through.</summary>
    private sealed class Link(string path, ushort productId)
    {
        public readonly string Path = path;
        public readonly ushort ProductId = productId;
        public readonly CancellationTokenSource Stop = new();
        /// <summary>Released to poll now rather than at the next interval.</summary>
        public readonly SemaphoreSlim Wake = new(0);
        public readonly Dictionary<byte, Paired> Devices = [];
        public bool IsReceiver => Receivers.Contains(ProductId);
        public bool IsHeadset => Headsets.ContainsKey(ProductId);
    }

    /// <summary>A device in one of a link's slots: what's known about it, and its latest reading.</summary>
    private sealed class Paired
    {
        public Dictionary<ushort, byte> Features = [];
        public string? Id, Name;
        public DeviceKind Kind = DeviceKind.Other;
        public ControllerBattery? Reading;
        public DateTime LastAnswer;
        /// <summary>Which battery feature answered and what it said, for diagnostics.</summary>
        public string? Detail;
    }

    private readonly Dictionary<string, Link> _links = [];
    /// <summary>
    /// The slots each kind of receiver has had a device in. Polling between full polls asks only these: a
    /// Lightspeed receiver's empty slots don't answer at all, and waiting for each costs most of a second.
    /// </summary>
    private readonly Dictionary<ushort, HashSet<byte>> _slotsInUse = [];
    private readonly IDisposable _watch, _notices;
    /// <summary>Done once the receivers already plugged in have been seen.</summary>
    private readonly Task _listed;
    private int _softwareId;

    public LogitechSource()
    {
        // HID++ long reports live on a vendor collection, usage page FF00, usage 0002; headsets have theirs
        // on FF43:0202, or 000C:0001 on the G535.
        static bool Wanted(HidInfo c) => c.Vendor == LogitechVendorId && (c.UsagePage, c.Usage) switch
        {
            (0xFF00, 0x0002) => true,
            (0xFF43, 0x0202) => Headsets.ContainsKey(c.Product),
            (0x000C, 0x0001) => c.Product == 0x0AC4,
            _ => false,
        };
        _watch = HidMonitor.Watch(Wanted, OnAdded, OnRemoved, out _listed);
        // A device connecting to its receiver brings its own interfaces with it: ask the receivers then.
        _notices = DeviceNotices.Watch(WakeAll);
    }

    /// <summary>
    /// Polls every link now and waits, for diagnostics: readings otherwise arrive a few seconds after
    /// start-up, in the background.
    /// </summary>
    public IReadOnlyList<string> PollNow(TimeSpan timeout)
    {
        _listed.Wait(timeout);
        Link[] links;
        lock (_links)
            links = [.. _links.Values];
        Task.WhenAll(links.Select(link => PollAsync(link, allSlots: true, link.Stop.Token))).Wait(timeout);
        lock (_links)
        {
            return links.SelectMany(link => link.Devices.Select(kv =>
                $"{kv.Value.Name}: receiver {link.ProductId:X4}{(link.IsReceiver ? $", slot {kv.Key}" : ", on its cable")}, " +
                $"features {string.Join(" ", kv.Value.Features.Keys.Select(f => f.ToString("X4")))}, {kv.Value.Detail ?? "no battery reading"}"))
                .ToList();
        }
    }

    public IReadOnlyList<ControllerBattery> Read()
    {
        lock (_links)
        {
            return _links.Values
                .SelectMany(link => link.Devices.Values)
                .Select(device => device.Reading)
                .OfType<ControllerBattery>()
                .ToList();
        }
    }

    private void OnAdded(HidInfo device)
    {
        var link = new Link(device.Path, device.Product);
        lock (_links)
        {
            if (!_links.TryAdd(device.Path, link))
                return;
        }
        _ = Task.Run(() => WatchAsync(link));
    }

    private void OnRemoved(string id)
    {
        Link? link;
        lock (_links)
        {
            if (!_links.Remove(id, out link))
                return;
        }
        link.Stop.Cancel();
        ControllersChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Asks every link to poll now, for a device that may have just connected or gone.</summary>
    private void WakeAll()
    {
        lock (_links)
        {
            foreach (var link in _links.Values.Where(l => l.Wake.CurrentCount == 0))
                link.Wake.Release();
        }
    }

    private async Task WatchAsync(Link link)
    {
        var token = link.Stop.Token;
        var settled = DateTime.UtcNow + SettlingTime;
        var nextFullPoll = DateTime.MinValue;
        while (!token.IsCancellationRequested)
        {
            try
            {
                // Every slot at each regular poll, so a newly paired device is found; while settling, or woken by
                // a device notice, only the slots in use, so the answer comes quickly.
                bool all = DateTime.UtcNow >= settled && DateTime.UtcNow >= nextFullPoll;
                await PollAsync(link, all, token);
                if (all)
                    nextFullPoll = DateTime.UtcNow + PollInterval;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Once($"Logitech receiver {link.ProductId:X4} failed: {ex.GetType().Name}: {ex.Message}");
            }

            try
            {
                var wait = DateTime.UtcNow < settled ? SettlingInterval : nextFullPoll - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                    await link.Wake.WaitAsync(wait, token);
                while (link.Wake.CurrentCount > 0)
                    link.Wake.Wait(0); // one poll answers any number of notices
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Asks the link's slots for their battery: all of them, or only those in use (all, if none are known yet).
    /// Each change is passed on as soon as it's read, not once every slot has been asked.
    /// </summary>
    private async Task PollAsync(Link link, bool allSlots, CancellationToken token)
    {
        // Opened for each poll and shared, so Logitech's own software and other apps keep working.
        using var handle = Hid.Open(link.Path, Hid.GenericRead | Hid.GenericWrite);
        if (handle.IsInvalid)
            throw new IOException($"open failed: error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        using var stream = new FileStream(handle, FileAccess.ReadWrite, 0, isAsync: true);

        byte[] slots = !link.IsReceiver ? [DirectSlot] : [1, 2, 3, 4, 5, 6];
        lock (_links)
        {
            if (link.IsReceiver && !allSlots && _slotsInUse.TryGetValue(link.ProductId, out var inUse) && inUse.Count > 0)
                slots = [.. inUse.Order()];
        }
        foreach (byte slot in slots)
        {
            Paired? device;
            lock (_links)
                link.Devices.TryGetValue(slot, out device);
            var before = device?.Reading;
            var after = await ReadSlotAsync(link, stream, slot, device, token);
            lock (_links)
            {
                if (after is null)
                    link.Devices.Remove(slot);
                else
                {
                    link.Devices[slot] = after;
                    if (!_slotsInUse.TryGetValue(link.ProductId, out var inUse))
                        _slotsInUse[link.ProductId] = inUse = [];
                    inUse.Add(slot);
                }
            }
            if (after?.Reading != before)
                ControllersChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The device in a slot with a fresh reading, or null if the slot is empty or its device is off.</summary>
    private async Task<Paired?> ReadSlotAsync(Link link, FileStream stream, byte slot, Paired? device, CancellationToken token)
    {
        // Pinging (root function 1) both checks the slot and wakes a dozing radio.
        var ping = await RequestAsync(stream, slot, 0, 1, [0, 0, 0xA5], device is null ? EmptySlotTimeout : FirstReplyTimeout, token);
        if (ping.Error is EmptySlot or OldProtocol || (ping.Error is not null && device is null))
            return null; // HID++ 1.0 devices are older than anything with a battery feature worth reading
        if (ping.Error is Unreachable or ConnectFailed)
            return null; // paired, but switched off or out of range
        if (ping.Reply is null)
        {
            // Asleep: no answer at all. Keep the last reading for a while rather than flicker.
            if (device is not null && DateTime.UtcNow - device.LastAnswer < AsleepKeep)
                return device;
            return null;
        }

        device ??= new Paired();
        device.LastAnswer = DateTime.UtcNow;
        if (device.Id is null)
            await DescribeAsync(link, stream, slot, device, token);

        var battery = await ReadBatteryAsync(stream, slot, device, token);
        if (battery is null)
            return device.Reading is null ? null : device;

        var (percent, level, power) = battery.Value;
        device.Reading = new ControllerBattery(device.Id!, device.Name!, Name, percent, level, power,
            $"{LogitechVendorId:X4}:{link.ProductId:X4}", link.IsReceiver ? Connection.Receiver : Connection.Usb, device.Kind);
        return device;
    }

    /// <summary>Its features, name, type and a stable id, asked once.</summary>
    private async Task DescribeAsync(Link link, FileStream stream, byte slot, Paired device, CancellationToken token)
    {
        if (link.IsHeadset)
            device.Kind = DeviceKind.Headset;
        foreach (ushort feature in new[] { DeviceInfo, DeviceName, UnifiedBattery, BatteryStatus, BatteryVoltage, AdcMeasurement })
        {
            var reply = await RequestAsync(stream, slot, 0, 0, [(byte)(feature >> 8), (byte)feature], ReplyTimeout, token);
            if (reply.Reply is { } r && r[4] != 0)
                device.Features[feature] = r[4];
        }

        // The name comes a few characters at a time; function 2 says what sort of device it is.
        if (device.Features.TryGetValue(DeviceName, out byte nameIndex))
        {
            var count = await RequestAsync(stream, slot, nameIndex, 0, [], ReplyTimeout, token);
            if (count.Reply is { } c)
            {
                var name = new StringBuilder();
                while (name.Length < c[4])
                {
                    var part = await RequestAsync(stream, slot, nameIndex, 1, [(byte)name.Length], ReplyTimeout, token);
                    if (part.Reply is not { } p)
                        break;
                    string chunk = Encoding.UTF8.GetString(p, 4, LongLength - 4).TrimEnd('\0');
                    if (chunk.Length == 0)
                        break;
                    name.Append(chunk);
                }
                device.Name = name.ToString(0, Math.Min(name.Length, c[4])).Trim();
            }
            var type = await RequestAsync(stream, slot, nameIndex, 2, [], ReplyTimeout, token);
            if (type.Reply is { } t)
            {
                device.Kind = t[4] switch
                {
                    0 or 2 => DeviceKind.Keyboard, // keyboard, numpad
                    3 or 4 or 5 => DeviceKind.Mouse, // mouse, trackpad, trackball
                    8 => DeviceKind.Headset,
                    11 or 12 => DeviceKind.Gamepad, // joystick, gamepad
                    _ => DeviceKind.Other,
                };
            }
        }

        // The unit id stays with the device, so it keeps its history whichever receiver or port it's on.
        string? unit = null;
        if (device.Features.TryGetValue(DeviceInfo, out byte infoIndex) &&
            (await RequestAsync(stream, slot, infoIndex, 0, [], ReplyTimeout, token)).Reply is { } info)
        {
            unit = Convert.ToHexString(info, 5, 4);
            if (unit == "00000000")
                unit = null;
        }
        device.Id = unit is not null ? $"logi:{unit}" : $"logi:{link.ProductId:X4}:{slot}";
        if (string.IsNullOrWhiteSpace(device.Name) && Headsets.TryGetValue(link.ProductId, out var headsetName))
            device.Name = headsetName;
        if (string.IsNullOrWhiteSpace(device.Name))
            device.Name = device.Kind switch
            {
                DeviceKind.Mouse => "Logitech mouse",
                DeviceKind.Keyboard => "Logitech keyboard",
                DeviceKind.Headset => "Logitech headset",
                _ => "Logitech device",
            };
    }

    /// <summary>From the first battery feature it has: unified battery, battery status, or battery voltage.</summary>
    private async Task<(int? Percent, BatteryLevel Level, ChargeState Power)?> ReadBatteryAsync(
        FileStream stream, byte slot, Paired device, CancellationToken token)
    {
        if (device.Features.TryGetValue(AdcMeasurement, out byte adc))
        {
            // An error reply means the headset is linked but inactive: no reading this time.
            if ((await RequestAsync(stream, slot, adc, 0, [], ReplyTimeout, token)).Reply is not { } a)
                return null;
            if ((a[6] & 0x01) == 0)
            {
                device.Reading = null; // switched off: it goes from the tray
                return null;
            }
            int millivolts = a[4] << 8 | a[5];
            int percent = PercentFromVoltage(millivolts);
            device.Detail = $"ADC measurement: {millivolts} mV, flags {a[6]:X2}, about {percent}%";
            return (percent, ControllerBattery.LevelFromPercent(percent), (a[6] & 0x02) != 0 ? ChargeState.Charging : ChargeState.Discharging);
        }

        if (device.Features.TryGetValue(UnifiedBattery, out byte unified) &&
            (await RequestAsync(stream, slot, unified, 1, [], ReplyTimeout, token)).Reply is { } u)
        {
            // State of charge, level flags (1 critical, 2 low, 4 good, 8 full), charging status, external power.
            int? percent = u[4] is > 0 and <= 100 ? u[4] : null;
            var level = percent is int p ? ControllerBattery.LevelFromPercent(p)
                : (u[5] & 8) != 0 ? BatteryLevel.Full
                : (u[5] & 4) != 0 ? BatteryLevel.Medium
                : (u[5] & 2) != 0 ? BatteryLevel.Low
                : (u[5] & 1) != 0 ? BatteryLevel.Empty
                : BatteryLevel.Unknown;
            var power = u[6] is 1 or 2 ? ChargeState.Charging : ChargeState.Discharging; // 3 is charged, still on its cable
            device.Detail = $"unified battery: {u[4]}%, level flags {u[5]:X2}, status {u[6]}";
            return (percent, level, power);
        }

        if (device.Features.TryGetValue(BatteryStatus, out byte status) &&
            (await RequestAsync(stream, slot, status, 0, [], ReplyTimeout, token)).Reply is { } s)
        {
            // Level, next level, status: 0 discharging, 1 charging, 2 almost full, 3 full, 4 slow charging.
            int percent = Math.Clamp((int)s[4], 0, 100);
            var power = s[6] is 1 or 2 or 4 ? ChargeState.Charging : ChargeState.Discharging;
            device.Detail = $"battery status: {s[4]}%, next {s[5]}%, status {s[6]}";
            return (percent, ControllerBattery.LevelFromPercent(percent), power);
        }

        if (device.Features.TryGetValue(BatteryVoltage, out byte voltage) &&
            (await RequestAsync(stream, slot, voltage, 0, [], ReplyTimeout, token)).Reply is { } v)
        {
            // Millivolts, then flags: bit 7 is on the charger, bits 0-1 then say charging (0) or full (1).
            int millivolts = v[4] << 8 | v[5];
            int percent = PercentFromVoltage(millivolts);
            bool charging = (v[6] & 0x80) != 0 && (v[6] & 0x03) == 0;
            device.Detail = $"battery voltage: {millivolts} mV, flags {v[6]:X2}, about {percent}%";
            return (percent, ControllerBattery.LevelFromPercent(percent), charging ? ChargeState.Charging : ChargeState.Discharging);
        }

        return null;
    }

    /// <summary>
    /// A lithium cell's charge from its voltage, by straight lines between points of a typical discharge
    /// curve (Solaar's table). Close enough for a meter, not exact.
    /// </summary>
    private static int PercentFromVoltage(int millivolts)
    {
        (int Millivolts, int Percent)[] curve =
        [
            (4186, 100), (4067, 90), (3989, 80), (3922, 70), (3859, 60), (3811, 50),
            (3778, 40), (3751, 30), (3717, 20), (3671, 10), (3646, 5), (3579, 2), (3500, 0),
        ];
        if (millivolts >= curve[0].Millivolts)
            return 100;
        for (int i = 1; i < curve.Length; i++)
        {
            var (hiV, hiP) = curve[i - 1];
            var (loV, loP) = curve[i];
            if (millivolts >= loV)
                return loP + (hiP - loP) * (millivolts - loV) / (hiV - loV);
        }
        return 0;
    }

    private readonly record struct Answer(byte[]? Reply, byte? Error);

    /// <summary>
    /// Sends one request and waits for its reply, recognised by our software id, skipping anything else
    /// on the interface (input, notifications, other apps' replies). No reply in time is a null reply.
    /// </summary>
    private async Task<Answer> RequestAsync(FileStream stream, byte slot, byte feature, byte function, byte[] parameters,
        TimeSpan timeout, CancellationToken token)
    {
        // Software ids 1 to 15 tag requests; cycling through them means a late reply never matches the next request.
        byte softwareId = (byte)(Interlocked.Increment(ref _softwareId) % 15 + 1);
        var request = new byte[LongLength];
        request[0] = LongReport;
        request[1] = slot;
        request[2] = feature;
        request[3] = (byte)(function << 4 | softwareId);
        parameters.CopyTo(request, 4);
        await stream.WriteAsync(request, token);

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
        wait.CancelAfter(timeout);
        var buffer = new byte[64];
        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(buffer, wait.Token);
                if (read < 7 || buffer[1] != slot)
                    continue;
                // An error echoes our header after the error marker.
                if ((buffer[2] is ErrorShort or ErrorLong) && buffer[3] == feature && buffer[4] == request[3])
                    return new Answer(null, buffer[5]);
                if (buffer[0] == LongReport && buffer[2] == feature && buffer[3] == request[3])
                    return new Answer(buffer[..LongLength], null);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new Answer(null, null);
        }
    }

    public void Dispose()
    {
        _notices.Dispose();
        _watch.Dispose();
        lock (_links)
        {
            foreach (var link in _links.Values)
                link.Stop.Cancel();
        }
    }
}
