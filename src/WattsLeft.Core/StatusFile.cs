using System.Text.Json;
using System.Text.Json.Serialization;
using WattsLeft.Battery;

namespace WattsLeft;

/// <summary>
/// status.json: every connected device's battery, kept up to date for other apps (Rainmeter skins,
/// scripts and the like) when the "Share battery levels" setting is on. It's replaced in one step, so a
/// reader never sees half a file.
/// </summary>
internal static class StatusFile
{
    public const string FileName = "status.json";
    /// <summary>Bumped if a field changes meaning or goes away; new fields don't bump it.</summary>
    private const int Format = 1;

    public static string FullPath => Path.Combine(Log.Folder, FileName);
    public const string DisplayPath = @"%LOCALAPPDATA%\WattsLeft\" + FileName;

    /// <summary>What was last written, less the time, so an unchanged state isn't rewritten every check.</summary>
    private static string? _written;
    private static bool _failureLogged;

    public static void Write(IReadOnlyList<ControllerBattery> readings, IReadOnlyDictionary<string, string> timeLeft,
        bool running = true)
    {
        var devices = readings.Select(r => new StatusDevice(
            Id: r.Id,
            Name: r.Name,
            DeviceName: r.DeviceName,
            Type: r.Kind switch
            {
                DeviceKind.Gamepad => "controller",
                DeviceKind.Mouse => "mouse",
                DeviceKind.Keyboard => "keyboard",
                DeviceKind.Headset => "headset",
                _ => "other",
            },
            Percent: r.EffectivePercent,
            // false when the source only reports a coarse level (empty, low, medium, full).
            Exact: r.Percent is not null,
            Power: r.Power switch
            {
                ChargeState.Discharging => "battery",
                ChargeState.Charging => "charging",
                ChargeState.Wired => "wired",
                _ => "unknown",
            },
            Connection: r.Connection switch
            {
                Connection.Usb => "usb",
                Connection.Bluetooth => "bluetooth",
                Connection.WirelessAdapter => "wireless-adapter",
                Connection.Receiver => "receiver",
                _ => "unknown",
            },
            TimeLeft: timeLeft.GetValueOrDefault(r.Id))).ToList();

        string state = JsonSerializer.Serialize(new StatusState(running, devices), StatusJson.Default.StatusState);
        // Rewritten if it's gone, say someone deleted it, even though nothing changed.
        if (state == _written && File.Exists(FullPath))
            return;

        // The pid lets a reader tell a running WattsLeft from one that crashed with running still true.
        string json = JsonSerializer.Serialize(
            new StatusDocument("WattsLeft", Format, AppInfo.Version, running, Environment.ProcessId, DateTimeOffset.Now, devices),
            StatusJson.Default.StatusDocument);

        try
        {
            Directory.CreateDirectory(Log.Folder);
            string temp = FullPath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, FullPath, overwrite: true);
            _written = state;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Most likely a reader has it open without sharing; the next check tries again.
            if (!_failureLogged)
                Log.Write($"Couldn't write {FileName}: {e.Message}");
            _failureLogged = true;
        }
    }

    public static void Delete()
    {
        _written = null;
        try
        {
            File.Delete(FullPath);
        }
        catch (DirectoryNotFoundException)
        {
            // No data folder yet, as on a first start, so no file either.
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Couldn't delete {FileName}: {e.Message}");
        }
    }
}

// status.json's shape, field by field, in the order written. The serializer's code for them is generated at
// build time, as a native build needs.
internal sealed record StatusDevice(string Id, string Name, string DeviceName, string Type, int? Percent, bool Exact, string Power,
    string Connection, string? TimeLeft);

internal sealed record StatusState(bool Running, List<StatusDevice> Devices);

internal sealed record StatusDocument(string App, int Format, string Version, bool Running, int Pid, DateTimeOffset Updated,
    List<StatusDevice> Devices);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(StatusState))]
[JsonSerializable(typeof(StatusDocument))]
internal sealed partial class StatusJson : JsonSerializerContext;
