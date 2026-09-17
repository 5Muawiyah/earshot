# Earshot

A Windows 11 tray utility that keeps your AirPods on your phone until you ask
for them on the PC, connects and disconnects them with one click, and stops
Windows dropping them to call quality.

## What it does, and why

When the PC powers on, Windows pages every paired Bluetooth device. The AirPods
accept, and they leave the phone in the middle of whatever you were listening
to. There is no per-device setting to stop this. Microsoft's answer is that a
paired device cannot be stopped from connecting automatically, other than by
unpairing it or turning Bluetooth off:
https://learn.microsoft.com/en-us/answers/questions/4093792/how-to-prevent-windows-from-automatically-connecti

Earshot takes the other route. It disables the AirPods' own Bluetooth device
nodes while you are not using them on the PC. A disabled node stays disabled
across a restart, so Windows never pages them at boot. The pairing is untouched,
so enabling the nodes again is quick.

One left click on the tray icon enables the nodes and asks the AirPods to
connect. Another click disconnects them and blocks them again. Setup needs one
administrator prompt; nothing after that does.

## Requirements

- Windows 11 on x64.
- AirPods paired to this PC and connected to it at least once, so that Windows
  has created their device nodes. A device node that is not present cannot be
  disabled, and Earshot says so rather than doing nothing.
- One administrator approval, for setup.
- Nothing else to install. The release carries its own .NET runtime.

## Install

