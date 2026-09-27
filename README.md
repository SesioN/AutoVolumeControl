# AutoVolumeControl

Tray application for Windows that keeps the volume and mute state of selected applications in sync
with the master volume of the default playback device.

- Right-click the tray icon to choose which apps are synced.
- New apps are synced by default and as soon as they start playing audio.
- Follows changes of the default playback device.
- Optional "Start with Windows".

## Requirements

- Windows 7 or later with .NET Framework 4.8
- To build: .NET SDK 8 or later (or Visual Studio 2022) and the .NET Framework 4.8 targeting pack

## Build

```
dotnet build AutoVolumeControl.sln -c Release
```

The single-file executable is written to `src/AutoVolumeControl/bin/Release/net48/AutoVolumeControl.exe`.

## Test

```
dotnet test AutoVolumeControl.sln
```

Most tests use fakes. `CoreAudioBackendTests` run against the real Windows audio stack: they only
change the volume of the test process's own (silent) audio session and are skipped when no playback
device exists. Tests use temporary registry keys under `HKCU\SOFTWARE\AutoVolumeControl.Tests`
which are removed afterwards.

## Layout

```
src/AutoVolumeControl/         application
tests/AutoVolumeControl.Tests/ xUnit tests
docs/ARCHITECTURE.md           architecture and threading model
```

## License

MIT, see [LICENSE](LICENSE).
