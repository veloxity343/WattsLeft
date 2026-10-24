# Changelog

Notable changes to WattsLeft (called ControllerHP before 0.4.0), newest first. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [semantic versioning](https://semver.org/).

<!--
Add each change under Unreleased as it's made. When releasing, rename Unreleased to the version and date,
for example "## [0.4.0] - 2026-11-02", and start a new empty Unreleased section above it. Each release
uses its version's section as its release notes, and the release build stops if a version has none.
Pre-releases (tags like v0.4.0-beta.1) use the Unreleased section.
-->

## [Unreleased]

### Fixed

- Logitech devices show up as soon as they connect to their receiver, after plugging the receiver back in or switching the device on, instead of up to 30 seconds later. They also leave the tray straight away when unplugged or switched off.

## [0.6.0] - 2026-10-24

### Added

- **Open folder** in Settings, under **Your data**, shows where WattsLeft keeps your battery history and logs.

### Removed

- Moving settings and history over from ControllerHP, WattsLeft's name before 0.4.0, on the first start.

## [0.5.0] - 2026-10-24

### Added

- **Updates:** WattsLeft can tell you when there's a new version, and update itself. It asks once, on its first start, whether to check, and **Check for updates** in Settings changes it. While it's on, it asks GitHub once a day which release is the latest. A newer one gets a pop-up, an **Update to WattsLeft x.y.z** item in the right-click menu, and **Update now** in Settings. After asking you, WattsLeft downloads it, checks it against the release's checksum, and restarts as the new version, keeping your settings, history and tray icon's place. The check sends nothing about you or your devices, and while it's off, WattsLeft makes no connections at all. Updating from the app works from this version on.

## [0.4.1] - 2026-10-24

### Fixed

- The 0.4.0 release had no download attached: the release build lost the zip before attaching it. 0.4.1 is the same app, with its zip.

## [0.4.0] - 2026-10-24

### Added

- **Bluetooth mice, keyboards and headsets:** any Bluetooth device Windows shows a battery for, in Settings → Bluetooth & devices, now shows in WattsLeft too, over Bluetooth Low Energy or classic Bluetooth. Each is drawn as what it is, worked out from what it says it is or the parts Windows installed for it. Devices on a USB receiver aren't supported yet.
- **Logitech mice, keyboards and headsets** on a Unifying, Bolt or Lightspeed receiver, or on their cable, read through Logitech's own protocol: battery, name and type, alongside G HUB, and read-only. The panel shows them as connected by **USB receiver**.
- **Devices on their makers' own receivers,** read through each maker's protocol, read-only and alongside the maker's software: Razer mice, keyboards and the BlackShark V2 Pro (2020); SteelSeries headsets and mice; Corsair, HyperX, Sony INZONE, Astro, Audeze, JBL, Lenovo, MCHOSE and Logitech headsets; ASUS, Keychron, Pulsar, ATK, VXE, Hitscan, WLmouse, LAMZU, G-Wolves, MCHOSE, NZXT and Angry Miao mice; Lofree keyboards; and Nintendo Switch controllers over Bluetooth, the Steam Controller's puck and 8BitDo controllers in their own mode. Untested on real hardware so far.
- **Show** setting: choose which kinds of device WattsLeft watches (controllers, mice, keyboards, headsets and others). Kinds turned off get no icon, pop-ups or history. All are on by default.
- **Pop-ups during fullscreen games:** in exclusive fullscreen, where pop-ups can't appear, they're now held and shown once the game closes, instead of being lost. A new **Hold while a game is fullscreen** setting does the same for borderless games. At most one shows per device, and a held battery warning is dropped if the device was charged meanwhile.
- **Fully charged** pop-up when a charging device reaches 100%, once per charge. It can be turned off in Settings.
- **Time left** in the tray tooltip and the device panel, such as "about 3 h to 20%" or "1 h 30 min to full", once there's enough history. It's the same figure as the Devices page shows.
- **Warning sounds:** a new **Play a sound** setting plays a short chime with each battery warning, two notes for low and three, ending lower, for critical, even while a fullscreen game holds the pop-up. Off by default.
- **Leave a device out of the chart** by clicking its name in the chart's legend, and click it again to bring it back. Its history and stats are kept.
- **A device's own warning levels:** each device's card on the Devices page has a switch and low and critical levels, for that device alone, which its pop-ups, time left and stats use. Others keep following Settings.
- **Rename a device** from its card on the Devices page, to tell two of the same model apart. The name shows everywhere WattsLeft shows it, and leaving it empty restores the device's own.
- **Share battery levels** setting: keeps `%LOCALAPPDATA%\WattsLeft\status.json` up to date with every connected device's battery, for other apps such as Rainmeter skins, Stream Deck plugins or scripts to read. Off by default.
- **Diagnostics** in the right-click menu writes a report of what WattsLeft sees and opens it, for bug reports, instead of running `--list` from a terminal. Bluetooth addresses in it are partly masked, so it can be posted as it is.

### Changed

- **ControllerHP is now WattsLeft,** as it shows more than controllers. Settings, battery history and Start with Windows move over by themselves on its first start. Because the program has a new name, Windows treats its tray icon as new: drag it back beside the clock if you'd pinned it there. Other apps reading the battery levels should read `%LOCALAPPDATA%\WattsLeft\status.json` now, whose `app` field says `WattsLeft`. After updating, the old `%LOCALAPPDATA%\Programs\ControllerHP` folder can be deleted.
- **Controllers are now devices** throughout the app and the wiki, as other devices show too: the Controllers page is the **Devices** page, and the wiki's Controllers and Supported controllers pages are now **Devices** and **Supported devices**.
- **The tray icon shows the device itself,** with a thin battery bar along the bottom, instead of a number above a battery, so it's clear at a glance what it's showing. It's drawn pixel by pixel at 16 px, so it stays sharp at 100% scaling. Hover over it for the exact percentage. The panel and pop-ups use it too.
- **Removing a connected device takes it out of the tray** too, along with the panel, the menu and its pop-ups, until it next connects. Before, it stayed in the tray while it was on. Its history keeps recording meanwhile.
- Stacked pop-ups have a little more room between them.
- **The settings window opens larger and in the middle of the screen,** instead of growing off to the right. Nothing in it is cut off at any size it can be made: text wraps, a device's figures drop to two or one columns, the chart's legend wraps onto more rows, and Restore removed moves under the device list when there isn't room beside it. Dialog titles with a long device name wrap too.
- **One download that needs nothing installed.** WattsLeft is now built on Avalonia and compiled to native code, with .NET built in, so there's no runtime to install and no separate standalone zip. Extract the whole zip: the exe needs the DLLs beside it.
- **Built on .NET 10,** supported until November 2028, instead of .NET 8, whose support ends in November 2026.
- WattsLeft no longer ships Windows' 25 MB device and Bluetooth library: it calls Windows directly.
- The supported devices tables say which devices and connections have been **tested** on real hardware, which are **likely** to work, and which are still **untested**.

### Fixed

- A device no longer shows as disconnected and then connected again, with a pop-up for each, when Windows briefly fails to list or read it.
- An Xbox controller or other Bluetooth device no longer shows as charging when its battery ticks up a point while resting. It now takes a rise of 2 points.
- The Undo bar on the Devices page closes when the removed device is brought back another way, such as with Restore removed.

## [0.3.0] - 2026-10-21

### Added

- **Controller panel:** left-click the tray icon to see every connected controller, with its charge and connection.
- **Settings window:** tray icons, pop-up position, connection pop-ups, battery warnings (each with its own on/off and level) and Start with Windows. Open it from the panel's gear or **Settings…** in the right-click menu.
- **Controllers page:** a chart of each controller's battery history (2 hours to 7 days), with time until low or full, average drain, play time, charge cycles and a battery health estimate. Controllers you no longer use can be removed, and restored any time.
- **Light and dark mode:** the right-click menu, panel and settings window follow Windows, with Windows 11's rounded corners.
- **New app icon,** also shown in the tray when no controllers are connected.
- **Battery warnings** toggle in the right-click menu, to turn both warnings on or off at once.
- A wiki covering getting started, every setting, the Controllers page, supported controllers and troubleshooting.

### Changed

- **Tray icons** now offers 1, 2 or one per controller.
- `history.log` is kept for 30 days and `error.log` to its newest 1 MB, instead of growing forever.
- Reconnecting a controller causes fewer background refreshes.

## [0.2.0] - 2026-10-21

### Added

- Support for **DualSense, DualSense Edge and DualShock 4** controllers over USB and Bluetooth.
- **One tray icon for several controllers,** showing the one with the lowest battery, with the rest listed when you hover. A **Tray icons** option shows more.
- **Pop-up position** and **Connection pop-ups** options in the right-click menu.

### Changed

- Pop-ups are slimmer and appear in the top-right corner by default, clear of Steam's notifications.

### Fixed

- A Bluetooth Xbox controller no longer disappears from the tray when a DualSense disconnects.

### Security

- Added a security policy for reporting vulnerabilities privately.

## [0.1.1] - 2026-10-21

### Added

- An application icon.

## [0.1.0] - 2026-10-21

### Added

- A battery meter in the notification area for Xbox controllers over USB, Bluetooth and the Xbox Wireless Adapter: the percentage, a meter that turns from green to red, and a bolt while charging.
- Pop-ups when a controller connects or disconnects, and when its battery reaches 20% and 10%.
- **Start with Windows**.

[Unreleased]: https://github.com/veloxity343/WattsLeft/compare/v0.6.0...HEAD
[0.6.0]: https://github.com/veloxity343/WattsLeft/compare/v0.5.0...v0.6.0
[0.5.0]: https://github.com/veloxity343/WattsLeft/compare/v0.4.1...v0.5.0
[0.4.1]: https://github.com/veloxity343/WattsLeft/compare/v0.4.0...v0.4.1
[0.4.0]: https://github.com/veloxity343/WattsLeft/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/veloxity343/WattsLeft/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/veloxity343/WattsLeft/compare/v0.1.1...v0.2.0
[0.1.1]: https://github.com/veloxity343/WattsLeft/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/veloxity343/WattsLeft/releases/tag/v0.1.0