1. Get `Earshot-1.0.0-win-x64.zip`. There is no download page: the zip is built
   from this repository by `tools\build-release.ps1`, which writes it to
   `artifacts\` and prints its size and SHA-256. Check that hash against the
   copy you were given before you unzip it, because setup copies these files
   into Program Files.
2. Unzip it anywhere. You get an `Earshot` folder.
3. Run `Earshot.exe`. An earbud icon appears in the notification area.
4. Right click the icon and choose **Set up Earshot...**. Windows shows one
   administrator prompt. The item only appears while setup is needed.

Connect, disconnect and the audio protection do not need setup. Only the boot
block does, because disabling a device node needs administrator rights.

Setup, in that one elevated run:

- **Copies the folder to `C:\Program Files\Earshot`** and checks every copied
  file against the SHA-256 recorded in `Earshot.files.json`, the list the
  release build writes. Only administrators can write to Program Files, so the
  program the scheduled task runs later cannot be swapped for another one. A
  file that does not match stops the install.
- **Creates `C:\ProgramData\Earshot`**, with inherited permissions removed:
  SYSTEM and administrators can write, you can read. It holds `device.json`
  (which device to block, as a Bluetooth address and container id),
  `config.json` (Block at boot), `protection.json` and
  `protection-intent.json` (which Bluetooth services Earshot turned off, so
  they can be turned back on), and one small status file per run of the
  elevated worker. These files are machine-wide because the worker runs with
  nobody signed in and cannot read your profile.
- **Creates the Task Scheduler folder `\Earshot`** with three tasks, all
  running as SYSTEM: `Gate` (on demand, blocks and allows the device nodes),
  `Protect` (on demand, changes the Bluetooth services, with a longer time
  limit because that installs and removes drivers) and `BootBlock` (at
  startup). `Gate` and `Protect` grant your account read and execute, which is
  what lets the tray start them later with no prompt, and that permission
  cannot be used to change what they run. `BootBlock` grants read only: nothing
  but its own startup trigger ever starts it, so you cannot start it either.

Setup never changes your pairing, and never touches the registry Run key. Your
own settings stay in `%APPDATA%\Earshot\settings.json` and the log in
`%LOCALAPPDATA%\Earshot\logs`.

## Daily use

**Left click** the tray icon. It connects when the AirPods are disconnected and
disconnects them when they are connected. A small card appears near the tray
with the device name and what is happening, then closes itself. It never takes
focus from what you are doing.

The icon has four states:

| Icon | Meaning |
|---|---|
| Outlined earbuds | Disconnected |
| Solid earbuds | Connected |
| Faded solid earbuds | Busy: connecting, disconnecting or allowing |
| Outlined earbuds with one diagonal slash | Blocked at boot |

The tooltip reads `Earshot: <device name> - connected`, and likewise
`disconnected`, `blocked` or `not found`. It reads `unknown` when the audio
devices could not be read at all, so a failed read is never shown as a state.

**Right click** opens the menu. Top to bottom, with the exact wording:

| Item | What it does |
|---|---|
| `Safe mode: no device actions` | A caption, not a command. It appears only when the `EARSHOT_SAFE_MODE` variable is set, which turns every device action off. |
| `Connect`, or `Disconnect` when connected | The same as a left click. Unavailable while a change is in flight. |
| `Block at boot` | Tick. Keeps the AirPods' device nodes disabled while they are not in use. Turning it on before setup runs setup. The tick shows what is in force; when the setting cannot be read it shows neither state. |
| `Protect audio quality` | Tick, on by default. Turns off the Hands-Free profile, as described below. |
| `Turns off the AirPods microphone` | A caption under that setting. Always visible, never clickable, because it is the cost of the setting above. |
| `Open on startup` | Tick, on by default. Writes one value named `Earshot` under the current user's `Run` key, with the `--startup` argument. Earshot has to be running to put the block back when you stop using the AirPods. |
| `Choose device...` | Lists the paired Bluetooth devices, with the name to match. Choosing one pins it, and points the elevated worker at the same device. Use this if your AirPods are renamed, so that the default match "AirPods" no longer fits. |
| `Set up Earshot...` | Runs the one-time setup. Shown only while setup is needed. |
| `Exit` | Closes Earshot. With Block at boot on, it blocks the device nodes before it closes. |

## How the boot block works

The rule Earshot holds to is simple: with Block at boot on, the AirPods' device
nodes are enabled exactly while you are using them on this PC, and disabled the
rest of the time. The steady state is disabled, which is what stops the paging
at boot. Nothing has to happen at shutdown for that to hold.

- **Blocking** disables each of the AirPods' own Bluetooth nodes with the
  persistent flag, so the disable survives a restart. Without that flag Windows
  would put them back at the next boot and the whole thing would quietly fail.
  Nodes are chosen by container and Bluetooth address, not by name alone, so a
  paired phone, the Bluetooth radio itself and any other device are never
  touched.
- **Allowing** enables the same nodes again, then Earshot waits for the audio
  endpoints to come back and asks the audio driver to reconnect.
- **The tray cannot do either by itself.** It starts the SYSTEM task
  `\Earshot\Gate`, which is registered with an explicit execute permission for
  your account, so there is no prompt. The task takes the device identity from
  `device.json` only, never from whoever started it, and accepts a fixed short
  list of commands. The tray then reads the real device state back itself
  rather than trusting the task's result.
- **While you are listening**, the nodes stay enabled. When the AirPods stop
  being used, and nothing else is in flight, Earshot blocks them again after
  about 30 seconds of quiet. That wait is a deliberately cautious figure, not a
  measured one, so that a brief drop or a driver change does not cut off
  someone who is still listening. If a block does not take, it is tried again
  on a widening interval.
- **At startup**, `\Earshot\BootBlock` runs as SYSTEM before anyone signs in.
  If Block at boot is on and a target node is present and enabled, it blocks
  it. This is a safety net for the case where the nodes were left enabled, for
  example after a crash. Where it falls in the order against Windows' own
  reconnect is not documented, so it can lose the race. The block held at rest
  is what the design relies on.
- **Shutting down while connected** is the one case the at-rest rule does not
  cover. Earshot answers the shutdown message by starting a block and returning
  at once, but Windows kills a tray program a few seconds into shutdown, and a
  forced shutdown sends no message at all. If the block loses that race, the
  nodes are enabled at the next boot, Windows may page the AirPods once, and
  the boot task blocks them again afterwards. Disconnecting, or closing
  Earshot, before you shut down avoids it entirely.

With Block at boot off, Earshot still connects and disconnects, and Disconnect
only asks the AirPods to disconnect.

## Protect audio quality

On by default. **While it is on, the AirPods microphone does not work on this
PC**, apart from the moment during a connect described below.

Windows switches a Bluetooth headset from stereo A2DP to the Hands-Free profile
whenever an application opens a microphone or plays through the communications
category. Hands-Free is a narrow mono voice channel. That switch, not the codec,
is what makes the AirPods suddenly sound like a phone call, and browsers, chat
apps and games trigger it constantly:
https://learn.microsoft.com/en-us/windows-hardware/drivers/bluetooth/bluetooth-classic-audio

Earshot turns off the Hands-Free and Headset services for the AirPods and leaves
the A2DP sink alone, so there is nothing for Windows to switch to. The change
installs and removes profile drivers, so it can take a while and it runs through
the SYSTEM task rather than in the tray. Earshot records exactly which services
it turned off, and turns those back on when you turn the setting off or
uninstall.

The change can lapse: reconnecting or restarting can bring the Hands-Free
service back. Earshot re-reads the installed services after a connect and after
boot, and applies it again when it has reverted.

**Connect can take the protection off for a moment.** The one-shot reconnect is
a Hands-Free property, so turning Hands-Free off also takes away the filter that
carries the request. If the A2DP filter refuses it, the card says "Trying another
way": Earshot turns the protection off, connects, and turns it back on. The
microphone works on this PC while that runs, and it is not instant, because the
change installs and removes profile drivers. If the protection cannot be put
back, the card says "Connected, but audio quality protection did not apply." and
the microphone stays available until a later connect puts it back.

There is no equaliser and no codec setting, and there will not be one. Windows
picks the codec itself, from what both ends support, and exposes no public way to
override that choice. A control that claimed to would be doing nothing.

## Battery

**Earshot shows no battery level, and the tray has no battery element at all.**
No battery value could be found that Windows exposes for these AirPods, and a
number that is not measured would be a made-up number.

The check was run on 15 September 2026 with the AirPods connected to this PC:

- The PnP battery query, filtered to battery keys, returned nothing for any of
  the AirPods device nodes.
- A dump of every property of the 13 device nodes in the AirPods container held
  no battery key.
- WinRT returned the battery key
  `{104EA319-6EE2-4701-BD47-8DDBF425BBE5},2` empty for the AirPods audio
  endpoint and for its device nodes.

Each of the three carried a positive control in the same read, so a query that
was simply broken could not pass as an absent value: the device reported as
present and OK in PnP, its endpoints were active in Core Audio, and WinRT
reported it as connected and paired.

The same check with the AirPods disconnected has not been run yet. It is in the
live tests. It cannot change the outcome, because a value would only be expected
while they are connected.

Per-earbud battery, and the noise control modes, ride on Apple's own protocol
over a Bluetooth channel that Windows does not open to ordinary programs. That
needs a kernel driver, which is out of scope here.

## Command line

One program, `Earshot.exe`, chosen by its first argument.

| Command | What it does |
|---|---|
| `Earshot.exe` | The tray application. `--startup` is the same thing, and is what the startup value passes. |
| `Earshot.exe probe [audio\|topology\|nodes\|services\|task\|battery\|all] [--json] [--out <path>]` | Read-only diagnostics. Reads endpoints, walks the audio topology, reads the device nodes, lists the installed Bluetooth services, reads the scheduled tasks, and reports the battery answer above. It changes nothing. On a machine where Earshot is not set up and no device is pinned it still writes a full report, and exits 78 to say so: the nodes and services targets had no device to read. Read the report, not the exit code. |
| `Earshot.exe probe icon --out <folder>` | Writes the tray icon to files, in each of its four states, at three screen scalings and in both inks, for checking how it looks. |
| `Earshot.exe install <userSid> <address> <containerGuid> [--principal user]` / `Earshot.exe uninstall` | The one-time setup and its removal. Both need an elevated administrator and refuse to run as SYSTEM. The menu runs `install` with those three arguments filled in; a bare `install` is refused, so it is not a command to type by hand. |
| `Earshot.exe gate <verb> <nonce> [address]` / `Earshot.exe gate-protect <verb> <nonce>` | The elevated workers: one for the device nodes, one for the Bluetooth services. Started by Earshot's own scheduled tasks, not by hand. |
| `Earshot.exe diag <target>` | Single live actions for testing on real hardware: connect, disconnect, a raw driver request, a gate run, the unelevated service call, and the battery sweep. **All but the battery sweep and `gate status` change the state of the device**; those two only read. They exist for the live tests in `tools\live-tests` and are not part of normal use. |

Two variables help testing. `EARSHOT_DATA_ROOT=<absolute folder>` moves every
data folder under that folder. `EARSHOT_SAFE_MODE=1` turns every device action
off, so only reads happen. `install`, `uninstall`, `gate` and `gate-protect`
refuse to run while either is set. A `diag` target is refused in safe mode only,
so the live test scripts stop the run themselves when `EARSHOT_DATA_ROOT` is set
rather than let a real device action write its evidence somewhere else.

## Known caveats

- **Device nodes that are not present cannot be disabled.** Connect the AirPods
  to this PC once from Windows Bluetooth settings before blocking. Earshot
  reports this rather than silently doing nothing.
- **A driver update can re-enumerate the AirPods as fresh device nodes**, which
  would not carry the disable. Earshot blocks again when it next sees them
  enabled and unused, but a boot in between can let Windows page them.
- **Shutting down while connected** relies on the best-effort backstop
  described above, not on a guarantee.
- **Fast Startup has not been tested.** It was off on the machine this was
  built against. Whether a persistent device-node disable behaves the same
  through a hybrid shutdown is unverified.
- **Whether the A2DP driver accepts the connect request is unverified.** The
  one-shot reconnect and disconnect properties are documented for Hands-Free
  filters, and the audio protection removes the Hands-Free filter. If the A2DP
  filter rejects the request, connect turns the protection off, connects with
  the Hands-Free filter back in place, and turns the protection on again, as
  described under Protect audio quality; it reports honestly when the AirPods
  still do not arrive. Disconnect always ends in a block when Block at boot is
  on, so it works either way.
- **Administrator protection**, the newer Windows elevation model, is off by
  default on this machine and has not been tested with. Setup follows
  Microsoft's guidance for a highest-privilege task, and passes the device
  identity to the elevated run as arguments rather than reading it from a user
  profile, which is what that model requires.
- **The Hands-Free change installs and removes drivers.** It can be slow, and
  the audio endpoints come and go while it runs.
- **The Hands-Free setting can revert** on reconnect or restart. Earshot checks
  and re-applies it, so it can take effect a moment after a connect rather than
  instantly.
- The tray must be running for the block to go back on when you stop using the
  AirPods, which is why Open on startup is on by default.

## Verification status

Nothing below that needs the AirPods themselves has been run yet. Those rows
stay pending until the live tests in `tools\live-tests` are run on the device.

| What | Status |
|---|---|
| Builds with no warnings, and no suppressed or downgraded analyser rule | Done, checked on every build |
| Unit tests: device-node matching, block and connection state, the elevated worker's argument validation, settings handling, icon bytes, card placement | Done |
| The live test scripts parse, and every Earshot command line they pass is accepted by the application's own argument parsers | Done. Checked in the unit tests, which never run a script |
| Read-only probe of the audio endpoints on this PC | Done. The AirPods container is found by name and grouped correctly |
| Read-only walk from the endpoints to the audio driver, including reading a pin property from both the A2DP and Hands-Free filters | Done. Both filters answer, so the connect path is reachable |
| Read-only reads of the device nodes, installed Bluetooth services and scheduled tasks | Done |
| Self-contained release runs from an unzipped folder | Done |
| Connect and disconnect on the AirPods | Pending |
| Connect while blocked: allow, then reconnect | Pending |
| Block and allow, and the disable surviving a restart | Pending |
| The power cycle test: after a full power cycle the AirPods stay on the phone | Pending |
| Shutting down while connected | Pending |
| Turning the Hands-Free profile off and on | Pending |
| Starting the SYSTEM task from the tray without a prompt | Pending |
| The battery check with the AirPods disconnected | Pending |
| Fast Startup | Pending |

## Live tests

The pending rows above are settled by the scripts in `tools\live-tests`, with
one exception: no script settles Fast Startup. Test 08 powers the machine down
with whatever it is set to and does not record which, so a full sitting leaves
that row where it is. To cover it by hand, turn Fast Startup on and run
`04-BlockAndReboot.ps1 -Note "fast startup on"`. They
are run by hand, with the AirPods and the phone there, against an installed copy
or an unzipped release. Nothing runs them for you, and none of them runs during
a build.

    powershell -NoProfile -ExecutionPolicy Bypass -File .\Run-LiveTests.ps1 -List
    powershell -NoProfile -ExecutionPolicy Bypass -File .\Run-LiveTests.ps1 -Test 01 -ExePath "C:\Program Files\Earshot\Earshot.exe"

They are numbered riskiest first. Test 08 is the acceptance test the whole
application exists for: after a full power cycle, the AirPods are still
connected to the phone. Every step that changes a device, a scheduled task or a
folder is printed first, with what it will do, and waits for you to agree. Six
of the tests are in two halves, because a script cannot survive a restart; the
first half prints the command to run afterwards and saves it as well.

Each run writes its evidence to `%LOCALAPPDATA%\Earshot\livetest`, one folder
per run, with a `result.json` that records every criterion as pass, fail or
inconclusive against a stated rule. `inconclusive` is a real answer and is
recorded as one.

`00-Restore.ps1` puts the machine back if a test stops in the middle. Read
`tools\live-tests\README.md` before the first run, so you know how to run it
before you need it.

## Uninstall

Turn **Open on startup** off in the menu first, then close Earshot. The startup
value belongs to the tray, and uninstall does not touch it; Earshot removes a
value left behind the next time it starts with the setting off.

Then run, from an administrator PowerShell or Command Prompt:

    "C:\Program Files\Earshot\Earshot.exe" uninstall

Windows shows its administrator prompt when you open that window. Uninstall
refuses to run without it.

It enables every device node it disabled, turns back on the Bluetooth services
it recorded, deletes the three scheduled tasks and the `\Earshot` task folder,
removes `C:\ProgramData\Earshot`, and removes `C:\Program Files\Earshot`,
scheduling that last one for the next restart if it is in use. Each step is
reported. If a node or a service could not be restored, it keeps the two files
that record what to restore and says so, so you can run it again.

Your pairing is never touched, and `%APPDATA%\Earshot` is left alone, so your
settings survive a reinstall.

## Building from source

The .NET SDK 10 on Windows x64.

    dotnet build Earshot.slnx
    dotnet test Earshot.slnx

`tools\check.ps1` rebuilds from scratch, runs the tests, and fails if any
compiler or analyser warning is suppressed anywhere in the solution.

`tools\build-release.ps1` publishes the self-contained win-x64 folder, checks
every published file against the manifest the build writes, zips it to
`artifacts\Earshot-<version>-win-x64.zip`, and prints the zip's size and
SHA-256. The version comes from `src\Earshot\Earshot.csproj`.

`Earshot.pdb` is published, listed in the manifest and installed on purpose. It
is 247 KB, and it is what turns the stack trace in a logged exception into file
names and line numbers you can act on. Since the log is where Earshot reports
the failures it refuses to swallow, the symbols earn their place.

`prototype\` holds the three PowerShell scripts that blocked the AirPods before
Earshot existed. They are superseded and kept only for reference.

## Licence

MIT. See `LICENSE`.
