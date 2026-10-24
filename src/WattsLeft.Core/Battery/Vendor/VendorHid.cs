using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace WattsLeft.Battery.Vendor;

/// <summary>
/// One HID collection of a device: where it is, what it is, and the report sizes Windows gives for it.
/// Sizes are read through a handle that can neither read nor write, so looking costs the device nothing.
/// </summary>
internal sealed partial class HidCollection(string path, ushort vendor, ushort product, ushort usagePage, ushort usage)
{
    public string Path { get; } = path;
    public ushort Vendor { get; } = vendor;
    public ushort Product { get; } = product;
    public ushort UsagePage { get; } = usagePage;
    public ushort Usage { get; } = usage;

    /// <summary>Whether it's a Bluetooth HID device (its path names the Bluetooth HID service).</summary>
    public bool IsBluetooth => Path.Contains("{00001124-0000-1000-8000-00805f9b34fb}", StringComparison.OrdinalIgnoreCase);

    /// <summary>The USB interface number (MI_xx in the path), or -1 for a device with only one.</summary>
    public int Interface { get; } = InterfaceNumber().Match(path) is { Success: true } m ? Convert.ToInt32(m.Groups[1].Value, 16) : -1;

    [GeneratedRegex(@"&MI_([0-9A-F]{2})", RegexOptions.IgnoreCase)]
    private static partial Regex InterfaceNumber();

    private Hid.HidpCaps? _caps;
    private bool _capsRead;

    private Hid.HidpCaps? Caps
    {
        get
        {
            if (!_capsRead)
            {
                using var probe = Hid.Open(Path, 0);
                _caps = probe.IsInvalid ? null : Hid.GetCaps(probe);
                _capsRead = true;
            }
            return _caps;
        }
    }

    /// <summary>
    /// The product name the device gives now. Read afresh each time: some receivers change it with what's
    /// linked to them.
    /// </summary>
    public string? ProductName() => Hid.ProductName(Path);

    /// <summary>Input, output and feature report lengths, each with its report id byte; 0 if unknown.</summary>
    public int InputLength => Caps?.InputReportByteLength ?? 0;
    public int OutputLength => Caps?.OutputReportByteLength ?? 0;
    public int FeatureLength => Caps?.FeatureReportByteLength ?? 0;

    public override string ToString() =>
        $"{Vendor:X4}:{Product:X4} interface {Interface} usage {UsagePage:X4}:{Usage:X4} in {InputLength} out {OutputLength} feature {FeatureLength}";
}

