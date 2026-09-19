<div align="center">

# Earshot

### A Windows 11 tray utility that keeps AirPods off the PC until you ask for them

![Windows 11](https://img.shields.io/badge/platform-Windows%2011-0078D4) &nbsp;![.NET](https://img.shields.io/badge/.NET-10.0-512BD4) &nbsp;[![build](https://github.com/5Muawiyah/earshot/actions/workflows/build.yml/badge.svg)](https://github.com/5Muawiyah/earshot/actions/workflows/build.yml) &nbsp;![Licence: MIT](https://img.shields.io/badge/licence-MIT-blue)

</div>

<p align="center"><img src="docs/images/hero.png" alt="The Earshot tray icon" width="100%"></p>

<p align="center"><i>Placeholder art, not a screenshot. Pending a real capture of the tray in use.</i></p>

Muawiyah Jahanzaib built Earshot because Windows pages every paired Bluetooth
device when the PC powers on, including AirPods that were left on a phone in
the middle of something, and there is no per-device setting to stop it. Earshot
disables the AirPods' own Bluetooth device nodes while they are not in use on
the PC, so Windows has nothing to page at boot; connects and disconnects them
with one click; and blocks the Hands-Free profile so a browser tab or a game
cannot drop them to call quality.

`docs/` currently holds only the placeholder images used below. The one
written guide beyond this file lives with the live tests.

| Document | What it covers |
|---|---|
| [Live test guide](tools/live-tests/README.md) | how to run the live tests by hand, and how to put the machine back if one stops in the middle |

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

## What it had to do

1. **Stop Windows paging the AirPods at boot.** Supporting work: the
   persistent-disable unit tests confirm the disable and block matching logic.
   Proof: Test 04 (block survives a restart) and Test 08 (the full power cycle
   acceptance test), both pending a live run.
2. **Connect and disconnect the AirPods with one click.** Supporting work:
   connection-state unit tests confirm the state machine each click drives.
   Proof: Test 01 passed on the AirPods on 19 September 2026, in the shipping
   default with Protect audio quality on. Test 02, disconnect in detail, is
   pending.
3. **Keep the AirPods on A2DP**, so a browser tab or a game cannot drop them to
   call quality. Supporting work: the read-only walk from the audio endpoints
   to both the A2DP and Hands-Free filters shows both filters answer, so the
   connect path is reachable. Proof: Test 06, pending.
4. **Show no battery figure that was not read off the device.** Proven by:
   three independent read-only checks, each run against a positive control so
   a broken query could not be mistaken for a missing value (done, see
   [What it does not do](#what-it-does-not-do)); the same check with the
   AirPods disconnected is Test 11, pending.
5. **Ask for exactly one administrator prompt, at setup, and nothing after.**
   Supporting work: the elevated worker's argument-validation unit tests
   confirm it refuses anything it does not expect. Proof: Test 15, Uninstall
   reversal, which exercises the live setup and its reversal, pending.

## How it works

The rule Earshot holds to is simple: with Block at boot on, the AirPods'
device nodes are enabled exactly while you are using them on this PC, and
disabled the rest of the time. The steady state is disabled, which is what
stops the paging at boot. Nothing has to happen at shutdown for that to hold.

- **Blocking** disables each of the AirPods' own Bluetooth nodes with the
  persistent flag, so the disable survives a restart. Without that flag
  Windows would put them back at the next boot and the whole thing would
  quietly fail. Nodes are chosen by container and Bluetooth address, not by
  name alone, so a paired phone, the Bluetooth radio itself and any other
  device are never touched.
- **Allowing** enables the same nodes again, then Earshot waits for the audio
  endpoints to come back and asks the audio driver to reconnect.
- **The tray cannot do either by itself.** It starts the SYSTEM task
  `\Earshot\Gate`, which takes the device identity from `device.json` only,
  never from whoever started it, and accepts a fixed short list of commands.
  The tray then reads the real device state back itself rather than trusting
  the task's result.
- **While you are listening**, the nodes stay enabled. When the AirPods stop
  being used, and nothing else is in flight, Earshot blocks them again after
  about 30 seconds of quiet, a deliberately cautious figure rather than a
  measured one, so a brief drop does not cut off someone still listening. If a
  block does not take, it is tried again on a widening interval.
- **At startup**, `\Earshot\BootBlock` runs as SYSTEM before anyone signs in.
  If Block at boot is on and a target node is present and enabled, it blocks
  it, as a safety net for the case where the nodes were left enabled, after a
  crash for instance. Windows does not document where this task falls against
  its own reconnect, so it can lose the race. The design leans on the nodes
  already being disabled, not on this task winning.
- **Shutting down while connected** is the one case the at-rest rule does not
  cover, and is described under [What it does not do](#what-it-does-not-do).

With Block at boot off, Earshot still connects and disconnects, and Disconnect
only asks the AirPods to disconnect.

**Protecting audio quality.** On by default. While it is on, the AirPods
microphone does not work on this PC, apart from the moment during a connect
described below.

Windows switches a Bluetooth headset from stereo A2DP to the Hands-Free
profile whenever an application opens a microphone or plays through the
communications category. Hands-Free is a narrow mono voice channel, and that
switch, not the codec, is what makes the AirPods suddenly sound like a phone
call:
https://learn.microsoft.com/en-us/windows-hardware/drivers/bluetooth/bluetooth-classic-audio

Earshot turns off the Hands-Free and Headset services for the AirPods and
leaves the A2DP sink alone, so there is nothing for Windows to switch to.
Earshot records exactly which services it turned off, and turns those back on
when you turn the setting off or uninstall. The change installs and removes
profile drivers, so it can be slow, and the audio endpoints come and go while
it runs; it runs through a SYSTEM task rather than in the tray. The change can
also lapse: reconnecting or restarting can bring
the Hands-Free service back, so Earshot re-reads the installed services after
a connect and after boot and re-applies it when it has reverted, so it can
take effect a moment after a connect rather than instantly.

Connect can take the protection off for a moment: the one-shot reconnect used
to bring the AirPods back is a Hands-Free property, so turning Hands-Free off
also removes the filter that carries the request. If the A2DP filter refuses
it, the card says "Trying another way": Earshot turns the protection off,
connects, and turns it back on. The microphone works on this PC while that
runs, and it is not instant, because the change installs and removes profile
drivers. If the protection cannot be put back, the card says "Connected, but
audio quality protection did not apply." and the microphone stays available
until a later connect puts it back.

## The safety model

- **Setup asks for one administrator prompt and does the rest itself.** It
  copies the folder to `C:\Program Files\Earshot` and checks every copied file
  against the SHA-256 recorded in `Earshot.files.json`, the list the release
  build writes. Only administrators can write to Program Files, so the
  program the scheduled tasks run later cannot be swapped for another one; a
  file that does not match stops the install.
- **`C:\ProgramData\Earshot`** has its inherited permissions removed: SYSTEM
  and administrators can write, you can read. It holds `device.json` (which
  device to block, as a Bluetooth address and container id), `config.json`
  (Block at boot), `protection.json` and `protection-intent.json` (which
  Bluetooth services Earshot turned off, so they can be turned back on), and
  one status file per run of the elevated worker.
- **Three scheduled tasks, all running as SYSTEM**, in their own Task
  Scheduler folder: `Gate` (blocks and allows the device nodes on demand),
  `Protect` (changes the Bluetooth services on demand, with a longer time
  limit because that installs and removes drivers) and `BootBlock` (at
  startup only). `Gate` and `Protect` grant your account read and execute,
  which is what lets the tray start them with no prompt, and that permission
  cannot be used to change what they run. `BootBlock` grants read only:
  nothing but its own startup trigger ever starts it, so you cannot start it
  either.
- **The device identity is read from `device.json`, never from whoever started
  the task.** Each task accepts a fixed short list of commands, and the tray
  reads the real device state back itself afterwards rather than trusting the
  task's own result.
- **Setup never changes your pairing**, and never touches the registry `Run`
  key. Your own settings stay in `%APPDATA%\Earshot\settings.json` and the log
  in `%LOCALAPPDATA%\Earshot\logs`, separate from the machine-wide state in
  `ProgramData`, because the elevated worker runs with nobody signed in and
  cannot read your profile.
- **A device that cannot play audio from this PC is refused**, a phone for
  instance, with the reason stated: "That device cannot play audio from this
  PC. Choose headphones or speakers."
- **Two environment variables keep testing off the real device.**
  `EARSHOT_DATA_ROOT` moves every data folder elsewhere, and `EARSHOT_SAFE_MODE`
  turns every device action off so only reads happen. `install`, `uninstall`,
  `gate` and `gate-protect` refuse to run while either is set. A `diag` target
  is refused in safe mode only, so `EARSHOT_DATA_ROOT` on its own does not stop
  a device action; the live test scripts stop the run themselves when it is
  set, rather than let a real device action write its evidence somewhere else.

## A closer look

<img src="docs/images/tray-menu.png" alt="The Earshot tray icon and its right-click menu" width="360" align="right">

<p align="center"><i>Placeholder art, not a screenshot. Pending a real capture of the menu open.</i></p>

**The tray menu.** Left click connects or disconnects, matching whichever
state the AirPods are in. The icon has four states:

| Icon | Meaning |
|---|---|
| Outlined earbuds | Disconnected |
| Solid earbuds | Connected |
| Faded solid earbuds | Busy: connecting, disconnecting or allowing |
| Outlined earbuds with one diagonal slash | Blocked at boot |

The tooltip reads `Earshot: <device name> - connected`, and likewise
`disconnected`, `blocked` or `not found`. If the audio devices cannot be read
at all it reads `unknown`, rather than picking one of the four and hoping.

Right click opens the menu. Top to bottom, with the exact wording:

| Item | What it does |
|---|---|
| `Safe mode: no device actions` | A caption, not a command. It appears only when the `EARSHOT_SAFE_MODE` variable is set, which turns every device action off. |
| `Connect`, or `Disconnect` when connected | The same as a left click. Unavailable while a change is in flight. |
| `Block at boot` | Tick. Keeps the AirPods' device nodes disabled while they are not in use. Turning it on before setup runs setup. The tick shows what is in force; when the setting cannot be read it shows neither state. |
| `Protect audio quality` | Tick, on by default. Turns off the Hands-Free profile, as described above. |
| `Turns off the AirPods microphone` | A caption under that setting, always visible and never clickable. It is there because that is what the setting costs you. |
| `Open on startup` | Tick, on by default. Writes one value named `Earshot` under the current user's `Run` key, with the `--startup` argument. Earshot has to be running to put the block back when you stop using the AirPods. |
| `Choose device...` | Lists the paired Bluetooth devices, with the name to match. Choosing one pins it, and points the elevated worker at the same device. Use this if your AirPods are renamed, so that the default match "AirPods" no longer fits. A device that cannot play audio from this PC, a phone for instance, is refused: "That device cannot play audio from this PC. Choose headphones or speakers." |
| `Set up Earshot...` | Runs the one-time setup. Shown only while setup is needed. |
| `Exit` | Closes Earshot. With Block at boot on, it blocks the device nodes before it closes. |

<br clear="all">

<img src="docs/images/connect-card.png" alt="The small card Earshot shows near the tray after a connect" width="360" align="right">

**The connect and disconnect card.** A small card appears near the tray with
the device name and what is happening, then closes itself without taking
focus from whatever you are doing. If the AirPods do not arrive the ordinary
way, it says so honestly, for instance "Trying another way" while Earshot
works around a driver refusal.

<p align="center"><i>Placeholder art, not a screenshot. Pending a real capture of the card in view.</i></p>

<br clear="all">

<img src="docs/images/boot-block.png" alt="Placeholder image, standing in for a diagram of the device node being disabled at boot" width="360" align="right">

**The boot block.** With Block at boot ticked, the AirPods' device nodes are
disabled the moment they are not in use, and stay disabled through a restart.
Nothing has to run at shutdown for that to hold; the steady state does the
work on its own.

<p align="center"><i>Placeholder art, not a screenshot. Pending a real capture of this state.</i></p>

<br clear="all">

<img src="docs/images/audio-protection.png" alt="Placeholder image, standing in for a diagram of the Hands-Free profile being turned off" width="360" align="right">

**Audio quality protection.** With Protect audio quality ticked, Windows has
no Hands-Free profile to fall back to, so a browser tab or a game cannot drop
the AirPods to a narrow voice channel. The tray tells you plainly that this
turns off the AirPods microphone on this PC.

<p align="center"><i>Placeholder art, not a screenshot. Pending a real capture of this state.</i></p>

<br clear="all">

## What it does not do

- **Show a battery level.** There is no battery element in the tray at all.
  Three read-only checks on 15 September 2026, with the AirPods connected to
  this PC, found no battery value Windows exposes for them: the PnP battery
  query returned nothing, a dump of every property on the AirPods' device
  nodes held no battery key, and WinRT returned the standard battery key
  empty for the AirPods' audio endpoint. Each check carried a positive
  control in the same read, so a broken query could not be mistaken for a
  missing value. The same check with the AirPods disconnected has not been
  run yet; it is Test 11 in the live tests, and it cannot change the outcome,
  because a value would only be expected while connected. Per-earbud battery
  and the noise control modes ride on Apple's own protocol over a channel
  Windows does not open to ordinary programs, which needs a kernel driver and
  is out of scope here.
- **Disable a device node that is not present.** Connect the AirPods to this
  PC once from Windows Bluetooth settings before blocking; Earshot reports
  this rather than silently doing nothing.
- **Survive a driver re-enumeration cleanly.** A driver update can give the
  AirPods fresh device nodes, which would not carry the disable. Earshot
  blocks again the next time it sees them enabled and unused, but a boot in
  between can let Windows page them.
- **Guarantee the shutdown-while-connected case.** Earshot answers the
  shutdown message by starting a block and returning at once, but Windows
  kills a tray program a few seconds into shutdown, and a forced shutdown
  sends no message at all. If the block loses that race, the nodes are
  enabled at the next boot, Windows may page the AirPods once, and the boot
  task blocks them again afterwards. Disconnecting, or closing Earshot,
  before you shut down avoids it entirely.
- **Cover Fast Startup.** It was off on the machine this was built against,
  so whether a persistent device-node disable behaves the same through a
  hybrid shutdown is unverified. Tests 08 and 09, the two that power the
  machine right down, record which it was set to, so a run made with it off
  is not later mistaken for one that covered it.
- **Confirm the A2DP connect request is always accepted.** The one-shot
  reconnect and disconnect properties are documented for Hands-Free filters,
  and the audio protection removes that filter. If the A2DP filter rejects
  the request, connect turns the protection off, connects with the
  Hands-Free filter back in place, and turns the protection on again; it
  reports honestly when the AirPods still do not arrive. Disconnect always
  ends in a block when Block at boot is on, so it works either way.
- **Claim anything about Administrator protection**, the newer Windows
  elevation model. It is off by default on this machine and has not been
  tested with. Setup follows Microsoft's guidance for a highest-privilege
  task, passing the device identity to the elevated run as arguments rather
  than reading it from a user profile, which is what that model requires.
- **Offer an equaliser or a codec setting**, and there will not be one.
  Windows picks the codec itself, from what both ends support, and exposes no
  public way to override that choice.
- **Keep the block current without running.** The tray must be running for
  the block to go back on when you stop using the AirPods, which is why Open
  on startup is on by default.

## Getting started

1. Get `Earshot-1.0.0-win-x64.zip`. There is no download page: the zip is
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

Requirements: Windows 11 on x64; the AirPods paired to this PC and connected
to it at least once, so Windows has created their device nodes; one
administrator approval, for setup; nothing else to install, because the
release carries its own .NET runtime.

## Uninstall

Turn **Open on startup** off in the menu first, then close Earshot. The
startup value belongs to the tray, and uninstall does not touch it; Earshot
removes a value left behind the next time it starts with the setting off.

Then run, from an administrator PowerShell or Command Prompt:

    "C:\Program Files\Earshot\Earshot.exe" uninstall

Windows shows its administrator prompt when you open that window. Uninstall
refuses to run without it.

It enables every device node it disabled, turns back on the Bluetooth
services it recorded, deletes the three scheduled tasks and the `\Earshot`
task folder, removes `C:\ProgramData\Earshot`, and removes
`C:\Program Files\Earshot`, scheduling that last one for the next restart if
it is in use. Each step is reported. If a node or a service could not be
restored, it keeps the two files that record what to restore and says so, so
you can run it again.

Your pairing is never touched, and `%APPDATA%\Earshot` is left alone, so your
settings survive a reinstall.

## Build and testing

The .NET SDK 10 on Windows x64.

    dotnet build Earshot.slnx
    dotnet test Earshot.slnx

`tools\check.ps1` rebuilds from scratch, runs the tests, and fails if any
compiler or analyser warning is suppressed anywhere in the solution.

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

### Verification status

The read-only rows were done on this machine with the AirPods paired and
connected. The first live sitting on the AirPods was on 19 September 2026:
Tests 01 and 05, and ordinary use of the tray with its log read afterwards.
Every time below is a single run, measured once, not a specification. A row
stays pending until a run on the device has written the evidence for it.

| What | Status |
|---|---|
| Builds with no warnings, and no suppressed or downgraded analyser rule | Done, checked on every build |
| Unit tests: device-node matching, block and connection state, the elevated worker's argument validation, settings handling, icon bytes, card placement | Done |
| The live test scripts parse, and every Earshot command line they pass is accepted by the application's own argument parsers | Done. Checked in the unit tests, which never run a script |
| Read-only probe of the audio endpoints on this PC | Done. The AirPods container is found by name and grouped correctly |
| Read-only walk from the endpoints to the audio driver, including reading a pin property from both the A2DP and Hands-Free filters | Done. Both filters answer, so the connect path is reachable |
| Read-only reads of the device nodes, installed Bluetooth services and scheduled tasks | Done |
| Self-contained release runs from an unzipped folder | Done |
| Connect and disconnect on the AirPods | Connect: done. Test 01 passed all 11 criteria. With Protect audio quality on, the A2DP filter accepted the reconnect request and the AirPods were playing from this PC 2749 ms later, and 3413 ms on the second run, inside the 15 s allowed. The Hands-Free assisted fallback is not needed (`hfpAssistedFallbackNeeded` = no). With protection off one run took 14371 ms, which is close to the limit. Disconnect: each of the three disconnects in Test 01 was confirmed in under 60 ms and the AirPods went back to the phone. Test 02, disconnect in detail, is pending |
| Connect while blocked: allow, then reconnect | Seen in the application's log during ordinary use: a click while blocked allowed the nodes, and Windows then connected the AirPods itself about a second later. Test 03, which scores it, is pending |
| Block and allow, and the disable surviving a restart | Block and allow: done. Test 05 showed all 8 device nodes disabled with the persistent flag before the allow, and none after it (`enableClearsConfigFlagsDisabled` = yes); the log shows a block disabling all 8. Test 05 is recorded as failed overall, because the test scripts could not read Earshot's exit code at the time; that fault is fixed and the run was not re-scored. Surviving a restart, Test 04: pending |
| The power cycle test: after a full power cycle the AirPods stay on the phone | Pending |
| Shutting down while connected | Pending |
| Turning the Hands-Free profile off and on | Done through the SYSTEM task, in Test 01 and from the tray: off and on each completed in about 3 to 4 seconds, and the microphone endpoint went and came back with it. Test 06, what the same call returns without elevation, is pending |
| Starting the SYSTEM task from the tray without a prompt | Seen in the application's log: the tray, not elevated, started the Gate and Protect tasks and both reported success. Test 07, which checks the arguments arrive, is pending |
| The battery check with the AirPods disconnected | Pending |
| Fast Startup | Pending |

### Live tests

The pending rows above are settled by the scripts in `tools\live-tests`, with
one exception: no single script settles Fast Startup on its own. Tests 08 and
09 read the setting and record it, but they power the machine down with
whatever it happens to be, so a full sitting leaves that row where it is
unless it was on. To cover it by hand, turn Fast Startup on and run 08 or 09,
the two tests that shut the machine right down rather than restarting it, then
read the finding back beside the criteria: `fastStartupAtPowerDown` from test
08, `fastStartupAtShutdown` from test 09. A restart always performs a full
shutdown and a cold boot, so no restart test can cover it.

The scripts are run by hand, with the AirPods and the phone there, against an
installed copy or an unzipped release. Nothing runs them for you, and none of
them runs during a build.

    powershell -NoProfile -ExecutionPolicy Bypass -File .\Run-LiveTests.ps1 -List
    powershell -NoProfile -ExecutionPolicy Bypass -File .\Run-LiveTests.ps1 -Test 01 -ExePath "C:\Program Files\Earshot\Earshot.exe"

They are numbered riskiest first. Test 08 is the acceptance test the whole
application exists for: after a full power cycle, the AirPods are still
connected to the phone. Every step that changes a device, a scheduled task or
a folder is printed first, with what it will do, and waits for you to agree.
Six of the tests are in two halves, because a script cannot survive a
restart; the first half prints the command to run afterwards and saves it as
well.

Each run writes its evidence to `%LOCALAPPDATA%\Earshot\livetest`, one folder
per run, with a `result.json` that records every criterion as pass, fail or
inconclusive against a stated rule. Where a criterion cannot be settled it
goes down as inconclusive rather than being left out.

`00-Restore.ps1` puts the machine back if a test stops in the middle. Read
[`tools\live-tests\README.md`](tools/live-tests/README.md) before the first
run, so you know how to run it before you need it.

The scripts themselves are tested by `tools\live-tests\selftest`, which runs
every one of them, and both halves of each that needs a restart, against a
fake machine with no device, no scheduled task and no `Earshot.exe` anywhere
in it. Reading the source cannot tell you whether a script gets to its end and
records everything it should; running it against a fake machine can. It runs
as part of `tools\check.ps1`.

## Command line

One program, `Earshot.exe`, chosen by its first argument.

| Command | What it does |
|---|---|
| `Earshot.exe` | The tray application. `--startup` is the same thing, and is what the startup value passes. |
| `Earshot.exe probe [audio\|topology\|nodes\|services\|task\|battery\|all] [--json] [--out <path>]` | Read-only diagnostics. Reads endpoints, walks the audio topology, reads the device nodes, lists the installed Bluetooth services, reads the scheduled tasks, and reports the battery answer above. It changes nothing. On a machine where Earshot is not set up and no device is pinned it still writes a full report, and exits 78 to say so: the nodes and services targets had no device to read. So read the report rather than the exit code. |
| `Earshot.exe probe icon --out <folder>` | Writes the tray icon to files, in each of its four states, at three screen scalings and in both inks, for checking how it looks. |
| `Earshot.exe install <userSid> <address> <containerGuid> [--principal user]` / `Earshot.exe uninstall` | The one-time setup and its removal. Both need an elevated administrator and refuse to run as SYSTEM. The menu runs `install` with those three arguments filled in; a bare `install` is refused, so it is not a command to type by hand. |
| `Earshot.exe gate <verb> <nonce> [address]` / `Earshot.exe gate-protect <verb> <nonce>` | The elevated workers: one for the device nodes, one for the Bluetooth services. Started by Earshot's own scheduled tasks, not by hand. |
| `Earshot.exe diag <target>` | Single live actions for testing on real hardware: connect, disconnect, a raw driver request, a gate run, the unelevated service call, and the battery sweep. **All but the battery sweep and `gate status` change the state of the device**; those two only read. They exist for the live tests in `tools\live-tests` and are not part of normal use. |

## Licence

MIT. See [LICENSE](LICENSE).

---

<div align="center"><sub>Built by Muawiyah Jahanzaib. MIT licensed. The live tests are run by hand on real AirPods; the verification table says which have been.</sub></div>
