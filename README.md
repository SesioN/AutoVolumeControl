# AutoVolumeControl

Tray application for Windows that keeps the volume and mute state of selected applications in sync
with the master volume of the default playback device.

- Right-click the tray icon to choose which apps are synced. Each app is shown with its icon and the title of its
  window (e.g. "shroud - Twitch" for Chrome, "Artist - Song" for Spotify), else its name; hovering shows the full
  title and the app name. For a browser it is the active tab, which is not necessarily the one playing.
- A slider per app sets its volume as a share of the master volume (100 % by default), e.g. music at 60 %
  of whatever the master is set to.
- New apps are synced by default and as soon as they start playing audio.
- Follows changes of the default playback device.
- Optional "Start with Windows".
- "App Scale" makes the whole menu (text, icons, checkboxes, sliders, spacing) 85 %, 100 %, 115 % or 130 % of
  its normal size, on top of the Windows display scaling; the choice is remembered.

## Requirements

- Windows 7 or later with .NET Framework 4.8
- To build: .NET SDK 8 or later (or Visual Studio 2022) and the .NET Framework 4.8 targeting pack

## Build

```
dotnet build AutoVolumeControl.sln -c Release
```

The single-file executable is written to `src/AutoVolumeControl/bin/Release/net48/AutoVolumeControl.exe`
(dependencies are embedded; the `.exe.config` next to it is optional).

Diagnostics are written to `%LOCALAPPDATA%\AutoVolumeControl\AutoVolumeControl.log`.

## Test

```
dotnet test AutoVolumeControl.sln
```

Most tests use fakes. `CoreAudioBackendTests` run against the real Windows audio stack: they only
change the volume of the test process's own (silent) audio session and of a silent helper process they
compile and start, and are skipped when no playback device exists. Tests that measure process-wide state
run in a non-parallel collection. Tests use temporary registry keys under `HKCU\SOFTWARE\AutoVolumeControl.Tests`
which are removed afterwards.

## Layout

```
src/AutoVolumeControl/         application
tests/AutoVolumeControl.Tests/ xUnit tests
docs/ARCHITECTURE.md           architecture and threading model
```

## License

MIT, see [LICENSE](LICENSE).
