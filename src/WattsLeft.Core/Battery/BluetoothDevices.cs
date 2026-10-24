using System.Text.RegularExpressions;

namespace WattsLeft.Battery;

/// <summary>A paired Bluetooth device Windows records a battery level for, with what its services say about it.</summary>
/// <param name="Address">Its Bluetooth address: twelve hex digits, upper case.</param>
/// <param name="Connected">From Windows' record of the device; null if it doesn't say (classic devices, often).</param>
/// <param name="Container">The id Windows groups a device's parts under.</param>
/// <param name="Audio">Whether it offers an audio service (headphones, a headset).</param>
internal sealed record BluetoothDevice(string Address, string Name, bool LowEnergy, int Percent, bool? Connected, Guid? Container,
    ushort? VendorId, ushort? ProductId, bool Audio);

/// <summary>
/// The Bluetooth devices Windows records a battery level for, Low Energy and classic, from its device nodes. The
/// sources that need Bluetooth batteries share this one listing; it's kept for a moment, so sources reading in
/// the same refresh ask Windows once.
/// </summary>
internal static partial class BluetoothDevices
{
    /// <summary>A refresh reads every source within milliseconds; this covers it, and nothing goes stale.</summary>
    private static readonly TimeSpan KeptFor = TimeSpan.FromSeconds(1);
    private static readonly object Gate = new();
    private static List<BluetoothDevice>? _kept;
    private static DateTime _keptAt;

    // Bluetooth service ids (16-bit, in their full form) that mean a device plays audio: A2DP sink,
    // hands-free, headset and headset audio gateway.
    private static readonly string[] AudioServices = ["0000110b-", "0000111e-", "00001108-", "00001131-"];

    /// <summary>A device's own node, e.g. BTHLE\DEV_70F8AE8F7583\... or BTHENUM\DEV_A0FA9C2B7263\..., which carries the battery level.</summary>
    [GeneratedRegex(@"^BTH(LE|ENUM)\\DEV_([0-9A-F]{12})\\", RegexOptions.IgnoreCase)]
    private static partial Regex DeviceNodeId();

    /// <summary>
    /// A classic device's service node, e.g. BTHENUM\{0000110b-...}_VID&amp;0002054c_PID&amp;0ce6\9&amp;...&amp;A0FA9C2B7263_C00000000:
    /// the service, maybe the maker and model, then the address.
    /// </summary>
    [GeneratedRegex(@"^BTHENUM\\\{([0-9A-F-]{36})\}(?:_VID&[0-9A-F]*?([0-9A-F]{4})_PID&([0-9A-F]{4}))?[^\\]*\\.*?([0-9A-F]{12})_C[0-9A-F]+$", RegexOptions.IgnoreCase)]
    private static partial Regex ClassicServiceNodeId();

    /// <summary>A Low Energy device's service node, e.g. BTHLEDEVICE\{...}_DEV_VID&amp;02045E_PID&amp;0B13_REV&amp;0509_70F8AE8F7583\...</summary>
    [GeneratedRegex(@"^BTHLEDEVICE\\\{([0-9A-F-]{36})\}_(?:DEV_VID&[0-9A-F]*?([0-9A-F]{4})_PID&([0-9A-F]{4})_REV&[0-9A-F]{4}_)?([0-9A-F]{12})\\", RegexOptions.IgnoreCase)]
    private static partial Regex LowEnergyServiceNodeId();

    /// <summary>Every such device now, or as listed less than a second ago.</summary>
    public static IReadOnlyList<BluetoothDevice> List()
    {
        lock (Gate)
        {
            if (_kept is not null && DateTime.UtcNow - _keptAt < KeptFor)
                return _kept;
            _kept = Query();
            _keptAt = DateTime.UtcNow;
            return _kept;
        }
    }

    private sealed class Found(string address, string name, bool lowEnergy, int percent, bool? connected, Guid? container)
    {
        public readonly string Address = address, Name = name;
        public readonly bool LowEnergy = lowEnergy;
        public readonly int Percent = percent;
        public readonly bool? Connected = connected;
        public readonly Guid? Container = container;
        public ushort? VendorId, ProductId;
        public bool Audio;
    }

    private static List<BluetoothDevice> Query()
    {
        var found = new Dictionary<string, Found>(StringComparer.OrdinalIgnoreCase);
        foreach (string prefix in new[] { @"BTHLE\DEV_", @"BTHENUM\DEV_" })
        {
            foreach (var node in BluetoothLeBattery.FindDevices(prefix,
                         [BluetoothLeBattery.BatteryKey, BluetoothLeBattery.ConnectedKey, DeviceQuery.ContainerId]))
            {
                var match = DeviceNodeId().Match(BluetoothLeBattery.InstanceId(node));
                if (!match.Success || node[BluetoothLeBattery.BatteryKey] is not IConvertible battery)
                    continue;
                string address = match.Groups[2].Value.ToUpperInvariant();
                found[address] = new Found(address, node.Name, match.Groups[1].Value.Equals("LE", StringComparison.OrdinalIgnoreCase),
                    Math.Clamp(battery.ToInt32(null), 0, 100), node[BluetoothLeBattery.ConnectedKey] as bool?,
                    node[DeviceQuery.ContainerId] as Guid?);
            }
        }

        // Its maker and model, and whether it plays audio, from the services Windows lists for it.
        if (found.Count > 0)
        {
            foreach (var (prefix, pattern) in new[] { (@"BTHENUM\{", ClassicServiceNodeId()), (@"BTHLEDEVICE\{", LowEnergyServiceNodeId()) })
            {
                foreach (var node in BluetoothLeBattery.FindDevices(prefix, []))
                {
                    var match = pattern.Match(BluetoothLeBattery.InstanceId(node));
                    if (!match.Success || !found.TryGetValue(match.Groups[4].Value, out var device))
                        continue;
                    if (AudioServices.Any(match.Groups[1].Value.ToLowerInvariant().StartsWith))
                        device.Audio = true;
                    if (match.Groups[2].Success && device.VendorId is null)
                    {
                        device.VendorId = Convert.ToUInt16(match.Groups[2].Value, 16);
                        device.ProductId = Convert.ToUInt16(match.Groups[3].Value, 16);
                    }
                }
            }
        }

        return [.. found.Values.Select(d => new BluetoothDevice(d.Address, d.Name, d.LowEnergy, d.Percent, d.Connected, d.Container,
            d.VendorId, d.ProductId, d.Audio))];
    }
}
