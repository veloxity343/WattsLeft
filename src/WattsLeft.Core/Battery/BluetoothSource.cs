using System.Runtime.InteropServices;

namespace WattsLeft.Battery;

/// <summary>
/// Every Bluetooth device whose battery level Windows itself records: the level shown in Settings →
/// Bluetooth &amp; devices, which Windows reads from the device's standard battery service. That covers most
/// Bluetooth mice, keyboards and headsets, over both Bluetooth Low Energy and classic Bluetooth. Xbox and
/// PlayStation controllers are left to their own sources, which know more about them.
/// </summary>
public sealed partial class BluetoothSource : IBatterySource
{
    public string Name => "Bluetooth";

    /// <summary>
    /// Never raised: Windows doesn't announce battery levels changing. A device connecting or going brings a
    /// device notice, which the tray refreshes on, and levels come with each refresh.
    /// </summary>
    public event EventHandler? ControllersChanged
    {
        add { }
        remove { }
    }

    private readonly ChargeTrends _trends = new();
    /// <summary>A device's type doesn't change, so it's looked up once per address.</summary>
    private readonly Dictionary<string, DeviceKind> _kinds = [];


    // Device setup classes Windows files a device's parts under.
    private static readonly Guid KeyboardClass = new("4d36e96b-e325-11ce-bfc1-08002be10318");
    private static readonly Guid MouseClass = new("4d36e96f-e325-11ce-bfc1-08002be10318");
    private static readonly Guid AudioEndpointClass = new("c166523c-fe0c-4a94-a586-f1a80cfbbf3e");
    private static readonly Guid MediaClass = new("4d36e96c-e325-11ce-bfc1-08002be10318");

    /// <summary>Held while reading: the tray and the diagnostics report can read at once, and both update what's kept.</summary>
    private readonly object _readGate = new();

    public IReadOnlyList<ControllerBattery> Read()
    {
        lock (_readGate)
            return ReadLocked();
    }

    private List<ControllerBattery> ReadLocked()
    {
        var devices = BluetoothDevices.List();
        if (devices.Count == 0)
            return [];
        var classic = ClassicDevices();

        var readings = new List<ControllerBattery>();
        foreach (var device in devices)
        {
            var (connected, kind) = Ask(device, device.LowEnergy ? null : classic.GetValueOrDefault(device.Address));
            if (connected != true)
                continue;
            // Xbox and PlayStation controllers have sources of their own, which see more than the level.
            if (kind == DeviceKind.Gamepad && device.VendorId is 0x045E or PlayStationSource.SonyVendorId)
                continue;
            if (device.LowEnergy && device.VendorId == 0x045E && device.Name.Contains("Xbox", StringComparison.OrdinalIgnoreCase))
                continue;

            string id = $"bt:{device.Address}";
            readings.Add(new ControllerBattery(id, device.Name, Name, device.Percent,
                ControllerBattery.LevelFromPercent(device.Percent),
                _trends.Charging(id, device.Percent) ? ChargeState.Charging : ChargeState.Discharging,
                device.VendorId is ushort vid ? $"{vid:X4}:{device.ProductId:X4}" : null,
                Connection.Bluetooth, kind));
        }
        _trends.KeepOnly(readings.Select(r => r.Id));
        return readings;
    }

    /// <summary>What the Bluetooth stack records about a paired classic device: whether it's connected, and its class.</summary>
    private sealed record Endpoint(bool Connected, uint ClassOfDevice);

