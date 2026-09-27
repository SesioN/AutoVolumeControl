# AutoVolumeControl

[![Build](https://github.com/SesioN/AutoVolumeControl/actions/workflows/build.yml/badge.svg)](https://github.com/SesioN/AutoVolumeControl/actions/workflows/build.yml)
[![Latest release](https://img.shields.io/github/v/release/SesioN/AutoVolumeControl)](https://github.com/SesioN/AutoVolumeControl/releases/latest)
[![License: MIT](https://img.shields.io/github/license/SesioN/AutoVolumeControl)](LICENSE)

A small Windows tray application that keeps the volume and mute state of selected applications in sync with the
master volume of the default playback device.

## Why?

Some audio devices ignore the Windows master volume on certain outputs. The classic example is the **digital (optical /
S/PDIF) output of Creative Sound Blaster devices** such as the Sound BlasterX G6: the volume bar in Windows, the volume
keys on the keyboard and even the volume knob on the device do nothing, and the signal always leaves the device at
full level. The only volume that still has an effect there is the **per-app volume** in the Windows volume mixer.

AutoVolumeControl bridges that gap. It watches the master volume of the default playback device and applies it to the
volume of every running app. The master volume bar, your keyboard's volume keys and the knob on the device work
again, and mute works too.

It is also useful without such a device, e.g. to keep apps that reset their own volume (browsers, games, music
players) tied to the master volume, or to let one app always play at a fixed share of it.

## Table of contents

- [Why?](#why)
- [Features](#features)
- [Installation](#installation)
- [Usage](#usage)
- [Settings and files](#settings-and-files)
- [Uninstalling](#uninstalling)
- [Building from source](#building-from-source)
- [Running the tests](#running-the-tests)
- [Continuous integration and releases](#continuous-integration-and-releases)
- [Project structure](#project-structure)
- [Contributing](#contributing)
- [License](#license)

## Features

- **Per-app sync.** You choose in the tray menu which apps follow the master volume and mute state.
- **Per-app ratio.** A slider per app sets its volume as a share of the master volume (100 % by default). For
  example, music can play at 60 % of whatever the master volume is.
- **Recognizable entries.** Each app is shown with its icon and the title of its window (e.g. "shroud - Twitch" for
  Chrome, "Artist - Song" for Spotify), otherwise its name. Hovering shows the full title and the app name. For a
  browser this is the active tab, which is not necessarily the one playing.
- **New apps are picked up automatically.** They are synced by default as soon as they start playing audio.
- **Follows the default device.** Switching to other speakers or a headset just works.
- **No fighting.** An app that keeps resetting its own volume is throttled instead of being corrected in a loop.
- **Start with Windows** (optional).
- **App Scale.** The whole menu can be shown at 85 %, 100 %, 115 % or 130 % of its normal size, on top of the Windows
  display scaling.
- **Single portable executable.** No installer and no admin rights needed.

## Installation

Requirements: Windows 10 or 11 with [.NET Framework 4.8](https://dotnet.microsoft.com/download/dotnet-framework/net48)
(included since Windows 10 version 1903). Windows 7 and 8.1 with .NET Framework 4.8 should work but are not tested.

1. Download `AutoVolumeControl.exe` from the [latest release](https://github.com/SesioN/AutoVolumeControl/releases/latest).
2. Put it in a permanent location, e.g. `%LOCALAPPDATA%\Programs\AutoVolumeControl\`.
3. Start it. The app runs in the notification area (system tray).

Only download AutoVolumeControl from the [releases page](https://github.com/SesioN/AutoVolumeControl/releases). Every
release is built from the tagged source by [GitHub Actions](.github/workflows/build.yml), and its notes list the
SHA-256 checksum of the executable, so you can check your download with `Get-FileHash AutoVolumeControl.exe` in
PowerShell.

The executable is not code-signed, so Windows SmartScreen may show a warning on the first start. Choose
**More info → Run anyway** if you trust the download, or build it yourself from source. For the same reason a few
antivirus engines may flag it as a false positive.

## Usage

Right-click the tray icon to open the menu:

| Entry | What it does |
|---|---|
| **App Scale** | Size of the menu: 85 %, 100 %, 115 % or 130 %. |
| App rows | Checkbox: sync this app on/off. Slider: its volume as a share of the master volume (mouse wheel moves it by 5 %). |
| **Start with Windows** | Starts the app when you sign in. |
| **Exit** | Closes the app. |

Only one instance runs at a time; starting it again has no effect.

## Settings and files

AutoVolumeControl stores everything per user, never in the program folder:

| What | Where |
|---|---|
| Settings (synced apps, ratios, menu size) | Registry: `HKCU\SOFTWARE\AutoVolumeControl` |
| "Start with Windows" | Registry: `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run` |
| Log file (rotated at 1 MB) | `%LOCALAPPDATA%\AutoVolumeControl\AutoVolumeControl.log` |

If something does not work as expected, the log file is the first place to look and is helpful to attach to an issue.

## Uninstalling

1. Turn off **Start with Windows** in the menu, then choose **Exit**.
2. Delete `AutoVolumeControl.exe`.
3. Optionally remove the settings and the log: delete the registry key `HKCU\SOFTWARE\AutoVolumeControl` and the
   folder `%LOCALAPPDATA%\AutoVolumeControl`.

## Building from source

Prerequisites:

- [.NET SDK 8](https://dotnet.microsoft.com/download) or later (or Visual Studio 2022)
- .NET Framework 4.8 targeting pack (included with the Visual Studio ".NET desktop development" workload)

```
git clone https://github.com/SesioN/AutoVolumeControl.git
cd AutoVolumeControl
dotnet build AutoVolumeControl.sln -c Release
```

The single-file executable is written to `src/AutoVolumeControl/bin/Release/net48/AutoVolumeControl.exe`.
All dependencies are embedded with [Costura.Fody](https://github.com/Fody/Costura). The `.exe.config` next to it is
optional.

## Running the tests

```
dotnet test AutoVolumeControl.sln
```

The tests use [xUnit](https://xunit.net/). Most of them use fakes. `CoreAudioBackendTests` run against the real Windows
audio stack: they only change the volume of the test process's own (silent) audio session and of a silent helper
process they compile and start, and they are skipped when no playback device exists (e.g. on CI runners). Tests that
measure process-wide state run in a non-parallel collection. Tests use temporary registry keys under
`HKCU\SOFTWARE\AutoVolumeControl.Tests`, which are removed afterwards.

## Continuous integration and releases

[GitHub Actions](.github/workflows/build.yml) builds and tests every push to `master` and every pull request on a
Windows runner, and keeps the executable as a build artifact.

To publish a release:

1. Bump `AssemblyVersion` and `AssemblyFileVersion` in
   [`src/AutoVolumeControl/Properties/AssemblyInfo.cs`](src/AutoVolumeControl/Properties/AssemblyInfo.cs) and commit
   it to `master`.
2. Either push a tag of the form `vMAJOR.MINOR.PATCH` (e.g. `git tag v1.7.0 && git push origin v1.7.0`), or start the
   **Build** workflow manually under **Actions** and enter the tag in **release_tag**.
3. The workflow builds and tests that commit and creates a GitHub release with `AutoVolumeControl.exe` and generated
   release notes.

Versions follow [Semantic Versioning](https://semver.org/).

## Project structure

```
src/AutoVolumeControl/          the application
tests/AutoVolumeControl.Tests/  xUnit tests
docs/ARCHITECTURE.md            architecture, components and threading model
.github/workflows/build.yml     CI build, tests and releases
```

New to the code? Start with [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Contributing

Bug reports, ideas and pull requests are welcome.

- **Bugs:** open an [issue](https://github.com/SesioN/AutoVolumeControl/issues) with your Windows version, the steps
  to reproduce and, if possible, the log file (see [Settings and files](#settings-and-files)).
- **Pull requests:** create a branch, keep the change focused, add or update tests, and make sure
  `dotnet build` and `dotnet test` pass. The CI build must be green before merging.
- Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/) (`feat:`, `fix:`, `chore:`, …).

## License

AutoVolumeControl is licensed under the [MIT License](LICENSE).
