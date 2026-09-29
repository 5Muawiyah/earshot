<div align="center">

# Earshot

### A Windows 11 tray utility that keeps AirPods off the PC until you ask for them

![Windows 11](https://img.shields.io/badge/platform-Windows%2011-0078D4) &nbsp;![.NET](https://img.shields.io/badge/.NET-10.0-512BD4) &nbsp;[![build](https://github.com/5Muawiyah/earshot/actions/workflows/build.yml/badge.svg)](https://github.com/5Muawiyah/earshot/actions/workflows/build.yml) &nbsp;![Licence: MIT](https://img.shields.io/badge/licence-MIT-blue)

</div>

<p align="center"><img src="docs/images/hero.png" alt="Earshot's icon in the tray, with its card above it saying Connected" width="100%"></p>

<p align="center"><i>Earshot in the tray: click the icon, then Connect on the card, and it says what happened.</i></p>

Muawiyah Jahanzaib built Earshot because Windows pages every paired Bluetooth
device when the PC powers on, including AirPods that were left on a phone in
the middle of something, and there is no per-device setting to stop it. Earshot
disables the AirPods' own Bluetooth device nodes while they are not in use on
the PC, so Windows has nothing to page at boot; connects and disconnects them
from a click on the tray icon's card; and blocks the Hands-Free profile so a
browser tab or a game cannot drop them to call quality.

| Document | What it covers |
|---|---|
| [Overview](docs/overview.md) | what Earshot does, a tour of the tray, its menu and the AirPods widget, in plain English |
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
untouched, so enabling the nodes again is quick: left-click the Earshot icon,
then Connect on the card.

## What it does

- **Stops Windows paging the AirPods at boot**, with Block at boot turned on.
- **Connects and disconnects the AirPods**: left-click the Earshot icon, then
  Connect or Disconnect on the card.
- **Blocks the Hands-Free profile**, on by default, so a browser tab or a game
  cannot drop the AirPods to a narrow voice channel. This turns off the
  AirPods microphone on this PC while it is on.
- **Hands the AirPods back at shut down, sleep and Exit.** With **Hand back
  on shut down, sleep and Exit** ticked (in the menu and on the card's
  settings page), Earshot lets the AirPods go, confirms it, and blocks the
  device nodes again before this PC can grab them back. A stuck disconnect
  still gets the block. The tests cover it against stand-ins; it has not had a
  live run (tests 17, 18 and 20 are pending).
- **The AirPods widget.** A gauge on the taskbar (the earbud mark with a ring
  in your Windows accent colour, and the lower proved bud's number), a card
  with a settings page behind its gear, and a low battery alert. It listens to
  the AirPods' own Bluetooth broadcast, so it needs no connection. A battery
  figure shows only once **Set up battery** has proved that field: open the
  case by the PC, tell Earshot what your iPhone shows, and do it twice. Until
  then the gauge shows the earbud mark alone. Set-up cannot prove whether a bud
  is in the ear or whether the case lid is open, so ear detection, auto-pause
  and the case-open card stay off. The widget has not had a live run. See
  [docs/overview.md](docs/overview.md#the-airpods-widget) and
  [docs/architecture.md](docs/architecture.md#the-airpods-widget).
- **Shortcuts, on by default.** Ctrl+Alt+Shift+A connects (switches the
  AirPods to this PC) and Ctrl+Alt+Shift+D disconnects (switches them to the
  phone). Both can be changed or cleared on the settings page. Nobody has
  pressed one on a real run yet (test 16 is pending).
- **Pause when the AirPods leave this PC**, on by default. If this PC was
  playing to the AirPods when they leave, Earshot pauses the one media session
  that is playing. It never resumes anything.
- **Updates.** **Check for updates** is in the menu and on the settings page.
  **Check automatically** is off by default, because a check contacts GitHub.
  Nothing downloads until you press Update. See
  [docs/architecture.md](docs/architecture.md#updates) for what the checksum
  does and does not protect against.
- **Spoken status and playing audio from a phone**, both off by default and
  neither with a live run.
- **A small background service for the hand-back.** When the Earshot icon has
  been closed or has crashed, nothing in the tray can hand the AirPods back at
  shut down. A Windows service, `EarshotHandBack`, covers that case: at shut
  down it checks whether the AirPods are already blocked and, if they are not,
  blocks them. It does not disconnect them. It does nothing else, takes no
  requests from any program, and reads its settings only from a folder that
  standard users cannot write to. Setup installs it and uninstall removes it.
  It does not cover a shut down with Fast Startup on, which Windows may
  finish without telling services, and whether a restart gives it the
  shut-down notice is not proved. It has not had a live run yet.

See [docs/requirements.md](docs/requirements.md) for what each point promises
and the test that proves it, and [docs/overview.md](docs/overview.md) for a
plain-English tour of the tray.

## Getting started

1. Get `Earshot-<version>-win-x64.zip`. The zip is built from this repository
   by `tools\build-release.ps1`, which writes it to `artifacts\` with a
   `.sha256` file beside it and prints its size and SHA-256. Check that hash
   against the copy you were given before you unzip it, because setup copies
   these files into Program Files.
2. Unzip it anywhere. You get an `Earshot` folder.
3. Run `Earshot.exe`. An earbud icon appears in the notification area.
4. Right click the icon and choose **Set up Earshot...**. Windows shows one
   administrator prompt. The item appears before setup, for a damaged
   install, and when the running copy is newer than the installed one.

Connect, disconnect and the audio protection do not need setup. Only the boot
block does, because disabling a device node needs administrator rights.

Requirements: Windows 11 on x64, which is what Earshot is built for and
tested on. Only Play from a phone needs Windows 10 version 2004 (build 19041) or later,
which every Windows 11 has; on anything older that one menu item is shown
disabled. Otherwise: the AirPods paired to this PC and connected to it at
least once, so Windows has created their device nodes; one administrator
approval, for setup; nothing else to install, because the release is
self-contained.

Settings are in `%APPDATA%\Earshot\settings.json`. Logs, live-test evidence
and the widget's claim, set-up records and proof are in
`%LOCALAPPDATA%\Earshot`. The files the elevated tasks and the service read
are in `%ProgramData%\Earshot`.

To remove Earshot, turn **Open on startup** off in the menu, close Earshot,
then run `& "C:\Program Files\Earshot\Earshot.exe" uninstall` from an
administrator PowerShell (in Command Prompt, leave out the `&`). See
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
