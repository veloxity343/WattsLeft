namespace WattsLeft.Battery.Vendor;

/// <summary>The Astro A50 Gen 5 base station.</summary>
/// <remarks>
/// On its FF32:0074 collection: 64-byte frames, report 02, then 0C, the length that follows (3 plus the
/// payload), 00, a command and a handle that comes back in the reply. Command 06 reads the battery: the
/// reply 02 0C .. 00 06 carries the level in byte 6 and, in byte 8, whether the headset is docked, which is
/// how the station says it's charging. The station also pushes frames of the same shape, so only a reply
/// to command 06 counts.
/// </remarks>
internal sealed class Astro : VendorProtocol
{
    public override string Name => "Astro";

    public override IReadOnlyCollection<ushort> Vendors { get; } = [0x046D];

    public override VendorModel? Identify(ushort vendor, ushort product) =>
        product == 0x0B1C ? new VendorModel("Astro A50 Gen 5", "a50-gen5", DeviceKind.Headset) : null;

    private static readonly TimeSpan ReadWait = TimeSpan.FromMilliseconds(300);

    public override async Task<VendorReading?> ReadAsync(VendorDevice device, CancellationToken token)
    {
        var collection = device.Collections.Values.Select(c => c.Collection).FirstOrDefault(c => c.UsagePage == 0xFF32 && c.Usage == 0x0074);
        if (collection is null || HidLink.Open(collection) is not { } link)
            return null;
        using (link)
        {
            while (await link.ReadAsync(_ => true, TimeSpan.FromMilliseconds(30), token) is not null) { }
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var r = await link.AskAsync(link.OutputReport(0x02, [0x0C, 0x03, 0x00, 0x06, 0x0C]),
                    r => r.Length > 8 && r[0] == 0x02 && r[1] == 0x0C && r[4] == 0x06 && r[6] <= 100, ReadWait, token);
                if (r is not null)
                    return new VendorReading(r[6], r[8] != 0 ? ChargeState.Charging : ChargeState.Discharging);
            }
        }
        return null;
    }
}

/// <summary>Audeze Maxwell and Maxwell 2 headsets, on their dongles or USB-C cables.</summary>
/// <remarks>
/// On the FF13:0001 collection: output report 06 07 80 05 5A 03 00 D6 0C asks for the battery attribute
/// (0CD6). The answer is input report 07, a rolling buffer of the dongle's recent replies, so the newest
/// frame carrying the marker D6 0C 00 00 &lt;percent&gt; counts. With no headset linked, the dongle names itself
/// "Audeze Maxwell Dongle" and keeps repeating the last level, so that name means off. There's no charging
/// flag, but a Maxwell always charges on USB-C, so its cable endpoint answering means charging.
/// </remarks>
internal sealed class Audeze : VendorProtocol
{
    public override string Name => "Audeze";

    public override IReadOnlyCollection<ushort> Vendors { get; } = [0x3329];

    private static readonly Dictionary<ushort, (string Name, string Group, bool Cable)> Models = new()
    {
        [0x4B19] = ("Audeze Maxwell", "maxwell", false), [0x4B1A] = ("Audeze Maxwell", "maxwell", true),
        [0x4B18] = ("Audeze Maxwell (Xbox)", "maxwell-xbox", false), [0x4B1E] = ("Audeze Maxwell (Xbox)", "maxwell-xbox", true),
        [0x4B29] = ("Audeze Maxwell 2", "maxwell-2", false),
        [0x4B28] = ("Audeze Maxwell 2 (Xbox)", "maxwell-2-xbox", false),
    };

    public override VendorModel? Identify(ushort vendor, ushort product) =>
        Models.TryGetValue(product, out var m) ? new VendorModel(m.Name, m.Group, DeviceKind.Headset, m.Cable) : null;

    private static readonly TimeSpan ReadWait = TimeSpan.FromMilliseconds(400);

    public override async Task<VendorReading?> ReadAsync(VendorDevice device, CancellationToken token)
    {
        foreach (var (collection, model) in device.ByPreference)
        {
            if (collection.UsagePage != 0xFF13 || collection.Usage != 0x0001)
                continue;
            if (!model.Cable && collection.ProductName()?.Contains("Dongle", StringComparison.OrdinalIgnoreCase) == true)
                return VendorReading.SwitchedOff; // no headset linked
            using var link = HidLink.Open(collection);
            if (link is null)
                continue;
            if (!await link.WriteAsync(link.OutputReport(0x06, [0x07, 0x80, 0x05, 0x5A, 0x03, 0x00, 0xD6, 0x0C]), token))
                continue;
            int? level = null;
            // The answer can take a frame or two; the newest marker wins.
            for (int i = 0; i < 3; i++)
            {
                if (await link.ReadAsync(r => r.Length > 0 && r[0] == 0x07, ReadWait, token) is not { } frame)
                    break;
                level = FindLevel(frame) ?? level;
            }
            if (level is int percent)
                return new VendorReading(percent, model.Cable ? ChargeState.Charging : ChargeState.Discharging);
        }
        return null;
    }

    /// <summary>The percentage after the first D6 0C 00 00 marker that's followed by a real percentage.</summary>
    internal static int? FindLevel(byte[] frame)
    {
        for (int i = 0; i + 4 < frame.Length; i++)
        {
            if (frame[i] == 0xD6 && frame[i + 1] == 0x0C && frame[i + 2] == 0x00 && frame[i + 3] == 0x00 && frame[i + 4] <= 100)
                return frame[i + 4];
        }
        return null;
    }
}

