<div align="center">

# Earshot

### A Windows 11 tray utility that keeps AirPods off the PC until you ask for them

![Windows 11](https://img.shields.io/badge/platform-Windows%2011-0078D4) &nbsp;![.NET](https://img.shields.io/badge/.NET-10.0-512BD4) &nbsp;[![build](https://github.com/5Muawiyah/earshot/actions/workflows/build.yml/badge.svg)](https://github.com/5Muawiyah/earshot/actions/workflows/build.yml) &nbsp;![Licence: MIT](https://img.shields.io/badge/licence-MIT-blue)

</div>

<p align="center"><img src="docs/images/hero.png" alt="Earshot's icon in the tray, with its card above it saying Connected" width="100%"></p>

<p align="center"><i>Earshot in the tray: one click, and a small card says what happened.</i></p>

Muawiyah Jahanzaib built Earshot because Windows pages every paired Bluetooth
device when the PC powers on, including AirPods that were left on a phone in
the middle of something, and there is no per-device setting to stop it. Earshot
disables the AirPods' own Bluetooth device nodes while they are not in use on
the PC, so Windows has nothing to page at boot; connects and disconnects them
with one click; and blocks the Hands-Free profile so a browser tab or a game
cannot drop them to call quality.

| Document | What it covers |
|---|---|
| [Overview](docs/overview.md) | what Earshot does, a tour of the tray and its menu, in plain English |
| [Architecture](docs/architecture.md) | how it works, the safety model, setup and removal, the command line |
| [Requirements](docs/requirements.md) | what Earshot has to do, the test that proves each point, and what it does not do |
| [Verification](docs/verification.md) | the verification table and the live-test evidence behind it |
| [Glossary](docs/glossary.md) | the terms, in plain English |
| [Live test guide](tools/live-tests/README.md) | how to run the live tests by hand, and how to put the machine back if one stops in the middle |
| [Third-party notices](THIRD-PARTY-NOTICES.txt) | every third-party component a release carries, its publisher, its licence and where to read the terms |

## The problem

When the PC powers on, Windows pages every paired Bluetooth device. The
AirPods accept, and they leave the phone in the middle of whatever you were
listening to. There is no per-device setting to stop this. Microsoft's answer
is that a paired device cannot be stopped from connecting automatically, other
than by unpairing it or turning Bluetooth off:
https://learn.microsoft.com/en-us/answers/questions/4093792/how-to-prevent-windows-from-automatically-connecti

Earshot takes the other route. It disables the AirPods' own Bluetooth device
nodes while you are not using them on the PC. A disabled node stays disabled
across a restart, so Windows never pages them at boot. The pairing is
untouched, so enabling the nodes again is quick, and one left click on the
tray icon does it.

## What it does

- **Stops Windows paging the AirPods at boot**, with Block at boot turned on.
- **Connects and disconnects the AirPods** with one left click on the tray icon.
- **Blocks the Hands-Free profile**, on by default, so a browser tab or a game
  cannot drop the AirPods to a narrow voice channel. This turns off the
  AirPods microphone on this PC while it is on.
- **Shows no battery figure.** There is no battery element in the tray at all.
- **Hands the AirPods back at shut down and sleep**, on by default, releasing
  them and blocking the nodes again before this PC can grab them back.
- **v1.1: keyboard shortcuts, spoken status and playing audio from a paired
  phone.** All three are built, reviewed and off by default, and none has had
  a live run yet. The hand-back above is built and covered by its own tests,
  and is in the same position on one point: it has not had a live run either.

See [docs/requirements.md](docs/requirements.md) for what each point promises
and the test that proves it, and [docs/overview.md](docs/overview.md) for a
plain-English tour of the tray.

## Getting started

1. Get `Earshot-1.1.0-win-x64.zip`. There is no download page: the zip is
   built from this repository by `tools\build-release.ps1`, which writes it
   to `artifacts\` and prints its size and SHA-256. Check that hash against
   the copy you were given before you unzip it, because setup copies these
   files into Program Files.
2. Unzip it anywhere. You get an `Earshot` folder.
3. Run `Earshot.exe`. An earbud icon appears in the notification area.
4. Right click the icon and choose **Set up Earshot...**. Windows shows one
   administrator prompt. The item only appears while setup is needed.

Connect, disconnect and the audio protection do not need setup. Only the boot
block does, because disabling a device node needs administrator rights.

Requirements: Windows 11 on x64, which is what Earshot is built for and
tested on. The v1.1 build declares no higher minimum than before (the
published `Earshot.dll` still carries `SupportedOSPlatform("Windows7.0")`).
Only Play from a phone needs Windows 10 version 2004 (build 19041) or later,
which every Windows 11 has; on anything older that one menu item is shown
disabled. Otherwise: the AirPods paired to this PC and connected to it at
least once, so Windows has created their device nodes; one administrator
approval, for setup; nothing else to install, because the release is
self-contained.

To remove Earshot, turn **Open on startup** off in the menu, close Earshot,
then run `"C:\Program Files\Earshot\Earshot.exe" uninstall` from an
administrator PowerShell or Command Prompt. See
[docs/architecture.md](docs/architecture.md#setup-and-removal) for what that
does and how it reports a step it could not finish.

## Build and testing

The .NET SDK 10 on Windows x64.

    dotnet build Earshot.slnx
    dotnet test Earshot.slnx

`tools\check.ps1` rebuilds from scratch, runs the tests, runs the live-test
self-test against a fake machine, and fails if any compiler or analyser
warning is suppressed anywhere in the solution.

`tools\build-release.ps1` publishes the self-contained win-x64 folder, checks
every published file against the manifest the build writes, zips it to
`artifacts\Earshot-<version>-win-x64.zip`, and prints the zip's size and
SHA-256. The version comes from `src\Earshot\Earshot.csproj`.

`Earshot.pdb` is published, listed in the manifest and installed on purpose:
it turns the stack trace in a logged exception into file names and line
numbers, and the log is where Earshot reports the failures it will not
swallow.

`prototype\` holds the three PowerShell scripts that blocked the AirPods
before Earshot existed. They are superseded and kept only for reference.

The live device tests under `tools\live-tests` are run by hand, on real
AirPods, and are never run by a build. See
[docs/verification.md](docs/verification.md) for what each one checks, how it
is run, and which have run so far.

## Licence

Earshot's own code is MIT licensed: see [LICENSE](LICENSE). A release also
carries Microsoft files that are not. `Microsoft.Windows.SDK.NET.dll` and
`WinRT.Runtime.dll` ship unmodified under the Microsoft Windows SDK licence
terms (https://learn.microsoft.com/en-us/legal/windows-sdk/license), which
the MIT licence does not replace, so pass a release on whole and under terms
that protect those two files at least as much as Microsoft's do.
[`THIRD-PARTY-NOTICES.txt`](THIRD-PARTY-NOTICES.txt), in the repository and
in every release, names each third-party component, its publisher, its
licence and where its terms can be read.

---

<div align="center"><sub>Built by Muawiyah Jahanzaib. MIT licensed. The live tests are run by hand on real AirPods; the verification table says which have been.</sub></div>
