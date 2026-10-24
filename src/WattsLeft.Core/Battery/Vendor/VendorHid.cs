
namespace WattsLeft.Battery.Vendor;

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
    private Dictionary<string, (HidCollection Collection, VendorModel Model)> _collections = [];

    /// <summary>
    /// Its collections present now. Replaced whole when one comes or goes, never changed in place, so a protocol
    /// reading it mid-poll always sees a consistent set.
    /// </summary>
    public IReadOnlyDictionary<string, (HidCollection Collection, VendorModel Model)> Collections => _collections;

    public void AddCollection(HidCollection collection, VendorModel model) =>
        _collections = new(_collections) { [collection.Path] = (collection, model) };

    /// <summary>False if it wasn't one of this device's.</summary>
    public bool RemoveCollection(string path)
    {
        if (!_collections.ContainsKey(path))
            return false;
        var remaining = new Dictionary<string, (HidCollection Collection, VendorModel Model)>(_collections);
        remaining.Remove(path);
        _collections = remaining;
        return true;
    }

    /// <summary>Held while it's asked, so its protocol never runs twice at once for it (a poll and diagnostics, say).</summary>
    public readonly SemaphoreSlim Asking = new(1, 1);

    /// <summary>
    /// For the protocol to keep what it's learned, such as which collection answers. Lock it to read or change
    /// it from a listener; polls are one at a time (<see cref="Asking"/>).
    /// </summary>
    public Dictionary<string, object> Memo { get; } = [];
    public ControllerBattery? Reading;
    public DateTime LastAnswer;
    public int SilentPolls, Skipped;

    /// <summary>Its collections, those on the cable first: a device being charged says so there.</summary>
    public IEnumerable<(HidCollection Collection, VendorModel Model)> ByPreference =>
        Collections.Values.OrderByDescending(c => c.Model.Cable);
}

/// <summary>
/// For devices that only speak when something changes (the Steam puck, JBL headsets): a listener per
/// collection, reading the reports it's told to keep, for as long as the collection is there. Nothing is sent.
/// </summary>
internal static class ReportListener
{
    /// <summary>
    /// Starts a listener on <paramref name="collection"/> unless one is already running, under the key
    /// <paramref name="name"/> in the device's memo. Each report <paramref name="match"/> accepts is passed to
    /// <paramref name="heard"/>, with the device's memo locked. It stops when <paramref name="token"/> is
    /// cancelled, or when a read fails at once, which means the collection has gone.
    /// </summary>
    public static void Ensure(VendorDevice device, string name, HidCollection collection, Func<byte[], bool> match,
        Action<byte[]> heard, CancellationToken token)
    {
        lock (device.Memo)
        {
            if (device.Memo.GetValueOrDefault(name) is Task { IsCompleted: false })
                return;
            device.Memo[name] = Task.Run(() => ListenAsync(device, collection, match, heard, token), token);
        }
    }

    /// <summary>A wait this long without a report is ordinary; one that ends sooner without a report means the device went.</summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromMinutes(5), Gone = TimeSpan.FromSeconds(1);

    private static async Task ListenAsync(VendorDevice device, HidCollection collection, Func<byte[], bool> match,
        Action<byte[]> heard, CancellationToken token)
    {
        using var link = HidLink.Open(collection);
        if (link is null)
            return;
        while (!token.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            var report = await link.ReadAsync(match, Quiet, token);
            if (report is not null)
            {
                lock (device.Memo)
                    heard(report);
            }
            else if (DateTime.UtcNow - started < Gone)
            {
                return;
            }
        }
    }
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
/// of the vendors the protocols know, groups them into devices, and asks them on a <see cref="PollSchedule"/>:
/// every 30 seconds, and straight away when something connects or a device appears.
/// </summary>
public sealed class VendorHidSource : IBatterySource, IDisposable
{
    public string Name => "Vendor protocols";

    public event EventHandler? ControllersChanged;

    /// <summary>
    /// A device with nothing to show is asked a few times at every full poll, then at every so many. Quick polls
    /// (something connected, or a device just appeared) ask every device.
    /// </summary>
    private const int TriesAtFullRate = 3, SilentPollEvery = 5;
    private readonly PollSchedule _schedule;

    private readonly IReadOnlyList<VendorProtocol> _protocols;
    private readonly Dictionary<string, VendorDevice> _devices = [];
    private readonly IDisposable _watch;
    /// <summary>Done once the collections already plugged in have been seen.</summary>
    private readonly Task _listed;

    internal VendorHidSource(IReadOnlyList<VendorProtocol> protocols)
    {
        _protocols = protocols;
        var vendors = protocols.SelectMany(p => p.Vendors).ToHashSet();
        // The schedule first, so devices already plugged in can settle on it; it starts once they're listed.
        var listed = new TaskCompletionSource();
        _schedule = new PollSchedule(Name, PollAllAsync, listed.Task);
        _watch = HidMonitor.Watch(info => vendors.Contains(info.Vendor), OnAdded, OnRemoved, out _listed);
        _listed.ContinueWith(_ => listed.TrySetResult(), TaskScheduler.Default);
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
        Task.WhenAll(devices.Select(d => PollAsync(d, force: true, _schedule.Stopping))).Wait(timeout);
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
                device.AddCollection(collection, model);
                // A cable appearing (charging) or a receiver plugged in is worth asking about straight away,
                // and again shortly, while the device behind it connects.
                device.Skipped = SilentPollEvery;
            }
            _schedule.Settle();
            return;
        }
    }

    private void OnRemoved(string id)
    {
        bool changed = false;
        lock (_devices)
        {
            foreach (var (key, device) in _devices.Where(kv => kv.Value.RemoveCollection(id)).ToList())
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

    /// <summary>
    /// Asks every device, each on its own so a slow one doesn't hold up the rest, passing each change on as
    /// soon as it's read. A full poll lets silent devices be skipped; a quick one asks them all.
    /// </summary>
    private Task PollAllAsync(bool full, CancellationToken token)
    {
        VendorDevice[] devices;
        lock (_devices)
            devices = [.. _devices.Values];
        return Task.WhenAll(devices.Select(async d =>
        {
            if (await PollAsync(d, force: !full, token))
                ControllersChanged?.Invoke(this, EventArgs.Empty);
        }));
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
        await device.Asking.WaitAsync(token);
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
        finally
        {
            device.Asking.Release();
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
        _schedule.Dispose();
        _watch.Dispose();
    }
}