/// <summary>
/// An open collection, for one exchange: feature reports set and got, output reports written, input reports
/// read with a time limit. Opened shared, so the maker's own software keeps working beside it.
/// </summary>
internal sealed class HidLink : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly FileStream? _stream;
    public HidCollection Collection { get; }

    private HidLink(HidCollection collection, SafeFileHandle handle)
    {
        Collection = collection;
        _handle = handle;
        _stream = new FileStream(handle, FileAccess.ReadWrite, 0, isAsync: true);
    }

    public static HidLink? Open(HidCollection collection)
    {
        var handle = Hid.Open(collection.Path, Hid.GenericRead | Hid.GenericWrite);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }
        return new HidLink(collection, handle);
    }

    /// <summary>A report of the collection's feature length: the report id, then <paramref name="payload"/>.</summary>
    public byte[] FeatureReport(byte reportId, ReadOnlySpan<byte> payload) => Report(Collection.FeatureLength, reportId, payload);

    /// <summary>A report of the collection's output length: the report id, then <paramref name="payload"/>.</summary>
    public byte[] OutputReport(byte reportId, ReadOnlySpan<byte> payload) => Report(Collection.OutputLength, reportId, payload);

    private static byte[] Report(int length, byte reportId, ReadOnlySpan<byte> payload)
    {
        var report = new byte[Math.Max(length, payload.Length + 1)];
        report[0] = reportId;
        payload.CopyTo(report.AsSpan(1));
        return report;
    }

    public bool SetFeature(byte[] report) => Hid.HidD_SetFeature(_handle, report, report.Length);

    /// <summary>The feature report with this id, or null if the device refused.</summary>
    public byte[]? GetFeature(byte reportId)
    {
        var report = new byte[Math.Max(Collection.FeatureLength, 2)];
        report[0] = reportId;
        return Hid.HidD_GetFeature(_handle, report, report.Length) ? report : null;
    }

    public async Task<bool> WriteAsync(byte[] report, CancellationToken token)
    {
        try
        {
            await _stream!.WriteAsync(report, token);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// The first input report <paramref name="match"/> accepts within <paramref name="timeout"/>, skipping
    /// anything else the device sends meanwhile; null if none came. A device that doesn't number its
    /// reports has Windows put a 00 in front; that's dropped, so a reply starts with its first real byte,
    /// as protocol descriptions (written against hidapi) number them.
    /// </summary>
    public async Task<byte[]?> ReadAsync(Func<byte[], bool> match, TimeSpan timeout, CancellationToken token)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
        wait.CancelAfter(timeout);
        var buffer = new byte[Math.Max(Collection.InputLength, 64)];
        try
        {
            while (true)
            {
                int read = await _stream!.ReadAsync(buffer, wait.Token);
                if (read == 0)
                    return null;
                var report = buffer[0] == 0 ? buffer[1..read] : buffer[..read];
                if (match(report))
                    return report;
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Writes a request and waits for the input report that answers it.</summary>
    public async Task<byte[]?> AskAsync(byte[] request, Func<byte[], bool> match, TimeSpan timeout, CancellationToken token) =>
        await WriteAsync(request, token) ? await ReadAsync(match, timeout, token) : null;

    /// <summary>
    /// Sets a feature report, then gets the answer back from the same report id, a few times over while
    /// the device prepares it.
    /// </summary>
    public async Task<byte[]?> AskFeatureAsync(byte[] request, Func<byte[], bool> match, int tries, TimeSpan wait,
        CancellationToken token)
    {
        if (!SetFeature(request))
            return null;
        for (int i = 0; i < tries; i++)
        {
            await Task.Delay(wait, token);
            if (GetFeature(request[0]) is { } reply && match(reply))
                return reply;
        }
        return null;
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _handle.Dispose();
    }
}

/// <summary>A device a protocol recognises, from its vendor and product id.</summary>
/// <param name="Group">Product ids with the same group are one device: say, a mouse's receiver and its cable.</param>
/// <param name="Cable">On its own cable rather than a receiver, so it shows as USB.</param>
internal sealed record VendorModel(string Name, string Group, DeviceKind Kind, bool Cable = false, object? Detail = null);

/// <summary>What a device said when asked.</summary>
/// <param name="Off">It answered that it's switched off or out of range, so it goes from the tray at once.</param>
/// <param name="Connection">How it's connected, when the protocol can tell; otherwise receiver or cable, by model.</param>
internal sealed record VendorReading(int? Percent, ChargeState Power, BatteryLevel? Level = null, string? Name = null, bool Off = false,
    Connection? Connection = null)
{
    public static readonly VendorReading SwitchedOff = new(null, ChargeState.Unknown, Off: true);
}

/// <summary>A device as the engine tracks it: its collections present now, and what's known about it.</summary>
internal sealed class VendorDevice(VendorProtocol protocol, string group)
{
    public VendorProtocol Protocol { get; } = protocol;
    public string Group { get; } = group;
    public Dictionary<string, (HidCollection Collection, VendorModel Model)> Collections { get; } = [];
    /// <summary>For the protocol to keep what it's learned, such as which collection answers.</summary>
    public Dictionary<string, object> Memo { get; } = [];
    public ControllerBattery? Reading;
    public DateTime LastAnswer;
    public int SilentPolls, Skipped;
    /// <summary>Until when it's asked at every quick poll: just after it appeared, while it may still be connecting.</summary>
    public DateTime SettleUntil;

    /// <summary>Its collections, those on the cable first: a device being charged says so there.</summary>
    public IEnumerable<(HidCollection Collection, VendorModel Model)> ByPreference =>
        Collections.Values.OrderByDescending(c => c.Model.Cable);
}

/// <summary>
/// One maker's (or one firmware family's) way of reading a battery over HID. A protocol only ever sends
/// read requests, and only to devices it recognises by vendor and product id.
/// </summary>
internal abstract class VendorProtocol
{
    /// <summary>Shown as the reading's source, and in diagnostics.</summary>
    public abstract string Name { get; }

    public abstract IReadOnlyCollection<ushort> Vendors { get; }

    /// <summary>The model this product is, or null if this protocol doesn't know it.</summary>
    public abstract VendorModel? Identify(ushort vendor, ushort product);

    /// <summary>Asks the device for its battery. Null if nothing answered (asleep, or not there).</summary>
    public abstract Task<VendorReading?> ReadAsync(VendorDevice device, CancellationToken token);

    /// <summary>
    /// How long a device that stops answering keeps its last reading. Many receivers can't tell a sleeping
    /// device from one switched off: a mouse or keyboard that stops answering is most likely asleep, and
    /// shouldn't come and go, while a headset that stops answering has most likely been switched off.
    /// </summary>
    public virtual TimeSpan SilentKeep(VendorModel model) =>
        model.Kind == DeviceKind.Headset ? TimeSpan.FromMinutes(2) : TimeSpan.FromMinutes(30);

    /// <summary>Reply timeouts shared by most protocols.</summary>
    protected static readonly TimeSpan ReplyTimeout = TimeSpan.FromMilliseconds(800);
    protected static readonly TimeSpan FeatureWait = TimeSpan.FromMilliseconds(50);
}

/// <summary>
/// Reads devices through their makers' own HID protocols (<see cref="VendorProtocol"/>), for those whose
/// battery Windows doesn't record: devices on their own USB receivers, mostly. It watches HID collections
/// of the vendors the protocols know, groups them into devices, and asks each every 30 seconds, and also
/// straight away whenever a device notice comes (something connected or went), every 2 seconds for a while
/// after a device appears, so a receiver's device shows as soon as it answers.
/// </summary>
public sealed class VendorHidSource : IBatterySource, IDisposable
{
    public string Name => "Vendor protocols";

    public event EventHandler? ControllersChanged;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    /// <summary>A device with nothing to show is asked a few times at the full rate, then every so many polls.</summary>
    private const int TriesAtFullRate = 3, SilentPollEvery = 5;
    /// <summary>
    /// A receiver appears before the device behind it has connected to it, so a new device is asked every
    /// <see cref="SettlingInterval"/> for this long, rather than waiting for the next poll.
    /// </summary>
    private static readonly TimeSpan SettlingTime = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SettlingInterval = TimeSpan.FromSeconds(2);
    /// <summary>Released to poll now rather than at the next interval.</summary>
    private readonly SemaphoreSlim _wake = new(0);
    private readonly IDisposable _notices;

    private readonly IReadOnlyList<VendorProtocol> _protocols;
    private readonly Dictionary<string, VendorDevice> _devices = [];
    private readonly IDisposable _watch;
    private readonly CancellationTokenSource _stop = new();
    /// <summary>Done once the collections already plugged in have been seen.</summary>
    private readonly Task _listed;

    internal VendorHidSource(IReadOnlyList<VendorProtocol> protocols)
    {
        _protocols = protocols;
        var vendors = protocols.SelectMany(p => p.Vendors).ToHashSet();
        _watch = HidMonitor.Watch(info => vendors.Contains(info.Vendor), OnAdded, OnRemoved, out _listed);
        // A device connecting to its receiver, or switching on, often brings interfaces with it: ask then.
        _notices = DeviceNotices.Watch(Wake);
        _ = Task.Run(() => PollLoopAsync(_stop.Token));
    }

    public IReadOnlyList<ControllerBattery> Read()
    {
        lock (_devices)
            return _devices.Values.Select(d => d.Reading).OfType<ControllerBattery>().ToList();
    }

    /// <summary>Asks every device now and waits, for diagnostics; lists each device's collections.</summary>
    public IReadOnlyList<string> PollNow(TimeSpan timeout)
    {
        _listed.Wait(timeout);
        VendorDevice[] devices;
        lock (_devices)
            devices = [.. _devices.Values];
        Task.WhenAll(devices.Select(d => PollAsync(d, force: true, CancellationToken.None))).Wait(timeout);
        return devices.SelectMany(d => d.Collections.Values
                .Select(c => $"{d.Protocol.Name} {d.Group}: {c.Model.Name}, {c.Collection}")
                .Append($"{d.Protocol.Name} {d.Group}: {(d.Reading?.Describe() ?? "no reading")}"))
            .ToList();
    }

    private void OnAdded(HidInfo info)
    {
        foreach (var protocol in _protocols)
        {
            if (protocol.Identify(info.Vendor, info.Product) is not { } model)
                continue;
            var collection = new HidCollection(info.Path, info.Vendor, info.Product, info.UsagePage, info.Usage);
            string key = $"{protocol.Name}|{model.Group}";
            lock (_devices)
            {
                if (!_devices.TryGetValue(key, out var device))
                    _devices[key] = device = new VendorDevice(protocol, model.Group);
                device.Collections[info.Path] = (collection, model);
                // A cable appearing (charging) or a receiver plugged in is worth asking about straight away,
                // and again shortly, while the device behind it connects.
                device.Skipped = SilentPollEvery;
                device.SettleUntil = DateTime.UtcNow + SettlingTime;
            }
            Wake();
            return;
        }
    }

    private void OnRemoved(string id)
    {
        bool changed = false;
        lock (_devices)
        {
            foreach (var (key, device) in _devices.Where(kv => kv.Value.Collections.Remove(id)).ToList())
            {
                if (device.Collections.Count == 0)
                {
                    _devices.Remove(key);
                    changed |= device.Reading is not null;
                }
            }
        }
        if (changed)
            ControllersChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Wake()
    {
        if (_wake.CurrentCount == 0)
            _wake.Release();
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        try
        {
            await _listed.WaitAsync(TimeSpan.FromSeconds(10), token);
        }
        catch (TimeoutException) { }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!token.IsCancellationRequested)
        {
            VendorDevice[] devices;
            lock (_devices)
                devices = [.. _devices.Values];
            var now = DateTime.UtcNow;
            // Each device on its own, so a slow one doesn't hold up the rest, and each change passed on as soon
            // as it's read. One still settling is always asked.
            try
            {
                await Task.WhenAll(devices.Select(async d =>
                {
                    if (await PollAsync(d, force: d.SettleUntil > now, token))
                        ControllersChanged?.Invoke(this, EventArgs.Empty);
                }));
            }
            catch (OperationCanceledException)
            {
                return;
            }

            bool settling;
            lock (_devices)
                settling = _devices.Values.Any(d => d.SettleUntil > DateTime.UtcNow);
            try
            {
                await _wake.WaitAsync(settling ? SettlingInterval : PollInterval, token);
                while (_wake.CurrentCount > 0)
                    _wake.Wait(0); // one poll answers any number of notices
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Asks one device for its battery. True if its reading changed.</summary>
    private async Task<bool> PollAsync(VendorDevice device, bool force, CancellationToken token)
    {
        var protocol = device.Protocol;
        lock (_devices)
        {
            bool shown = device.Reading is not null;
            if (!force && !shown && device.SilentPolls >= TriesAtFullRate && ++device.Skipped < SilentPollEvery)
                return false;
            device.Skipped = 0;
        }

        VendorReading? answer = null;
        try
        {
            answer = await protocol.ReadAsync(device, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Once($"{protocol.Name} {device.Group} failed: {ex.GetType().Name}: {ex.Message}");
        }

        lock (_devices)
        {
            var before = device.Reading;
            if (answer is { Off: true })
            {
                device.Reading = null;
                device.SilentPolls++;
            }
            else if (answer is { } reading && device.Collections.Count > 0)
            {
                device.SilentPolls = 0;
                device.LastAnswer = DateTime.UtcNow;
                var (collection, model) = device.ByPreference.First();
                int? percent = reading.Percent is int p ? Math.Clamp(p, 0, 100) : null;
                var level = reading.Level ?? (percent is int q ? ControllerBattery.LevelFromPercent(q) : BatteryLevel.Unknown);
                device.Reading = new ControllerBattery($"{protocol.Name.ToLowerInvariant().Replace(' ', '-')}:{device.Group}",
                    reading.Name ?? model.Name, protocol.Name, percent, level, reading.Power,
                    $"{collection.Vendor:X4}:{collection.Product:X4}",
                    reading.Connection ?? (model.Cable ? Connection.Usb : Connection.Receiver), model.Kind);
            }
            else
            {
                device.SilentPolls++;
                if (device.Reading is not null && device.Collections.Count > 0 &&
                    DateTime.UtcNow - device.LastAnswer >= protocol.SilentKeep(device.ByPreference.First().Model))
                    device.Reading = null;
            }
            return device.Reading != before;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _notices.Dispose();
        _watch.Dispose();
    }
}
