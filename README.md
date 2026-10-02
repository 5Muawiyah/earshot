<div align="center">

# Earshot

### A Windows 11 tray utility that keeps AirPods off the PC until you ask for them

![Windows 11](https://img.shields.io/badge/platform-Windows%2011-0078D4) &nbsp;![.NET](https://img.shields.io/badge/.NET-10.0-512BD4) &nbsp;[![build](https://github.com/5Muawiyah/earshot/actions/workflows/build.yml/badge.svg)](https://github.com/5Muawiyah/earshot/actions/workflows/build.yml) &nbsp;![Licence: MIT](https://img.shields.io/badge/licence-MIT-blue)

</div>

<p align="center"><img src="docs/images/hero.png" alt="An earlier version of Earshot's icon in the tray, with its card above it saying Connected" width="100%"></p>

<p align="center"><i>Earshot in the tray: click the icon, then Connect on the card, and it says what happened.</i></p>

I built Earshot because Windows pages every paired Bluetooth device when the PC
powers on, including AirPods that were left on my phone in the middle of
something, and there is no per-device setting to stop it. Earshot disables the
AirPods' own Bluetooth device nodes while they are not in use on the PC, so
Windows has nothing to page at boot. It connects and disconnects them from a
click on the tray icon's card. It blocks the Hands-Free profile so a browser
tab or a game cannot drop them to call quality. Since v1.3.0 it also shows
their battery, and in v1.4.0 it keeps their last reading when they are away.

| Document | What it covers |
|---|---|
| [Overview](docs/overview.md) | what Earshot does, a tour of the tray, its menu and the AirPods widget, in plain English |
| [Architecture](docs/architecture.md) | how it works, the safety model, setup and removal, the command line |
| [Requirements](docs/requirements.md) | what Earshot has to do, the test that proves each point, and what it does not do |
| [Verification](docs/verification.md) | the verification table and the live-test evidence behind it |
| [Glossary](docs/glossary.md) | the terms, in plain English |
| [Live test guide](tools/live-tests/README.md) | how to run the live tests by hand, and how to put the machine back if one stops in the middle |
| [Third-party notices](THIRD-PARTY-NOTICES.txt) | every third-party component a release carries, its publisher, its licence and where to read the terms |

## Install

In a normal (not administrator) PowerShell window:

```
irm https://github.com/5Muawiyah/earshot/releases/latest/download/earshot.ps1 | iex
```

It offers install, update, repair and uninstall, and works the same way for
each. It downloads the latest release, checks the zip's SHA-256, and shows one
administrator prompt. After an install, update or repair it starts Earshot; after
an uninstall it does not. The details are in
[docs/architecture.md](docs/architecture.md#the-install-script).

The script and the zip come from the same release, so the checksum catches a
damaged or cut-short download. It does not catch a compromised release or
account, and the app is not signed.

### Install with AI

Paste this into any AI chat that can run commands on your PC:

```
Install Earshot on this Windows PC (or update, repair or uninstall it, as I ask).
Run exactly this in PowerShell and nothing else, with my choice as the action:
& ([scriptblock]::Create((irm https://github.com/5Muawiyah/earshot/releases/latest/download/earshot.ps1))) -Action Install
Tell me what it printed. If Windows asks for administrator approval, tell me to click Yes.
```

### By hand

Download `Earshot-<version>-win-x64.zip` and its `.sha256` from the latest
release, check the hash, unzip, run `Earshot.exe`, then choose **Set up
Earshot...** from the menu. Windows shows one administrator prompt. Once
Earshot is installed the menu says **Repair Earshot...** in its place.

Requirements: Windows 11 on x64, and AirPods paired to this PC and connected to
it at least once, so Windows has created their device nodes. Nothing else needs
installing, because the release is self-contained.

## The problem

When the PC powers on, Windows pages every paired Bluetooth device. The AirPods
accept, and they leave the phone in the middle of whatever you were listening
to. Microsoft's answer is that a paired device cannot be stopped from
connecting automatically, other than by unpairing it or turning Bluetooth off:
https://learn.microsoft.com/en-us/answers/questions/4093792/how-to-prevent-windows-from-automatically-connecti

Earshot takes the other route. A disabled node is designed to stay disabled
across a restart. The pairing is untouched, so enabling the nodes again is
quick: left-click the Earshot icon, then Connect on the card. The power cycle
test that checks this (test 08) has not run yet, and a shut down with Fast
Startup on is not covered.

## What it does

- **Stops Windows paging the AirPods at boot**, with Block at boot turned on.
- **Connects and disconnects them** from the card.
- **Blocks the Hands-Free profile**, on by default. This turns off the AirPods
  microphone on this PC while it is on.
- **Microphone off mode**, an opt-in alternative, off by default. The switch
  only turns audio protection off. Its Open button opens Windows' sound settings
  at the AirPods' microphone when Earshot has a well-formed id for that capture
  endpoint, and the list of sound devices when it has not, with one line on what
  to press. Call quality with it on is unproved until I have tried it.
- **Shows the battery** of the left bud, the right bud and the case: live while
  it hears them, then greyed with its age, and kept across a restart. A part that
  was charging is shown rising as an estimate, marked `≈`. Earshot listens to the
  AirPods' own Bluetooth broadcast, and you link your pair by opening the case
  next to the PC. See [Battery](#battery) for what that does and does not prove.
- **Opens a card when your case opens near the PC**, with the three levels and
  Connect, and closes it when the case closes. It never takes focus.
- **Hands the AirPods back at shut down, sleep and Exit**, on for a new
  install. Earshot lets them go, confirms it, and blocks the device nodes again
  before this PC can grab them back. A stuck disconnect still gets the block.
  The tests cover it against stand-ins; it has not had a live run (tests 17, 18
  and 20 are pending).
- **Shortcuts, on by default.** Ctrl+Alt+Shift+A connects, Ctrl+Alt+Shift+D
  disconnects and Ctrl+Alt+Shift+E opens or closes the card. All can be changed
  or cleared on the settings page. Nobody has
  pressed one on a real run yet (test 16 is pending).
- **Pauses when the AirPods leave this PC**, on by default, if this PC was
  playing to them. It pauses the one media session that is playing and never
  resumes anything.
- **Spoken status and playing audio from a phone**, both off by default and
  neither with a live run.
- **Updates and repair.** **Check for updates** is in the menu and on the
  settings page. **Check automatically** is off by default, because a check
  contacts GitHub, and nothing downloads until you press Update. **Repair
  Earshot...** checks every installed file against the list the release
  published and fixes what it finds. Your settings and chosen AirPods are kept.
  After an update, Earshot starts again on its own.
  [docs/architecture.md](docs/architecture.md#updates) has each route.
- **A small background service for the hand-back.** If the Earshot icon has
  been closed or has crashed, `EarshotHandBack` blocks the AirPods at shut down
  when they are not already blocked. It acts only when Block at boot and Hand
  back are both on, never disconnects the AirPods, and takes no requests from
  any program. It does not cover a shut down with Fast Startup on, and whether
  a restart gives it the shut-down notice is not proved. It has not had a live
  run.

## Battery

The AirPods broadcast their battery over Bluetooth Low Energy, and Earshot
reads it with no set-up beyond opening the case. The card shows left, right and
case together, and the taskbar gauge shows a ring round the earbud mark with the
lower bud's number while the AirPods are connected to this PC, and the case's
last or estimated number beside a case mark, in grey, while they are not. There is a
**Refresh** control on the card and in the menu. It listens for up to 12 seconds
and ends on values or on "Open the case".
**Gauge order** on the settings page changes how the gauge lines up its ring,
number and charging bolt. **Display** puts the gauge on one display's taskbar,
or on every display's taskbar at once with **All displays**. The rarer settings
are under **More**, and Repair is on the Updates page.

What to know before you trust a number:

- **Which AirPods.** You link them by opening the case next to the PC. A pair in
  an open case sends its case level and a pair in use does not, so Earshot links
  the set of your model that sends one, close to the PC (-70 dBm or stronger,
  steadily for about two seconds). The first set to do that is linked, and a pair
  that opens its case later but 8 dB stronger takes the link. A pair worn nearby is
  never linked. Earshot then follows your pair when its addresses change
  (only to a set that says the levels yours last said, within 30 seconds), and
  drops the link after two minutes without hearing it, showing the last readings,
  greyed, until you open the case again. The link is kept in memory only, so a
  restart, which an update does, needs the case opened once more for live
  figures. With the AirPods connected and
  nothing linked, the card says "Open the case to show battery".
- **The risk that remains.** A pair of the same model that opens its case next to
  the PC more strongly than yours can be linked instead, and one whose levels
  equal yours, heard within 30 seconds of yours going quiet, can be followed as if
  it were yours. Once your pair has been unheard for more than 10 seconds (the
  case shut, or the buds out of range), any pair of the same model that opens its
  case at -70 dBm or stronger is linked at once, with no 8 dB margin to clear.
  I accept that risk, and you should know it is there. Saved readings, estimates
  and the case-open card all follow that link, so a pair that takes it is shown
  as mine. Opening your own case at least 8 dB stronger takes the link back.
- **Last readings.** The last reading of each bud and the case of your linked
  pair is shown, greyed with its age, wherever the AirPods are and however old
  it is, until a newer one is heard. It is kept across a restart in
  `last-reading.json` in Earshot's local folder: the level, the charging flag, the
  time and the model, no address and no name. It also notes which parts have had
  their fully charged notice, so a restart does not repeat it. Windows' own figure
  is shown only while the AirPods are connected to this PC.
- **The case shows only after I have seen it once.** Until my case has been heard,
  the gauge has no case to show, and there is no case estimate until one case
  charge has been seen.
- **History.** The live readings of your linked pair are kept for seven days in
  `battery-history.json` beside it: the part, the level, the charging flag and the
  time. Nothing else, no address and no name.
- **Age.** A value older than 30 seconds is greyed, with how long ago it was read.
- **Estimates are estimates.** A part that was charging when last read is shown
  rising, marked `≈` with the age of the reading it grew from, at a rate learned
  from your own pair's charging, and only once one has been learned. It stops at
  100, never falls, and a newer reading replaces it, even a lower one. It is not
  a reading.
- **Left and right.** Which bud is left and which right rests partly on a
  published description of the broadcast and partly on one local capture. It is
  unproved.
- **Windows' own figure.** When Windows has a Hands-Free battery reading for the
  AirPods, Earshot reads it too, from the AirPods' device nodes only. With
  Hands-Free off, the default, it was seen empty on this PC.
- **Ear detection is built but inactive.** Pausing when a bud comes out, and
  resuming when it goes back, needs a documented in-ear value, and there is none.
  It does nothing today.
- **Not yet live-tested.** The battery, the estimates and the case-open card have
  not had a live run on a real PC (test 19 is pending).

The card and settings follow Windows 11: text size, accent colour, light and
dark theme, reduced motion and keyboard focus.

**The case-open card.** It appears when my linked pair's case opens near the PC,
on the display the gauge is on by default, and closes when the case closes, after
a time I choose, or on its close button. It never shows over a full-screen app
and never takes focus. It is on by default.

**Also new in 1.4.0.** A fully charged notice, one per charge per part, on by
default. Low battery alerts now cover the case. **Battery history** is a 24 hour
chart of left, right and case, stepping back by day over the 7 days kept. Screen
readers get names and values for the gauge, the card and the history. **Copy
diagnostics**, in the tray menu and under More, copies the recent log and a UI
stall log with addresses, ids, names and user paths removed. Motion follows
Windows' curves and is paced to each display's refresh rate. The card is built
before the first click, and the taskbar is read on its own events with a slower
safety poll. None of this has had a live run yet.

See [docs/overview.md](docs/overview.md#the-airpods-widget) and
[docs/architecture.md](docs/architecture.md#the-airpods-widget).

## What it does not do

- No driver, and no noise control or other Apple-only features.
- Audio is over Bluetooth only. Earshot does not touch any other transport.

[docs/requirements.md](docs/requirements.md) lists each promise and the test
that proves it.

## Settings and files

New installs write Hand back on shut down, sleep and Exit on, and Open on
startup on. An existing settings file keeps its saved values through an
install, an update or a repair.

Settings are in `%APPDATA%\Earshot\settings.json`. Logs and live-test evidence
are in `%LOCALAPPDATA%\Earshot`. The files the elevated tasks and the service
read are in `%ProgramData%\Earshot`.

## Remove

Turn **Open on startup** off in the menu, close Earshot, then run
`& "C:\Program Files\Earshot\Earshot.exe" uninstall` from an administrator
PowerShell (in Command Prompt, leave out the `&`). The install script's
Uninstall does the same. Your pairing is untouched and the AirPods' device
entries are turned back on, so Windows pages them at boot again. See
[docs/architecture.md](docs/architecture.md#setup-and-removal).

## Build and testing

The .NET SDK 10 on Windows x64.

    dotnet build Earshot.slnx
    dotnet test Earshot.slnx

`tools\check.ps1` rebuilds from scratch, runs the tests, runs the live-test
self-test against a fake machine, and fails if any compiler or analyser
warning is suppressed anywhere in the solution. `tools\build-release.ps1`
publishes the self-contained win-x64 folder, checks it against the manifest,
zips it to `artifacts\` and prints the zip's size and SHA-256. The release is
precompiled (ReadyToRun) to start faster, which makes the zip larger. I have
not measured a release build yet.

The live device tests under `tools\live-tests` are run by hand on real AirPods
and never by a build. [docs/verification.md](docs/verification.md) says which
have run.

`prototype\` holds the three PowerShell scripts that blocked the AirPods
before Earshot existed. They are superseded and kept for reference.

## Licence

Earshot's own code is MIT licensed: see [LICENSE](LICENSE). A release also
carries Microsoft files that are not. `Microsoft.Windows.SDK.NET.dll` and
`WinRT.Runtime.dll` ship unmodified under the Microsoft Windows SDK licence
terms (https://learn.microsoft.com/en-us/legal/windows-sdk/license), which the
MIT licence does not replace, so pass a release on whole and under terms that
protect those two files at least as much as Microsoft's do.
[`THIRD-PARTY-NOTICES.txt`](THIRD-PARTY-NOTICES.txt), in the repository and in
every release, names each third-party component, its publisher, its licence
and where to read the terms.

---

<div align="center"><sub>MIT licensed. The live tests are run by hand on real AirPods; the verification table says which have been.</sub></div>
