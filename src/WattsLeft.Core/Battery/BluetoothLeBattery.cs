using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WattsLeft.Battery;

/// <summary>A connected Bluetooth LE device's battery level as Windows records it (what Settings shows).</summary>
internal sealed record BleBattery(string Address, string Name, ushort VendorId, ushort ProductId, int Percent);

/// <summary>
/// Reads the battery percentage Windows caches from a Bluetooth LE device's GATT Battery Service.
/// Xbox controllers on Bluetooth report a bogus fixed value through Windows.Gaming.Input
/// (e.g. 100/1000 mWh at full charge), but the GATT value here is accurate.
/// </summary>
internal static unsafe partial class BluetoothLeBattery
{
    // Undocumented devnode properties, the same ones the Settings app reads.
    internal static readonly DevPropKey BatteryKey = DevPropKey.Parse("{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2");
    internal static readonly DevPropKey ConnectedKey = DevPropKey.Parse("{83DA6326-97A6-4088-9453-A1923F573B29} 15");

    /// <summary>The connected Bluetooth LE devices with a battery level and a known maker and model.</summary>
    public static List<BleBattery> QueryConnected() =>
        [.. BluetoothDevices.List()
            .Where(d => d.LowEnergy && d.Connected == true && d.VendorId is not null)
            .Select(d => new BleBattery(d.Address, d.Name, d.VendorId!.Value, d.ProductId ?? 0, d.Percent))];

    /// <summary>Every property in the Bluetooth battery property set, for the --list diagnostic.</summary>
    public static IEnumerable<string> DescribeRawProperties(string instanceIdPrefix = @"BTHLE\DEV_")
    {
        DevPropKey[] keys = [.. Enumerable.Range(2, 10).Select(i => BatteryKey with { Id = (uint)i }), ConnectedKey];
        foreach (var device in FindDevices(instanceIdPrefix, keys))
        {
            var values = keys.Where(k => device[k] is not null).Select(k => $"{k.Id}={device[k]}");
            yield return $"{device.Name}: {string.Join("  ", values)}";
        }
    }

    internal static List<DeviceObject> FindDevices(string instanceIdPrefix, DevPropKey[] properties) =>
        DeviceQuery.Find(DeviceQuery.ObjectType.Device, [DeviceQuery.InstanceId, .. DeviceQuery.NameKeys, .. properties],
            new DeviceQuery.Filter(DeviceQuery.InstanceId, BeginsWith: instanceIdPrefix));

    /// <summary>Every device node in a container: the parts Windows installed for one physical device.</summary>
    internal static List<DeviceObject> FindByContainer(Guid container, DevPropKey property) =>
        DeviceQuery.Find(DeviceQuery.ObjectType.Device, [property], new DeviceQuery.Filter(DeviceQuery.ContainerId, EqualsGuid: container));

    internal static string InstanceId(DeviceObject device) => device[DeviceQuery.InstanceId] as string ?? device.Id;

    // Reading straight from the device, through Windows' Bluetooth GATT API: diagnostics only.

    private static readonly Guid BatteryService = ShortUuid(0x180F);
    private const ushort BatteryLevel = 0x2A19;
    /// <summary>The interface a Bluetooth LE device itself exposes, which lists all its services.</summary>
    private static readonly Guid LeDeviceInterface = new("781aee18-7733-4ce4-add0-91f41c67b592");
    private static readonly DevPropKey InterfaceClassKey = new(new("026e516e-b814-414b-83cd-856d6fef4822"), 4);
    private static readonly DevPropKey InterfaceEnabledKey = new(new("026e516e-b814-414b-83cd-856d6fef4822"), 3);

    /// <summary>A 16-bit Bluetooth id in its full form.</summary>
    private static Guid ShortUuid(ushort id) => new($"0000{id:x4}-0000-1000-8000-00805f9b34fb");

    /// <summary>
    /// Reads the GATT Battery Level characteristic straight from the device, bypassing the value Windows
    /// caches. Diagnostic only: it's slow and wakes the radio.
    /// </summary>
    public static string ReadLive(string address)
    {
        if (InterfacePath(BatteryService, address) is not string path)
            return "battery service unavailable";
        using var service = OpenGatt(path);
        if (service.IsInvalid)
            return $"battery service unavailable (error {Marshal.GetLastWin32Error()})";
        if (Characteristics(service, null, out int hr) is not { } characteristics)
            return $"battery characteristic unavailable (0x{hr:X8})";
        foreach (var characteristic in characteristics)
        {
            if (characteristic.Uuid.IsShort == 0 || characteristic.Uuid.Short != BatteryLevel)
                continue;
            var value = ReadValue(service, characteristic, out hr);
            return value is { Length: > 0 } ? $"{value[0]}%" : $"read failed (0x{hr:X8})";
        }
        return "battery characteristic unavailable";
    }

    /// <summary>Lists every GATT service and characteristic, reading the readable ones. Diagnostic only.</summary>
    public static IEnumerable<string> DescribeGatt(string address)
    {
        if (InterfacePath(LeDeviceInterface, address) is not string path)
        {
            yield return "device not found";
            yield break;
        }
        using var device = OpenGatt(path);
        if (device.IsInvalid)
        {
            yield return $"device unavailable (error {Marshal.GetLastWin32Error()})";
            yield break;
        }
        var services = Services(device, out int hr);
        yield return $"services: {(services is null ? $"0x{hr:X8}" : "Success")}";
        foreach (var service in services ?? [])
        {
            yield return $"service {service.Uuid.ToGuid()}";
            if (Characteristics(device, service, out hr) is not { } characteristics)
            {
                yield return $"  characteristics: 0x{hr:X8}";
                continue;
            }
            foreach (var c in characteristics)
            {
                string value = "";
                if (c.IsReadable != 0)
                    value = ReadValue(device, c, out hr) is { } bytes ? $" = {Convert.ToHexString(bytes)}" : $" (read 0x{hr:X8})";
                yield return $"  char {c.Uuid.ToGuid()} [{c.Describe()}]{value}";
            }
        }
    }

