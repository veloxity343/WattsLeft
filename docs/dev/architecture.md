# Architecture

[← Developer docs](README.md)

WattsLeft is a tray app with no window of its own: it lives in the notification area. The code is in two projects:

- `src/WattsLeft.Core`: everything that isn't user interface, with no UI framework.
  - `Battery/`: finding devices and reading their batteries, including each maker's protocol.
  - Battery history and stats, settings, the status file, warning sounds, fullscreen detection, logs (`Log.cs`) and the diagnostics report.
- `src/WattsLeft`: the tray app, on [Avalonia](https://avaloniaui.net/): everything you see (`Ui/`), the tray logic (`TrayController.cs`, `ControllerNotifier.cs`), the Windows tray icons (`Platform/Windows/NotifyIcons.cs`) and the entry point (`Program.cs`). Releases are compiled to one native executable with Native AOT.

## From device to tray

```
 Battery sources ──readings──▶ TrayApp ──▶ tray icons (TrayEntry slots)
  (IBatterySource)    events      │      ├─▶ pop-ups (ControllerNotifier → ToastManager)
                                  │      ├─▶ battery.csv (BatteryHistory)
                                  │      └─▶ flyout / settings window, while open
                                  └── Settings (registry), applied on change
```

1. **Sources** implement `IBatterySource`: a name, a `ControllersChanged` event, and `Read()`, which returns one `ControllerBattery` per device. (The names date from when it only read controllers.) A reading carries a stable id, a name, a percentage or coarse level, a charge state, hardware ids, the connection and the kind of device.
2. **`TrayApp`** refreshes on three triggers: a 30-second poll, a source's event, and a follow-up shortly after a device connects. Events arrive in bursts, so they wait for a 250 ms quiet spell (at most a second) and a burst costs one refresh.
3. **A refresh** reads every source and then:
   - updates the tray icon slots;
   - passes the readings to `ControllerNotifier` for pop-ups;
   - records any changes in `battery.csv` and `history.log`.

## Sources

| Source | File | Notes |
|---|---|---|
| Xbox, via Windows.Gaming.Input | `GamingInputSource.cs` | Pairs Bluetooth pads with their Bluetooth LE battery (`BluetoothLeBattery.cs`), since Windows.Gaming.Input's own figure is wrong over Bluetooth. It also falls back to the Bluetooth LE reading when Windows.Gaming.Input's gamepad list goes stale in a long-running process. Over Bluetooth, charging is inferred as for other Bluetooth devices (`ChargeTrends.cs`). |
| Xbox, via XInput | `XInputSource.cs` | Fallback with four levels. |
| PlayStation | `PlayStationSource.cs` | Opens each controller briefly every 5 seconds and reads one HID input report (`Hid.cs`). It never holds the device open, so tools that want exclusive access aren't blocked. |
| Logitech | `LogitechSource.cs` | HID++ 2.0 over the receiver's vendor collection (FF00:0002, long reports): slots 1 to 6 on a receiver, the direct slot on a cable. For each device, once: its features, name, type and unit id; then every 30 seconds its battery. Requests carry a software id, so replies to other apps (G HUB) are skipped. A device that doesn't answer is asleep and keeps its reading for 30 minutes; one that replies switched off goes at once. Read-only. |
| Other makers' devices | `Vendor/VendorHid.cs` and one file per maker | `VendorHidSource` watches the HID collections of every vendor id a `VendorProtocol` knows, groups a device's collections (receiver and cable) by the protocol's model group, and asks each device every 30 seconds. A protocol only says which product ids it knows, which collection to use, its read request and how to read the answer; `HidLink` gives it feature and output reports out and input reports in, with time limits, and drops the 00 report id Windows puts in front of unnumbered input reports. A silent mouse or keyboard keeps its reading for 30 minutes (some protocols: for as long as the receiver is there), a silent headset for 2; a device that says it's off goes at once; a device with nothing to show is asked every fifth poll after three tries. Devices that only speak on events (JBL, the Steam puck) get a listener. Every protocol is read-only. |
| Other Bluetooth devices | `BluetoothSource.cs` | The battery level Windows records for each paired device, Bluetooth LE or classic. A classic device's connection and Class of Device come from the Bluetooth API (no inquiry); a Low Energy device's connection from its device node, and its Appearance from the Bluetooth stack's record in the registry. Failing those, the kind comes from the device classes of the parts Windows installed (keyboard beats mouse), else audio services. Skips Xbox and PlayStation controllers, which have their own sources. Charging is inferred from the level rising 2 points from its low, and cleared at 100%, on a drop, or after 5 minutes without a rise (`ChargeTrends.cs`). |

The two Xbox sources see the same controllers, so `FirstAvailableSource` uses whichever finds any first. Sources for *different* families are independent, and their readings are combined.

### How the sources reach Windows

The app has no Windows Runtime projection: it calls Windows directly, through a few small files.

- **`HidMonitor.cs`** lists HID collections with the configuration manager and identifies each by opening it with no access. Every HID source subscribes to it.
- **`DeviceNotices.cs`** registers once for configuration-manager notices of any device interface arriving or going, and passes each burst on. `HidMonitor` lists again on it; the Bluetooth and Xbox sources refresh on it.
- **`DeviceQuery.cs`** wraps `DevGetObjects`, which lists device nodes and interfaces with chosen properties, filtered on them: the Bluetooth battery levels and connection flags, device containers and classes. (Its watch counterpart, `DevCreateObjectQuery`, fails in a desktop process, so changes come from `DeviceNotices` and the poll.)
- **`GamingInput.cs`** calls the handful of Windows.Gaming.Input methods used through their COM method tables, with its own handler for the added and removed events. Readings and events were checked against the projection before it was removed.
- **`BluetoothLeBattery.cs`** reads the Battery Level characteristic live through the Win32 GATT API, for diagnostics.

## Tray and UI

| Piece | File | Notes |
|---|---|---|
| Tray icons | `TrayController.cs` (`TraySlot`), `Platform/Windows/NotifyIcons.cs` | Reused slots, so icons keep their taskbar position. Which devices get one: lowest battery first. Kinds turned off under **Show**, and removed devices, are dropped as readings arrive. The icons go through `Shell_NotifyIcon` directly, for their clicks and tooltips. |
| Icon drawing | `Ui/BatteryIcon.cs` | The device's shape with a battery bar. At 16 px each shape is hand-placed pixels; larger sizes draw smooth shapes on a 16×16 grid, scaled. The "no devices" icon is the embedded `app.ico`. |
| Menu | `Ui/TrayMenu.cs` | The right-click menu and its submenus, drawn in the taskbar's colours. |
| Pop-ups | `ControllerNotifier.cs`, `Ui/Toast.cs` | The notifier decides when, and the toast window draws and stacks them, never taking focus. |
| Panel | `Ui/Flyout.cs`, `Ui/PopupWindow.cs` | Opened by left-click. It closes on losing focus, and also on any click outside it via a mouse hook, because rapid tray clicks can leave it without focus. |
| Settings window | `Ui/SettingsWindow.cs`, `Ui/Controls.cs` | Custom-drawn controls in the app's own palette. The page lays its items out to fit any width down to the minimum. |
| Devices page | `BatteryHistory.cs`, `BatteryStats.cs` (Core), `Ui/HistoryChart.cs`, `Ui/StatsCard.cs` | Recording and loading battery history, figures, chart, and stat cards, including Remove. |
| Theming | `Ui/Appearance.cs` | Palettes and Windows 11 styling (rounded corners, dark title bar). The tray menu and panel follow the taskbar's mode; the settings window follows the app mode. |
| Design aids | `Preview.cs` | `--preview-icons` (the README's picture), `--preview-tray`, `--preview-settings` (CI checks this one), `--dump-icons` and `--demo` render the interface off screen, or show sample pop-ups. |
| Settings | `Settings.cs` | `HKCU\Software\WattsLeft`. Raises `Changed` on every write, so the menu and the window stay in step. |

## Design decisions worth knowing

The Windows details the sources rely on, and the quirks they work around, are in [Windows notes](windows-notes.md).

- **Readings are only written when they change.** That keeps `battery.csv` small, and is why the chart holds a level flat until the next row. A heartbeat file (`battery.alive`) lets sessions left open by a crash close at the time the app stopped.
- **Rates are measured between level changes,** not from connection time. A PlayStation controller can be anywhere inside a 10% step when it connects.
- **Health excludes charging time and charge cycles.** It compares runtime per charge with the device's own first week, and needs three weeks of history.
- **Parsed history is cached,** and later loads read only the rows appended since. The file only grows between startups.
- **Logs are capped at startup:** `battery.csv` at 90 days, `history.log` at 30 days, `error.log` at 1 MB.
- **No network access, apart from updates** if the user turns them on: one HTTPS request a day to GitHub's `/releases/latest`, reading only the address it redirects to (`UpdateCheck.cs`); and, when the user confirms an update, the new release's zip, checked against the `.sha256` file CI publishes beside it before it's installed (`UpdateInstall.cs`). The new version installs itself, run with `--finish-update`, once the old one has closed. Nothing else runs that isn't needed: idle cost is one ~15 ms check every 30 seconds, about 0.1 s of CPU a minute in all, and around 15 MB of private memory, since drawing is on the CPU and no graphics driver is loaded.

---

[← Developer docs](README.md) · Next: [Diagnostics →](diagnostics.md)