/// <summary>The JBL Quantum 910 Wireless.</summary>
/// <remarks>
/// Nothing is ever sent to it. The headset pushes its battery on its FF13:0001 collection as report 08 with
/// the level in byte 1, but only on events (being plugged in to charge, say), and a report that arrives while
/// nobody's listening is lost. So a listener keeps the collection open, and a reading is kept for as long as
/// the receiver is plugged in. No charging flag.
/// </remarks>
internal sealed class Jbl : VendorProtocol
{
    public override string Name => "JBL";

    public override IReadOnlyCollection<ushort> Vendors { get; } = [0x0ECB];

    public override VendorModel? Identify(ushort vendor, ushort product) =>
        product == 0x2088 ? new VendorModel("JBL Quantum 910 Wireless", "quantum-910", DeviceKind.Headset) : null;

    public override TimeSpan SilentKeep(VendorModel model) => TimeSpan.MaxValue;

    public override Task<VendorReading?> ReadAsync(VendorDevice device, CancellationToken token)
    {
        var collection = device.Collections.Values.Select(c => c.Collection).FirstOrDefault(c => c.UsagePage == 0xFF13 && c.Usage == 0x0001);
        if (collection is null)
            return Task.FromResult<VendorReading?>(null);
        ReportListener.Ensure(device, "listener", collection, r => r.Length > 1 && r[0] == 0x08 && r[1] <= 100,
            r => device.Memo["heard"] = new VendorReading(r[1], ChargeState.Discharging), token);
        lock (device.Memo)
            return Task.FromResult(device.Memo.GetValueOrDefault("heard") as VendorReading);
    }
}

/// <summary>The Logitech G930 wireless headset on its dongle.</summary>
/// <remarks>
/// On the FF00:0001 collection: feature report FF 09 00 FD F4 10 05 B1 BF A0 04, then the same feature
/// report read back three times, 100 ms apart. Byte 13 of the last is the battery, from 44 (empty) to 91
/// (full). No charging flag.
/// </remarks>
internal sealed class LogitechG930 : VendorProtocol
{
    public override string Name => "Logitech G930";

    public override IReadOnlyCollection<ushort> Vendors { get; } = [0x046D];

    public override VendorModel? Identify(ushort vendor, ushort product) =>
        product == 0x0A1F ? new VendorModel("Logitech G930", "g930", DeviceKind.Headset) : null;

    public override async Task<VendorReading?> ReadAsync(VendorDevice device, CancellationToken token)
    {
        var collection = device.Collections.Values.Select(c => c.Collection).FirstOrDefault(c => c.UsagePage == 0xFF00 && c.Usage == 0x0001);
        if (collection is null || HidLink.Open(collection) is not { } link)
            return null;
        using (link)
        {
            if (!link.SetFeature(link.FeatureReport(0xFF, [0x09, 0x00, 0xFD, 0xF4, 0x10, 0x05, 0xB1, 0xBF, 0xA0, 0x04])))
                return null;
            byte[]? r = null;
            for (int i = 0; i < 3; i++)
            {
                if (i > 0)
                    await Task.Delay(TimeSpan.FromMilliseconds(100), token);
                r = link.GetFeature(0xFF);
                if (r is null)
                    return null;
            }
            if (r is not { Length: > 13 } || r[13] is 0)
                return null;
            int percent = Math.Clamp((int)Math.Round((r[13] - 44) * 100.0 / (91 - 44)), 0, 100);
            return new VendorReading(percent, ChargeState.Discharging);
        }
    }
}

/// <summary>The Lenovo Wireless VoIP headset on its dongle.</summary>
/// <remarks>
/// On the FF07:0222 collection (interface 3): feature report 24 01, the status command, answered by a
/// 61-byte input report 27 01 &lt;error&gt;: a non-zero error means the headset isn't connected to the dongle,
/// and byte 7 is the level. No charging flag.
/// </remarks>
internal sealed class LenovoVoip : VendorProtocol
{
    public override string Name => "Lenovo";

    public override IReadOnlyCollection<ushort> Vendors { get; } = [0x17EF];

    public override VendorModel? Identify(ushort vendor, ushort product) =>
        product == 0xA07D ? new VendorModel("Lenovo Wireless VoIP Headset", "voip-a07d", DeviceKind.Headset) : null;

    public override async Task<VendorReading?> ReadAsync(VendorDevice device, CancellationToken token)
    {
        var collection = device.Collections.Values.Select(c => c.Collection).FirstOrDefault(c => c.UsagePage == 0xFF07 && c.Usage == 0x0222);
        if (collection is null || HidLink.Open(collection) is not { } link)
            return null;
        using (link)
        {
            while (await link.ReadAsync(_ => true, TimeSpan.FromMilliseconds(30), token) is not null) { }
            if (!link.SetFeature(link.FeatureReport(0x24, [0x01])))
                return null;
            var r = await link.ReadAsync(r => r.Length >= 8 && r[0] == 0x27 && r[1] == 0x01, TimeSpan.FromSeconds(1), token);
            if (r is null)
                return null;
            if (r[2] != 0)
                return VendorReading.SwitchedOff;
            return r[7] <= 100 ? new VendorReading(r[7], ChargeState.Discharging) : null;
        }
    }
}