    /// <summary>The path of an enabled interface of this class that belongs to the device at this address.</summary>
    private static string? InterfacePath(Guid interfaceClass, string address) =>
        DeviceQuery.Find(DeviceQuery.ObjectType.DeviceInterface, [],
                new DeviceQuery.Filter(InterfaceClassKey, EqualsGuid: interfaceClass), new DeviceQuery.Filter(InterfaceEnabledKey, EqualsBool: true))
            .Select(i => i.Id)
            .FirstOrDefault(p => p.Contains(address, StringComparison.OrdinalIgnoreCase));

    private static SafeFileHandle OpenGatt(string path) =>
        DeviceFile.Open(path, DeviceFile.GenericRead | DeviceFile.GenericWrite, overlapped: false);

    private const uint FlagNone = 0, FlagForceReadFromDevice = 4;

    private static GattService[]? Services(SafeFileHandle device, out int hr)
    {
        hr = BluetoothGATTGetServices(device, 0, null, out ushort count, FlagNone);
        if (count == 0)
            return hr < 0 ? null : [];
        var services = new GattService[count];
        fixed (GattService* buffer = services)
            hr = BluetoothGATTGetServices(device, count, buffer, out count, FlagNone);
        return hr < 0 ? null : services[..count];
    }

    private static GattCharacteristic[]? Characteristics(SafeFileHandle device, GattService? service, out int hr)
    {
        var copy = service.GetValueOrDefault();
        GattService* which = service is null ? null : &copy;
        hr = BluetoothGATTGetCharacteristics(device, which, 0, null, out ushort count, FlagNone);
        if (count == 0)
            return hr < 0 ? null : [];
        var characteristics = new GattCharacteristic[count];
        fixed (GattCharacteristic* buffer = characteristics)
            hr = BluetoothGATTGetCharacteristics(device, which, count, buffer, out count, FlagNone);
        return hr < 0 ? null : characteristics[..count];
    }

    private static byte[]? ReadValue(SafeFileHandle device, GattCharacteristic characteristic, out int hr)
    {
        hr = BluetoothGATTGetCharacteristicValue(device, &characteristic, 0, null, out ushort size, FlagForceReadFromDevice);
        if (size < sizeof(uint))
            return null;
        var buffer = new byte[size];
        fixed (byte* value = buffer)
            hr = BluetoothGATTGetCharacteristicValue(device, &characteristic, size, value, out size, FlagForceReadFromDevice);
        if (hr < 0)
            return null;
        // The value: its length, then its bytes.
        int length = (int)Math.Min(BitConverter.ToUInt32(buffer, 0), (uint)(buffer.Length - sizeof(uint)));
        return buffer[sizeof(uint)..(sizeof(uint) + length)];
    }

    /// <summary>A Bluetooth id, either a 16-bit one or a full GUID.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 20)]
    private struct LeUuid
    {
        [FieldOffset(0)] public byte IsShort;
        [FieldOffset(4)] public ushort Short;
        [FieldOffset(4)] public Guid Long;

        public readonly Guid ToGuid() => IsShort != 0 ? ShortUuid(Short) : Long;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct GattService
    {
        [FieldOffset(0)] public LeUuid Uuid;
        [FieldOffset(20)] public ushort AttributeHandle;
    }

    [StructLayout(LayoutKind.Explicit, Size = 36)]
    private struct GattCharacteristic
    {
        [FieldOffset(0)] public ushort ServiceHandle;
        [FieldOffset(4)] public LeUuid Uuid;
        [FieldOffset(24)] public ushort AttributeHandle;
        [FieldOffset(26)] public ushort ValueHandle;
        [FieldOffset(28)] public byte IsBroadcastable;
        [FieldOffset(29)] public byte IsReadable;
        [FieldOffset(30)] public byte IsWritable;
        [FieldOffset(31)] public byte IsWritableWithoutResponse;
        [FieldOffset(32)] public byte IsSignedWritable;
        [FieldOffset(33)] public byte IsNotifiable;
        [FieldOffset(34)] public byte IsIndicatable;
        [FieldOffset(35)] public byte HasExtendedProperties;

        /// <summary>What it can do, e.g. "Read, Notify".</summary>
        public readonly string Describe() => string.Join(", ", new (byte Flag, string Name)[]
        {
            (IsBroadcastable, "Broadcast"), (IsReadable, "Read"), (IsWritableWithoutResponse, "WriteWithoutResponse"),
            (IsWritable, "Write"), (IsNotifiable, "Notify"), (IsIndicatable, "Indicate"),
            (IsSignedWritable, "AuthenticatedSignedWrites"), (HasExtendedProperties, "ExtendedProperties"),
        }.Where(p => p.Flag != 0).Select(p => p.Name));
    }

    [LibraryImport("BluetoothApis.dll")]
    private static partial int BluetoothGATTGetServices(SafeFileHandle device, ushort count, GattService* services, out ushort actual, uint flags);

    [LibraryImport("BluetoothApis.dll")]
    private static partial int BluetoothGATTGetCharacteristics(SafeFileHandle device, GattService* service, ushort count,
        GattCharacteristic* characteristics, out ushort actual, uint flags);

    [LibraryImport("BluetoothApis.dll")]
    private static partial int BluetoothGATTGetCharacteristicValue(SafeFileHandle device, GattCharacteristic* characteristic, uint size,
        byte* value, out ushort required, uint flags);
}
