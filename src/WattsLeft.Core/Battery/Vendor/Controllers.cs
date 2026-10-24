namespace WattsLeft.Battery.Vendor;

/// <summary>Nintendo Switch Pro Controllers and Joy-Cons over Bluetooth.</summary>
/// <remarks>
/// Input reports 21 (a subcommand's reply), 30 and 31 (full reports) carry the battery in byte 2: bits 7-5
/// a level of 0, 2, 4, 6 or 8 (empty, critical, low, medium, full) and bit 4 charging. Over Bluetooth a
/// controller starts in a simple mode without it. If an app such as Steam has switched it to full reports,
/// those are read and nothing is sent; otherwise one subcommand that only reads information goes out:
/// output report 01 with neutral rumble data (00 01 40 40, twice) and subcommand 02, device info, whose 21
/// reply carries the header. The mode is never changed. Not read over USB, where it needs a handshake that
/// changes its USB state.
/// </remarks>
internal sealed class Nintendo : VendorProtocol
{
    public override string Name => "Nintendo";

    public override IReadOnlyCollection<ushort> Vendors { get; } = [0x057E];

    private static readonly Dictionary<ushort, string> Models = new()
    {
        [0x2006] = "Nintendo Joy-Con (L)",
        [0x2007] = "Nintendo Joy-Con (R)",
        [0x2009] = "Nintendo Switch Pro Controller",
    };

    public override VendorModel? Identify(ushort vendor, ushort product) =>
        Models.TryGetValue(product, out var name) ? new VendorModel(name, $"{product:X4}", DeviceKind.Gamepad) : null;

    private static readonly TimeSpan Listen = TimeSpan.FromMilliseconds(100), ReplyWait = TimeSpan.FromMilliseconds(600);

    public override async Task<VendorReading?> ReadAsync(VendorDevice device, CancellationToken token)
    {
        foreach (var (collection, _) in device.ByPreference)
        {
            if (!collection.IsBluetooth)
                continue;
            using var link = HidLink.Open(collection);
            if (link is null)
                continue;
            static bool CarriesBattery(byte[] r) => r.Length > 2 && r[0] is 0x21 or 0x30 or 0x31;
            var r = await link.ReadAsync(r => r.Length > 2 && r[0] is 0x30 or 0x31, Listen, token);
            if (r is null)
            {
                byte counter = (byte)(device.Memo.GetValueOrDefault("counter") is byte c ? c + 1 : 0);
                device.Memo["counter"] = (byte)(counter & 0x0F);
                r = await link.AskAsync(link.OutputReport(0x01, [(byte)(counter & 0x0F), 0x00, 0x01, 0x40, 0x40, 0x00, 0x01, 0x40, 0x40, 0x02]),
                    CarriesBattery, ReplyWait, token);
            }
            if (r is null)
                continue;
            int level = (r[2] >> 4) & 0x0E;
            var power = (r[2] & 0x10) != 0 ? ChargeState.Charging : ChargeState.Discharging;
            var coarse = level switch
            {
                >= 8 => BatteryLevel.Full,
                6 => BatteryLevel.Medium,
                4 => BatteryLevel.Low,
                _ => BatteryLevel.Empty,
            };
            return new VendorReading(null, power, coarse, Connection: Connection.Bluetooth);
        }
        return null;
    }
}

/// <summary>The Steam Controller (2025) on its puck.</summary>
/// <remarks>
/// Nothing is ever sent: changing the controller's settings would change how it behaves in games. The puck
/// relays a controller on each of interfaces 2 to 5 and, while it's awake, pushes input report 43 about
/// every 2.5 seconds: the charge state in byte 1 (1 discharging, 2 charging, 4 charged) and the level in
/// byte 2. Reports 79 and 46 carry the link state in byte 1, 1 meaning disconnected. A listener keeps each
/// slot open, and the last level stays for as long as the puck is plugged in.
/// </remarks>
internal sealed class SteamPuck : VendorProtocol
{
    public override string Name => "Steam";

    public override IReadOnlyCollection<ushort> Vendors { get; } = [0x28DE];

    public override VendorModel? Identify(ushort vendor, ushort product) =>
        product is 0x1304 or 0x1305 ? new VendorModel("Steam Controller", $"puck-{product:X4}", DeviceKind.Gamepad) : null;

