# Windows notes

[← Developer docs](README.md)

What WattsLeft relies on that Windows doesn't document, the Windows behaviour it works around, and the lessons from building and releasing it. None of this is obvious from the code alone, and most of it took real devices to find out. **Read this before changing a battery source, the tray, or the release workflow.**

- [Undocumented details](#undocumented-details)
- [Windows quirks and their workarounds](#windows-quirks-and-their-workarounds)
- [Avalonia](#avalonia)
- [Building and releasing](#building-and-releasing)
- [If something stops working after a Windows update](#if-something-stops-working-after-a-windows-update)

## Undocumented details

These work today, and are what Windows' own Settings app uses, but Microsoft doesn't document them, so a Windows update could change them. Each is read in one place.

| What | Where in the code | What it's for | If it changed |
|---|---|---|---|
| Device property `{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2` | `BluetoothLeBattery.BatteryKey` | A Bluetooth device's battery level, the one Settings → Bluetooth & devices shows. Set on the device's own node (`BTHLE\DEV_<address>` or `BTHENUM\DEV_<address>`). | Bluetooth mice, keyboards and headsets, and Xbox controllers over Bluetooth, lose their battery level. |
| Device property `{83DA6326-97A6-4088-9453-A1923F573B29} 15` | `BluetoothLeBattery.ConnectedKey` | Whether that Bluetooth device is connected right now. | Disconnected devices could show as connected, or the reverse. |
| Registry value `LEAppearance` under `HKLM\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices\<address>` | `BluetoothSource.Appearance` | What a Bluetooth LE device says it is (keyboard, mouse and so on), as Windows recorded it when pairing. | Devices are still sorted by the fallbacks (the parts Windows installed, then audio), so most still get the right icon. |
| Device instance id formats: `BTHLE\DEV_<address>\…`, `BTHLEDEVICE\{service}_DEV_VID&02<vid>_PID&<pid>_REV&<rev>_<address>\…`, `BTHENUM\{service}_VID&0002<vid>_PID&<pid>\…_<address>_…` | Regexes in `BluetoothLeBattery.cs` and `BluetoothSource.cs` | A Bluetooth device's address, and its maker and model ids, which tell Xbox and PlayStation controllers apart from other devices. | Controllers could be shown twice (once by their own source, once as a Bluetooth device), or not matched to their battery. |

**Checking them:** `WattsLeft.exe --list` prints the raw values under *Raw Bluetooth LE battery properties* and *Raw classic Bluetooth battery properties*: property id `2` is the battery level, `15` whether it's connected. Compare them with what Settings shows.

**Finding them again** if one moves: in PowerShell, `Get-PnpDevice -Class Bluetooth | Get-PnpDeviceProperty` lists every property of every Bluetooth device. Look for the value that matches the battery level Settings shows, on the `BTHLE\DEV_` or `BTHENUM\DEV_` node.

## Windows quirks and their workarounds

Documented APIs that don't behave as you'd expect.

**Xbox controllers over Bluetooth**
- **Windows.Gaming.Input gives a fixed, wrong battery reading** over Bluetooth: 100 of 1000 mWh, which reads as 10%, whatever the real level. The real level is the Bluetooth battery level above, so `GamingInputSource` pairs each Bluetooth pad with it by address.
- **Right after a reconnect, the wrong 10% can arrive before the real reading.** `ControllerNotifier.SettleTime` (2 seconds) holds a newly connected device's pop-up until it settles.
- **Windows.Gaming.Input's list of pads can go stale** in a long-running process: a DualSense disconnecting once took a Bluetooth Xbox pad out of the list until the pad reconnected. While Windows still reports the pad connected over Bluetooth, it's shown from its Bluetooth reading alone.
- **Windows doesn't say whether a Bluetooth device is charging.** It's worked out from the level rising: see `ChargeTrends.cs`, and [Charging over Bluetooth](../wiki/supported-devices.md#charging-over-bluetooth) for the rules and why it needs a 2-point rise.

**Calling Windows.Gaming.Input without the .NET projection**
- It's a Windows Runtime API, which is COM underneath. `GamingInput.cs` calls each method by its slot in the interface's method table, with ids and slot numbers from Windows' metadata. COM interfaces never change once published (new methods go in new interfaces), so the slots are safe to hard-code.
- The gamepad added and removed events need a handler of type `EventHandler<Gamepad>`. Its interface id isn't listed anywhere: like every generic Windows Runtime interface, it's worked out from its signature (a version 5 UUID, `GamingInput.GenericId`). If that's wrong, subscribing fails with "no such interface".

**Watching for devices**
- **`DevCreateObjectQuery` (the device query API's watcher) fails with `0x8007007E`** in a desktop app, though `DevGetObjects` (a one-off query) works. Changes come instead from the configuration manager's `CM_Register_Notification`, for every device interface class (`DeviceNotices.cs`), and each change triggers a fresh listing.
- **The Bluetooth association endpoint query can block for about 30 seconds.** So classic Bluetooth devices come from the Bluetooth API (`BluetoothFindFirstDevice`, with inquiry off so it never scans) and the registry instead.
- **A HID device is announced a moment before it can be opened.** `HidMonitor` tries one again every second, up to 15 times.
- **A failed listing isn't an empty one.** If the configuration manager can't list devices, `HidMonitor` keeps what it knew and tries again, rather than reporting every device unplugged. Likewise, a source that fails to read keeps its last readings for up to 3 reads (`TrayController`).

**PlayStation controllers over Bluetooth** send a short report without battery data until their calibration feature report is read, which switches them to the full report until they reconnect. That's a read, not a command, but it changes what the controller sends: see [the limitation it causes](../wiki/supported-devices.md#known-limitations).

**Logitech receivers:**
- A receiver answers as soon as it's plugged in, but its devices reconnect to it a few seconds later, or only once moved if they're asleep. A device connecting brings its own interfaces with it, so Windows sends a device notice: `LogitechSource` polls its receivers on every notice, and the device shows within half a second of connecting. A new receiver is also polled every 2 seconds for its first 20 seconds, in case a notice is missed.
- **A Lightspeed receiver's empty slots don't answer at all**, so each costs a full timeout (0.7 s). Polls between the regular 30-second ones ask only the slots a receiver has had a device in (`_slotsInUse`); the regular poll still asks all six, so a newly paired device is found.
- Each change is reported as soon as its slot is read, not after every slot has been asked.

**The tray icon's place is remembered by the exe's path.** Windows keeps whether an icon is pinned beside the clock by the program's full path, so the exe must keep its name (`WattsLeft.exe`) and its install folder from one version to the next. The rename from ControllerHP lost everyone's pin once; don't rename the exe again without a good reason.

## Avalonia

- **`Shutdown()` doesn't raise `ShutdownRequested`.** That event is only for Windows ending the session. Clean-up hangs off the lifetime's `Exit` event, which every way of exiting raises (`App.cs`).
- **Start-up work is posted to the dispatcher**, so it runs once the message loop is going and can end the app itself, as the design aids do.
- **The tray icons don't use Avalonia's `TrayIcon`.** `Platform/Windows/NotifyIcons.cs` calls `Shell_NotifyIcon` directly, for one icon per device with stable ids, the click position to open the panel at, and Explorer restarts.
- **The interface is drawn in code**, with no XAML: custom controls derive from `ThemedControl` and `ThemedPanel`. `ThemedPanel` derives from `Control`, not `Panel`, because `Panel.Render` is sealed.
- **Pop-ups close on any click outside them** through a low-level mouse hook while one is open, since a window opened from the tray may never get focus, and so never lose it.

## Building and releasing

- **Native AOT only compiles on Windows.** Cross-compiling from Linux or macOS isn't supported, so releases are built on a Windows runner. Day-to-day builds (`deploy.sh`, from WSL) are ordinary .NET builds.
- **`SelfContained` must not be `false` in a native build.** The project sets it to `false` only when `PublishAot` isn't on.
- **In the release job, check out before downloading the build.** Checkout clears the workspace, so it deleted a zip downloaded before it. That's how 0.4.0 shipped without its download.
- **Releases are immutable once published** (a repository setting). Nothing can be added to or changed in one afterwards, so the workflow makes a draft, attaches the zip, then publishes. A broken published release can't be fixed: release the next patch version instead.
- **CI's test job** runs the downloaded zip on a clean Windows machine: `--list` and `--preview-settings`. It doesn't run the tray app or see any devices, so try a release candidate on real hardware before tagging.
- **The release notes are the changelog's section** for that version. A full release without one stops the release job.
- **Every release needs its `.sha256` file.** Updating from the app downloads `WattsLeft-x.y.z-win-x64.zip.sha256` from the release first, and refuses an update without one or that doesn't match. CI makes it in the Package step and its test job checks it. Releases before 0.5.0 have none, so they can't be updated to from the app.
- **An update can't overwrite the running exe.** So the app hands over to the new version, unpacked in `%LOCALAPPDATA%\WattsLeft\update`, which waits for the old one to close, copies itself into the old folder (renaming the old files to `.old` first, and putting them back if anything fails), and starts from there. The folder is the same, so the tray icon keeps its place.

## If something stops working after a Windows update

1. Run `WattsLeft.exe --list`. It shows what each source sees, raw values included. Compare with what Settings → Bluetooth & devices shows.
2. Check `%LOCALAPPDATA%\WattsLeft\error.log` for a source that started failing.
3. If Bluetooth levels are missing, check the [undocumented details](#undocumented-details) first, and find them again as described there.
4. Only ever read from devices: never send a guessed command to find out what changed.