    /// <summary>
    /// Every paired classic device the Bluetooth stack knows, by address: whether it's connected right now,
    /// and its class of device. It's a listing of what the stack already knows, with no inquiry, so it's quick.
    /// </summary>
    private static Dictionary<string, Endpoint> ClassicDevices()
    {
        var devices = new Dictionary<string, Endpoint>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var search = new SearchParams
            {
                Size = Marshal.SizeOf<SearchParams>(),
                ReturnAuthenticated = 1, ReturnRemembered = 1, ReturnConnected = 1,
            };
            var info = new DeviceInfo { Size = Marshal.SizeOf<DeviceInfo>() };
            IntPtr find = BluetoothFindFirstDevice(search, ref info);
            if (find == IntPtr.Zero)
                return devices;
            try
            {
                do
                {
                    devices[info.Address.ToString("X12")] = new Endpoint(info.Connected != 0, info.ClassOfDevice);
                    info = new DeviceInfo { Size = Marshal.SizeOf<DeviceInfo>() };
                }
                while (BluetoothFindNextDevice(find, ref info));
            }
            finally
            {
                BluetoothFindDeviceClose(find);
            }
        }
        catch (Exception ex)
        {
            Log.Once($"Bluetooth lookup failed: {ex.GetType().Name}: {ex.Message}");
        }
        return devices;
    }

    /// <summary>
    /// Whether it's connected right now, and what sort of device it is. Classic devices are asked of the
    /// Bluetooth stack; for those the connected flag Windows records beside the battery level is only the
    /// fallback, as it can read as disconnected for a headset playing audio. Low Energy devices use that flag,
    /// and the appearance Windows keeps from pairing.
    /// </summary>
    private (bool? Connected, DeviceKind Kind) Ask(BluetoothDevice device, Endpoint? endpoint)
    {
        bool? connected = endpoint?.Connected ?? device.Connected;
        bool knowKind = _kinds.TryGetValue(device.Address, out var kind);
        if (!knowKind)
        {
            kind = DeviceKind.Other;
            try
            {
                if (device.LowEnergy && Appearance(device.Address) is uint appearance)
                    kind = KindFromAppearance(appearance);
                else if (!device.LowEnergy && endpoint is not null)
                    kind = KindFromClass(endpoint.ClassOfDevice);
            }
            catch (Exception ex)
            {
                Log.Once($"Bluetooth device type lookup failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (!knowKind)
        {
            // Many devices don't say what they are over Bluetooth (a keyboard can advertise nothing), but
            // Windows files the parts it installs for them by class, as Settings shows.
            if (kind == DeviceKind.Other && device.Container is Guid container)
                kind = KindFromParts(container);
            if (kind == DeviceKind.Other && device.Audio)
                kind = DeviceKind.Headset;
            _kinds[device.Address] = kind;
        }
        return (connected, kind);
    }

    /// <summary>
    /// The appearance a Low Energy device advertises (what sort of device it says it is), from the Bluetooth
    /// stack's own record of the device in the registry, where Windows keeps it from pairing.
    /// </summary>
    private static uint? Appearance(string address)
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            $@"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices\{address.ToLowerInvariant()}");
        return key?.GetValue("LEAppearance") is int appearance ? (uint)appearance : null;
    }

    /// <summary>
    /// A device's type from the parts Windows installed for it: a keyboard part makes it a keyboard (keyboards
    /// often have a mouse part too, for media keys or a touchpad), else a mouse part a mouse, else an audio
    /// part a headset.
    /// </summary>
    private static DeviceKind KindFromParts(Guid container)
    {
        try
        {
            var classes = BluetoothLeBattery.FindByContainer(container, DeviceQuery.ClassGuid)
                .Select(part => part[DeviceQuery.ClassGuid] as Guid?)
                .OfType<Guid>()
                .ToHashSet();
            if (classes.Contains(KeyboardClass))
                return DeviceKind.Keyboard;
            if (classes.Contains(MouseClass))
                return DeviceKind.Mouse;
            if (classes.Contains(AudioEndpointClass) || classes.Contains(MediaClass))
                return DeviceKind.Headset;
        }
        catch (Exception ex)
        {
            Log.Once($"Bluetooth device parts lookup failed: {ex.GetType().Name}: {ex.Message}");
        }
        return DeviceKind.Other;
    }

    /// <summary>A Bluetooth Low Energy device's type, from the GAP Appearance it advertises.</summary>
    internal static DeviceKind KindFromAppearance(uint appearance)
    {
        uint category = appearance >> 6, subcategory = appearance & 0x3F;
        return category switch
        {
            0x0F => subcategory switch // Human Interface Device
            {
                1 => DeviceKind.Keyboard,
                2 => DeviceKind.Mouse,
                3 or 4 => DeviceKind.Gamepad, // joystick, gamepad
                _ => DeviceKind.Other,
            },
            0x25 => DeviceKind.Headset, // wearable audio device: earbuds, headset, headphones, neckband
            _ => DeviceKind.Other,
        };
    }

    /// <summary>A classic Bluetooth device's type, from its Class of Device.</summary>
    internal static DeviceKind KindFromClass(uint classOfDevice)
    {
        uint major = (classOfDevice >> 8) & 0x1F, minor = (classOfDevice >> 2) & 0x3F;
        return major switch
        {
            4 when minor is 1 or 2 or 6 => DeviceKind.Headset, // audio: wearable headset, hands-free, headphones
            5 when (minor & 0x0F) is 1 or 2 => DeviceKind.Gamepad, // peripheral: joystick, gamepad
            5 => (minor >> 4) switch // peripheral: keyboard, pointing device, or both
            {
                1 or 3 => DeviceKind.Keyboard,
                2 => DeviceKind.Mouse,
                _ => DeviceKind.Other,
            },
            _ => DeviceKind.Other,
        };
    }

    // The classic Bluetooth API (bluetoothapis.h): listing the devices the stack remembers.
    [StructLayout(LayoutKind.Sequential)]
    private struct SearchParams
    {
        public int Size;
        public int ReturnAuthenticated, ReturnRemembered, ReturnUnknown, ReturnConnected, IssueInquiry;
        public byte TimeoutMultiplier;
        public IntPtr Radio; // all radios
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct DeviceInfo
    {
        public int Size;
        public ulong Address;
        public uint ClassOfDevice;
        public int Connected, Remembered, Authenticated;
        public fixed ushort LastSeen[8], LastUsed[8];
        public fixed char Name[248];
    }

    [LibraryImport("bthprops.cpl", SetLastError = true)]
    private static partial IntPtr BluetoothFindFirstDevice(in SearchParams search, ref DeviceInfo info);

    [LibraryImport("bthprops.cpl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BluetoothFindNextDevice(IntPtr find, ref DeviceInfo info);

    [LibraryImport("bthprops.cpl")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BluetoothFindDeviceClose(IntPtr find);
}