    public override TimeSpan SilentKeep(VendorModel model) => TimeSpan.MaxValue;

    public override Task<VendorReading?> ReadAsync(VendorDevice device, CancellationToken token)
    {
        // Each slot's collection: FF00:0001, else any vendor page, else the gamepad usage.
        var slots = device.Collections.Values.Select(c => c.Collection)
            .Where(c => c.Interface is >= 2 and <= 5)
            .GroupBy(c => c.Interface)
            .Select(g => g.OrderBy(c => c.UsagePage == 0xFF00 && c.Usage == 0x0001 ? 0 : c.UsagePage >= 0xFF00 ? 1 :
                c.UsagePage == 0x0001 && c.Usage == 0x0005 ? 2 : 3).First());
        foreach (var slot in slots)
        {
            string heard = $"heard {slot.Path}";
            ReportListener.Ensure(device, $"listener {slot.Path}", slot, r => r.Length > 2 && r[0] is 0x43 or 0x79 or 0x46, r =>
            {
                if (r[0] == 0x43 && r[2] <= 100)
                    device.Memo[heard] = new VendorReading(r[2], r[1] == 2 ? ChargeState.Charging : ChargeState.Discharging);
                else if (r[0] is 0x79 or 0x46 && r[1] == 1)
                    device.Memo.Remove(heard); // disconnected
            }, token);
        }
        lock (device.Memo)
        {
            // The lowest level of the controllers heard, if more than one is paired.
            var heard = device.Memo.Where(kv => kv.Key.StartsWith("heard ")).Select(kv => kv.Value).OfType<VendorReading>();
            return Task.FromResult(heard.OrderBy(r => r.Percent).FirstOrDefault());
        }
    }
}

/// <summary>8BitDo controllers in their own (D-input) mode: SF30 Pro, SN30 Pro, Pro 2 and Pro 3.</summary>
/// <remarks>
/// Nothing is ever sent: switching on the report that carries the battery breaks input for DirectInput
/// games. So it's read only while Steam or a game has switched it on: the "enhanced" input report (01 over
/// Bluetooth, 04 over USB; always 04 on the Pro 3), with the level in bits 0-6 of byte 14 and bit 7
/// charging. A 0 there is padding of the ordinary report, not an empty battery. (In XInput mode these
/// controllers read as Xbox controllers.)
/// </remarks>
internal sealed class EightBitDo : VendorProtocol
{
    public override string Name => "8BitDo";

    public override IReadOnlyCollection<ushort> Vendors { get; } = [0x2DC8];

    private static readonly Dictionary<ushort, string> Models = new()
    {
        [0x6000] = "8BitDo SF30 Pro", [0x6100] = "8BitDo SF30 Pro",
        [0x6001] = "8BitDo SN30 Pro", [0x6101] = "8BitDo SN30 Pro",
        [0x6003] = "8BitDo Pro 2", [0x6006] = "8BitDo Pro 2",
        [0x6009] = "8BitDo Pro 3",
    };

    public override VendorModel? Identify(ushort vendor, ushort product) =>
        Models.TryGetValue(product, out var name) ? new VendorModel(name, $"{product:X4}", DeviceKind.Gamepad, Detail: product) : null;

    public override async Task<VendorReading?> ReadAsync(VendorDevice device, CancellationToken token)
    {
        foreach (var (collection, model) in device.ByPreference)
        {
            if (collection.UsagePage != 0x0001 || collection.Usage != 0x0005)
                continue;
            using var link = HidLink.Open(collection);
            if (link is null)
                continue;
            byte reportId = (ushort)model.Detail! == 0x6009 || !collection.IsBluetooth ? (byte)0x04 : (byte)0x01;
            var r = await link.ReadAsync(r => r.Length > 14 && r[0] == reportId && (r[14] & 0x7F) is >= 1 and <= 100,
                TimeSpan.FromMilliseconds(400), token);
            if (r is not null)
            {
                return new VendorReading(r[14] & 0x7F, (r[14] & 0x80) != 0 ? ChargeState.Charging : ChargeState.Discharging,
                    Connection: collection.IsBluetooth ? Connection.Bluetooth : Connection.Usb);
            }
        }
        return null;
    }
}
