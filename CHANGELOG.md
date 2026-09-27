# Changelog

All notable changes to AutoVolumeControl are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows
[Semantic Versioning](https://semver.org/). Versions up to 1.4 were published outside GitHub; 1.7.0 is the first
release on [GitHub Releases](https://github.com/SesioN/AutoVolumeControl/releases).

## [1.7.0] - 2026-09-27

### Added
- **App Scale:** the whole tray menu (text, icons, checkboxes, sliders, spacing) can be shown at 85 %, 100 %, 115 % or
  130 % of its normal size, on top of the Windows display scaling. The choice is remembered.
- Each app row shows the title of the app's window (e.g. "shroud - Twitch" for Chrome, "Artist - Song" for Spotify)
  instead of its process name; hovering shows the full title and the app name.
- Builds, tests and releases with GitHub Actions; every release lists the SHA-256 checksum of the executable.

### Changed
- The menu uses its own Material style controls, which scale cleanly and follow the display DPI. The MaterialSkin.2
  dependency is removed.
- Larger default menu size (the new 100 % equals the former 130 %).
- App rows span the whole menu with the slider aligned right.
- Rewritten README with the use case, installation, usage, settings and uninstall instructions.

### Fixed
- The menu no longer grows wider with every rescale, and the last app row no longer covers the separator.

## [1.6.0] - 2026-09-27

### Added
- **App icons** in the menu.
- **Per-app volume ratio:** a 0–100 % slider per app sets its volume as a share of the master volume (mouse wheel
  moves it by 5 %). The default of 100 % keeps the previous behavior.
- Clicking an app's icon or name toggles it.
- Event-driven session tracking: apps that close, apps that change their own volume, removed devices and restarts of
  the Windows audio service are detected immediately.
- **Automatic recovery** when the audio service is not ready at logon or restarts, without restarting the app.
- Apps that keep resetting their own volume are throttled instead of being corrected in an endless loop.
- Only one instance runs at a time.
- Diagnostics log in `%LOCALAPPDATA%\AutoVolumeControl` (rotated at 1 MB); unhandled errors are logged instead of
  showing an error dialog.

### Changed
- Replaced the unmaintained CSCore library with a small, own Core Audio interop layer. This removes a crash that could
  occur after a restart of the audio service.
- Volume and mute are only written where they differ from the target, and the app's own writes no longer trigger
  feedback loops.

### Fixed
- Blurry, oversized menu on high-DPI displays, also when started from a launcher with DPI compatibility overrides.
- Blurry tray icon.
- Black app rows in the menu.

## [1.5.0] - 2026-09-27

### Added
- xUnit test suite and architecture documentation.

### Changed
- The project builds from a clean clone (SDK-style project, NuGet `PackageReference`). The output is still a single
  executable.
- All audio work runs on one dedicated thread.

### Fixed
- The menu leaked window handles every time it was opened.
- A stale volume could win when the volume changed quickly.
- Changes of the default playback device were ignored.
- For some apps the checkbox had no effect.
- Invalid values in the registry crashed the menu.
- "Start with Windows" stored the path unquoted and ignored where the app was located.

## [1.4.0] - 2025-03-24

### Changed
- New Material design for the tray menu.
- More padding in the app list for better readability.

## [1.3.0.2] - 2025-02-27

### Fixed
- The menu no longer flickers while it is open and the volume keeps changing; it is only redrawn when the app list
  changed.
- The menu is updated when it opens again (regression from 1.3.0.1).

## [1.3.0.1]

### Fixed
- The app list is updated when an event such as a volume change occurs.

## [1.3.0] - 2024-09-26

### Changed
- Uses the CSCore Core Audio API for all interaction with the audio devices, for better reliability.

### Fixed
- "Start with Windows" did not work on some systems.
- Some audio clients were not detected or could not be controlled.

## [1.2]

### Added
- "Start with Windows" option.
- The menu shows the app name and version.

## [1.1]

### Added
- Choose in the tray menu which apps are affected. Apps that AutoVolumeControl has never seen before are enabled by
  default, and the selection is stored per Windows user.

## [1.0] - 2020-07-07

- Initial version: keeps the volume of all running apps in sync with the master volume of the default playback
  device.

[1.7.0]: https://github.com/SesioN/AutoVolumeControl/releases/tag/v1.7.0
