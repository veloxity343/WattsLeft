using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace WattsLeft.Battery;

/// <summary>
/// One HID collection of a device: where it is, what it is, and the report sizes Windows gives for it.
/// Sizes are read through a handle that can neither read nor write, so looking costs the device nothing.
/// </summary>
internal sealed partial class HidCollection(string path, ushort vendor, ushort product, ushort usagePage, ushort usage)
{
    public HidCollection(HidInfo info) : this(info.Path, info.Vendor, info.Product, info.UsagePage, info.Usage) { }

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
/// read with a time limit. Every source that talks to a HID device does it through this. Opened shared, so the
/// maker's own software keeps working beside it.
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
