using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Threading;
using WattsLeft.Battery;
using WattsLeft.Battery.Vendor;

namespace WattsLeft;

internal static partial class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // The second half of an update: this is the new version, unpacked, installing itself over the old one.
        if (args.Length == 3 && args[0] == "--finish-update" && int.TryParse(args[2], out int oldProcess))
        {
            UpdateInstall.Finish(args[1], oldProcess);
            return 0;
        }

        if (args.Contains("--list"))
            return ListDevices(CreateSources());

        if (args.Contains("--hid-probe"))
            return ProbeDevices();

        if (args.Contains("--check-updates"))
            return CheckForUpdates();

        // Design aids, rendered off screen: they need Avalonia running, but no tray or sources.
        if (args.Length == 2 && args[0] == "--preview-tray")
            return RunPreview(args, () => Preview.WriteTraySheet(args[1]));
        if (args.Length == 2 && args[0] == "--preview-icons")
            return RunPreview(args, () => Preview.WriteIconSheet(args[1]));
        if (args.Length == 2 && args[0] == "--dump-icons")
            return RunPreview(args, () => Preview.DumpIcons(args[1]));
        if (args.Length is 2 or 3 && args[0] == "--preview-settings")
            return RunPreview(args, () => Preview.WriteSettingsSheet(args[1], minimum: args.Length == 3 && args[2] == "min"));

        if (args.Contains("--demo"))
        {
            App.Startup = Preview.RunToastDemo;
            return Run(args);
        }

        // One copy at a time, so two never read devices at once.
        using var singleInstance = new Mutex(initiallyOwned: true, @"Local\WattsLeft.SingleInstance", out bool isFirst);
        if (!isFirst)
            return 0;

        UpdateInstall.CleanUp();
        Log.Trim();
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"Unhandled exception: {e.ExceptionObject}");
        var sources = CreateSources();
        App.Startup = () =>
        {
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                Log.Write($"Unhandled UI exception: {e.Exception}");
                e.Handled = true;
            };
            var tray = new TrayController(sources, () => App.Exit());
            App.Exiting += tray.Dispose;
        };
        return Run(args);
    }

    /// <summary>
    /// Runs Avalonia, drawing on the CPU: for a tray app's few small windows it looks the same, and it keeps the
    /// GPU, and its driver's tens of megabytes, out of an app that runs all day alongside games.
    /// </summary>
    private static int Run(string[] args) =>
        AppBuilder.Configure<App>().UsePlatformDetect()
            .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] })
            .StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);

    /// <summary>
    /// Runs a design aid, then exits. A failure is printed and ends it with exit code 1, for CI to catch, instead
    /// of leaving it running.
    /// </summary>
    private static int RunPreview(string[] args, Action preview) => RunPreview(args, () =>
    {
        preview();
        return Task.CompletedTask;
    });

    /// <inheritdoc cref="RunPreview(string[], Action)"/>
    private static int RunPreview(string[] args, Func<Task> preview)
    {
        App.Startup = async () =>
        {
            try
            {
                await preview();
                App.Exit();
            }
            catch (Exception ex)
            {
                AttachConsole(-1);
                Console.Error.WriteLine(ex);
                App.Exit(1);
            }
        };
        return Run(args);
    }

    private static IBatterySource[] CreateSources()
    {
        // The Xbox sources see the same controllers, so only the best one that finds any is used.
        var xbox = new FirstAvailableSource("Xbox", new GamingInputSource(), new XInputSource());
        var playStation = new PlayStationSource();
        // Every other Bluetooth device with a battery Windows records: mice, keyboards, headsets.
        var bluetooth = new BluetoothSource();
        // Logitech mice, keyboards and headsets, on their USB receivers or cables.
        var logitech = new LogitechSource();
        // Other makers' devices on their own receivers, each through its maker's protocol.
        var vendors = new VendorHidSource(VendorProtocols.All);
        return [xbox, playStation, bluetooth, logitech, vendors];
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);

    /// <summary>Prints what every source sees, for diagnosing which APIs work with a given device.</summary>
    private static int ListDevices(IEnumerable<IBatterySource> sources)
    {
        AttachConsole(-1); // ATTACH_PARENT_PROCESS, since this is a WinExe.
        // Windows.Gaming.Input reports already-connected pads asynchronously after startup.
        Thread.Sleep(TimeSpan.FromSeconds(1.5));
        Diagnostics.WriteSources(Console.Out, sources);
        return 0;
    }

    /// <summary>Asks GitHub which release is the latest, as the daily check does, and prints the answer.</summary>
    private static int CheckForUpdates()
    {
        AttachConsole(-1);
        Console.WriteLine($"This version: {UpdateCheck.Current.ToString(3)}");
        try
        {
            var newer = UpdateCheck.FindNewerAsync().GetAwaiter().GetResult();
            Console.WriteLine(newer is null ? "This is the latest version." : $"Newer: {newer.Version.ToString(3)} at {newer.Url}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"The check failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Prints the raw HID reports of connected Xbox and PlayStation controllers, and every Bluetooth LE device's
    /// GATT services, for working out a new device's format. It only listens and reads.
    /// </summary>
    private static int ProbeDevices()
    {
        AttachConsole(-1);
        // The Xbox Wireless Controller over Bluetooth.
        HidProbe.Dump(0x045E, 0x0B13, TimeSpan.FromSeconds(5));
        foreach (ushort productId in PlayStationSource.ProductIds)
            HidProbe.Dump(PlayStationSource.SonyVendorId, productId, TimeSpan.FromSeconds(5));
        foreach (var device in BluetoothLeBattery.QueryConnected())
        {
            Console.WriteLine($"  {device.Name}");
            foreach (var line in BluetoothLeBattery.DescribeGatt(device.Address))
                Console.WriteLine($"    {line}");
        }
        return 0;
    }
}
