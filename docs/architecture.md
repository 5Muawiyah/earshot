# Architecture

The technical guide: how Earshot holds the AirPods off the PC at rest, how it
protects audio quality, the permission model behind both, and the command
line the tray and the live tests both drive. For a plain-English tour of the
tray instead, see [overview.md](overview.md).

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
  measured one, so a brief drop does not cut off someone still listening. If
  a block does not take, it is tried again on a widening interval.
- **At startup**, `\Earshot\BootBlock` runs as SYSTEM before anyone signs in.
  If Block at boot is on and a target node is present and enabled, it blocks
  it, as a safety net for the case where the nodes were left enabled, after a
  crash for instance. Windows does not document where this task falls
  against its own reconnect, so it can lose the race. The design leans on
  the nodes already being disabled, not on this task winning.
- **Shutting down or sleeping while connected** is handled by the hand-back
  below, which goes further than leaving it to the at-rest rule alone; see
  [requirements.md](requirements.md#what-it-does-not-do) for what it still
  cannot cover.

With Block at boot off, Earshot still connects and disconnects, and
Disconnect only asks the AirPods to disconnect.

### Handing the AirPods back at shut down and sleep

With **Hand back at shut down and sleep** on (the default), Earshot releases
the AirPods and blocks their device nodes again before the session actually
ends or the machine actually sleeps, so this PC does not take them back the
moment it restarts or wakes.

**Where it runs.** Entirely inside the tray's own hidden window, on the real
Windows messages it already receives: `WM_ENDSESSION` (with the reply held
open) for a shut down, a restart or a sign-out, and `WM_POWERBROADCAST` with
`PBT_APMSUSPEND` for sleep. Nothing runs unless that window gets the
message, which is why the tray has to be running for any of this to happen.

**The fixed order**, the same for both triggers:

1. Let go of any open Play from a phone link.
2. Send the disconnect and wait for confirmation that the audio render
   endpoint has left ACTIVE.
3. Send the block whether or not the disconnect confirmed. Leaving every
   node enabled by choice would break the at-rest guarantee, so the block
   still goes out; if render had not actually left ACTIVE, Windows vetoes
   that one sink entry and the outcome comes back Partial rather than an
   assumed Success. The log records whatever the gate actually returned.

**Two caps, held as constants in the tray's own code:**

| Trigger | Cap |
|---|---|
| Shut down, restart, sign-out (`WM_ENDSESSION`) | 4 seconds |
| Sleep (`PBT_APMSUSPEND`) | 1.5 seconds |

Reaching a cap logs "cut short" and names what was still running. The cap
is a single deadline, taken once and shared by both the reply hold and the
hand-back's own work, so a step that overruns it is caught and its "cut
short" line written before the reply returns, not as an afterthought once
the process has moved on. A block that was already sent is never abandoned
at the cap: it is a request already handed to the elevated Gate task, which
keeps running in its own process and finishes on its own, and its outcome is
still written to the log once it arrives, even after the tray itself has
gone. A disconnect that has not confirmed by the time its own, shorter share
of the cap runs out is treated the same way: the request stays sent, and
Earshot moves on to check whether it can still block rather than waiting any
longer.

**Why sending the disconnect first still matters.** A block sent while the
AirPods are still rendering audio is vetoed by Windows at that one sink
entry, seen on the owner's machine, so sending the disconnect first gives
Windows the best chance to have already left render before the block goes
out. The block is sent either way; when the disconnect has not confirmed,
the veto can still catch that one entry, and the outcome comes back Partial,
recorded as such, rather than an assumed Success.

**A block already queued at the query.** At `WM_QUERYENDSESSION`, if render
was not ACTIVE then, the ordinary at-rest block described above already
runs, and `WM_ENDSESSION`'s hand-back waits for that same block rather than
sending a second one. Render is read again at `WM_ENDSESSION` regardless of
what the query found, so audio started in between is still disconnected. If
the block being reused comes back Failed, or Partial in a way the gate is
not still running, it is sent once more before the reply returns, inside the
same shared deadline, and the log names the status that triggered the retry.

A step that throws instead of returning a result, the disconnect call or the
block call itself, is never a silent catch: its raw code is recorded and
logged, and the procedure still moves on to the block where it can.

**What this deliberately does not do.** Nothing runs when Earshot is not
running, whether it was closed, crashed, or never started; the boot-time
block described above is the only fallback for that case, and it can lose
the race. A permanent background service that could hand back the AirPods
even without the tray running was considered and designed, but is not
something built.

**The log lines**, all written by one formatter so nothing here drifts from
what a reader, or a live-test script, actually parses:

- `Hand-back (shutdown): started at ...` and `Hand-back (sleep): started at ...`
- `Hand-back (shutdown): nothing to disconnect`, or `disconnect ...,
  confirmed after ... ms` / `not confirmed within ... ms`
- `Hand-back (shutdown): block sent at ...`, or `block not sent: <reason>`
- `Hand-back (shutdown): the block queued at the query was <status>, so it is
  sent once more`
- `Hand-back (shutdown): finished in ... ms; disconnect ...; block ...`
- `Hand-back (shutdown): cut short at ... ms; still running: ...; block was
  sent at ... ` / `not sent`
- `Hand-back (resume): connected at resume; the resume check did not hold`
- `Hand-back (resume): the nodes were enabled and not in use, so they are
  blocked now`
- `Hand-back (resume): nothing to do: <reason>`
- `Hand-back: off, so nothing runs for this session end.` / `... suspend.`
- `Session ending: no block issued at the query, because the AirPods are in
  use and Hand back is on; the hand-back runs when Windows confirms the
  session is ending.`

The sleep and resume messages arrive at the tray's existing hidden window
without any new registration: it already receives broadcast messages such as
`WM_SETTINGCHANGE`, and `WM_POWERBROADCAST` reaches it the same way.

### Audio quality protection

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
it runs; it runs through a SYSTEM task rather than in the tray. The change
can also lapse: reconnecting or restarting can bring the Hands-Free service
back, so Earshot re-reads the installed services after a connect and after
boot and re-applies it when it has reverted, so it can take effect a moment
after a connect rather than instantly.

Connect can take the protection off for a moment: the one-shot reconnect used
to bring the AirPods back is a Hands-Free property, so turning Hands-Free off
also removes the filter that carries the request. If the A2DP filter refuses
it, the card says "Trying another way": Earshot turns the protection off,
connects, and turns it back on. The microphone works on this PC while that
runs, and it is not instant, because the change installs and removes profile
drivers. If the protection cannot be put back, the card says "Connected, but
audio quality protection did not apply." and the microphone stays available
until a later connect puts it back.

## The AirPods widget

For what the widget shows and its plain-English limits, see
[overview.md](overview.md#the-airpods-widget). This section is the
mechanism behind it.

**Reading the AirPods without connecting.** The widget never pairs or
connects to read battery, charging, lid or in-ear state: it runs a passive
[`BluetoothLEAdvertisementWatcher`](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.advertisement.bluetoothleadvertisementwatcher),
keeps only advertisements carrying Apple's manufacturer company ID (0x004C),
and looks for the proximity-pairing message AirPods and other Apple
accessories broadcast in the clear. The message's documented layout, not its
bit-level meaning, is described by the furiousMAC Continuity project's notes
(github.com/furiousMAC/continuity, messages/proximity_pairing.md) and by
Celosia and Cunche, "Discontinued Privacy", PETS 2020
(petsymposium.org/popets/2020/popets-2020-0003.pdf). Nothing from either
source is copied into this repository; both are cited by URL only. The
watcher is read-only throughout: nothing it does ever writes to a Bluetooth
device, and it is stopped and restarted around sleep.

**Phase 0.** Before the widget can trust any of that message's fields, one
capture has to prove what this hardware actually sends: a short, one-time
recording, taken with the owner's AirPods and his phone's own battery
reading side by side, so the decoded fields can be checked against a known
answer. That recording has not been made yet. Until it has, the message's
battery, charging and lid fields are held as unproved, and the widget shows
nothing derived from them: see
[overview.md](overview.md#the-honest-state-today).

**Whose AirPods it shows.** A room can hold several sets of the same model.
`OwnershipRule` decides, on every advertisement, whether it is the owner's:
model and colour bytes must match a one-time claim made when he opens his
own case by the PC; the signal must clear the strength recorded at that
claim; and the battery must be consistent with the last reading held for
him, where "consistent" means the same, lower, or exactly one 10% step
higher, and higher by more than that only while the matching charging bit is
set. A live connection to this PC does not shortcut any of these checks; the
same rule runs every time. Anything that fails is counted and nothing else
is recorded about it. This is an owner decision, not an oversight: a
same-model stranger with a lower battery reading than the owner's last one
can pass the rule, and he chose to accept that risk rather than tighten it
and risk the widget missing his own AirPods.

**The taskbar gauge.** Windows 11 removed the deskband API that used to let
a program dock a control into the taskbar, and has no replacement for it, so
there is no supported way to do this. The gauge is an owned, topmost,
layered overlay window positioned over free taskbar space, which it finds by
reading the taskbar's own button layout through UI Automation and polling it
for changes; the window's alpha-zero pixels let a click reach the taskbar
underneath rather than the gauge
(https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#layered-windows).
If there is no free space, or reading the taskbar fails, or the window
cannot be shown, the gauge hides itself and the ordinary tray icon takes
over automatically. It re-measures and re-attaches after Explorer restarts,
on the documented `TaskbarCreated` broadcast
(https://learn.microsoft.com/en-us/windows/win32/shell/taskbar). Because
this is unofficial, a Windows update to the taskbar's own layout could break
it; the tray icon fallback is what keeps the widget usable if that happens.

**The card and the case-open card.** A borderless window with rounded
corners and Windows' own translucent card backdrop, applied through the
documented DWM extended-frame call
(https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmextendframeintoclientarea),
following the system's light or dark theme; on a Windows build too old for
that call, or if it fails, the card falls back to an opaque colour instead
of the translucent one. It opens above the gauge, closes
when it loses focus, and works from the keyboard. The case-open card is the
same window in a separate, unfocused instance: Windows' own case-open event
shows it, it reads its own dismiss time from
[`SystemParametersInfoW(SPI_GETMESSAGEDURATION)`](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow)
so it follows the owner's own accessibility setting, and its Connect or
Disconnect button only ever fires on a genuine click on that button; nothing
else in its code path can press it.

**Probe target.** `Earshot.exe probe widget --out <folder>` renders the
gauge, the card and the case-open card from fixed, synthetic snapshots, at
three DPIs and in both taskbar inks, straight to PNG files, the same
drawing and layout code the real widget uses. No device, no window shown on
screen, and no `IWidgetStatus` connection: see
[docs/overview.md](overview.md#the-airpods-widget) for what the pictures
made this way actually show and do not show.

### What still needs a kernel driver

Not built, and not close to being built, on Windows without one:

- Noise control (active noise cancellation, transparency, adaptive audio).
- Conversational awareness.
- Battery read to the nearest 1%, rather than the 10% steps the
  advertisement carries.
- The AirPods' own press-and-hold button settings.
- Personalised volume.
- Renaming the AirPods.
- The hearing features.
- Real-time in-ear detection, if phase 0 shows the advertisement cannot
  give it while playing from this PC; that is still an open question.

**Why.** These all go through Apple's own accessory protocol, carried over a
Bluetooth L2CAP channel at a fixed PSM (0x1001), not through anything in the
advertisement the widget already reads. Microsoft's own documentation for
opening an L2CAP connection to a remote device,
["Creating a L2CAP Client Connection to a Remote Device"](https://learn.microsoft.com/en-us/windows-hardware/drivers/bluetooth/creating-a-l2cap-client-connection-to-a-remote-device),
is written for a kernel-mode Bluetooth profile driver, not for an ordinary
Windows program; nothing in the Windows SDK opens this kind of channel from
user mode.

**What building one would cost.** An unsigned kernel driver only loads once
Windows' test-signing boot option is turned on
(https://learn.microsoft.com/en-us/windows-hardware/drivers/install/the-testsigning-boot-configuration-option),
commonly called Test Mode, which weakens the system's own code-integrity
guarantees and is why kernel-level anti-cheat such as FACEIT refuses to run
at all while it is on. Getting a driver trusted without Test Mode means
signing it through Microsoft's own driver programme: since the April 2026
Windows update, Windows no longer trusts a kernel driver signed only through
the older cross-signing route by default
(https://techcommunity.microsoft.com/blog/windows-itpro-blog/advancing-windows-driver-security-removing-trust-for-the-cross-signed-driver-pro/4504818).

**On top of the cost, one more limit.** No application reviewed for this
project, on any platform, shows the name of the device the AirPods are
actually connected to when that device is not the one asking; the widget's
"On your iPhone" is the owner's own label, never a name read off the
AirPods, for the reason above.

## The safety model

- **Setup asks for one administrator prompt and does the rest itself.** It
  copies the folder to `C:\Program Files\Earshot` and checks every copied
  file against the SHA-256 recorded in `Earshot.files.json`, the list the
  release build writes. Only administrators can write to Program Files, so
  the program the scheduled tasks run later cannot be swapped for another
  one; a file that does not match stops the install.
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
- **The device identity is read from `device.json`, never from whoever
  started the task.** Each task accepts a fixed short list of commands, and
  the tray reads the real device state back itself afterwards rather than
  trusting the task's own result.
- **Setup never changes your pairing**, and never touches the registry `Run`
  key. Your own settings stay in `%APPDATA%\Earshot\settings.json` and the
  log in `%LOCALAPPDATA%\Earshot\logs`, separate from the machine-wide state
  in `ProgramData`, because the elevated worker runs with nobody signed in
  and cannot read your profile.
- **A device that cannot play audio from this PC is refused**, a phone for
  instance, with the reason stated: "That device cannot play audio from this
  PC. Choose headphones or speakers."
- **Two environment variables keep testing off the real device.**
  `EARSHOT_DATA_ROOT` moves every data folder elsewhere, and
  `EARSHOT_SAFE_MODE` turns every device action off so only reads happen.
  `install`, `uninstall`, `gate` and `gate-protect` refuse to run while
  either is set. A `diag` target is refused in safe mode only, so
  `EARSHOT_DATA_ROOT` on its own does not stop a device action; the live
  test scripts stop the run themselves when it is set, rather than let a
  real device action write its evidence somewhere else.

## Setup and removal

Setup is the one-time flow started from **Set up Earshot...** in the tray
menu: one administrator prompt, then Earshot copies itself into Program
Files, verifies every copied file, and registers the three scheduled tasks
described above. Connect, disconnect and audio protection all work without
setup; only the boot block needs it, because disabling a device node needs
administrator rights.

To remove Earshot, turn **Open on startup** off in the tray menu first, then
close Earshot. The startup registry value belongs to the tray, and uninstall
does not touch it; Earshot removes a value left behind the next time it
starts with the setting off.

Then run, from an administrator PowerShell:

    & "C:\Program Files\Earshot\Earshot.exe" uninstall

PowerShell needs the `&` to run a quoted path; in Command Prompt, leave it out.

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

Test 15, which exercises setup and this reversal live, has not yet run; the
elevated launch site it covers has never been exercised. See
[verification.md](verification.md).

## Command line

One program, `Earshot.exe`, chosen by its first argument.

| Command | What it does |
|---|---|
| `Earshot.exe` | The tray application. `--startup` is the same thing, and is what the startup value passes. |
| `Earshot.exe probe [audio\|topology\|nodes\|services\|task\|battery\|all] [--json] [--out <path>]` | Read-only diagnostics. Reads endpoints, walks the audio topology, reads the device nodes, lists the installed Bluetooth services, reads the scheduled tasks, and reports the battery answer described in [requirements.md](requirements.md). It changes nothing. On a machine where Earshot is not set up and no device is pinned it still writes a full report, and exits 78 to say so: the nodes and services targets had no device to read. So read the report rather than the exit code. |
| `Earshot.exe probe icon --out <folder>` | Writes the tray icon to files, in each of its four states, at three screen scalings and in both inks, for checking how it looks. |
| `Earshot.exe probe widget --out <folder>` | Writes the taskbar gauge, the card and the case-open card to files, from fixed synthetic snapshots, at three DPIs and in both taskbar inks. See [The AirPods widget](#the-airpods-widget). |
| `Earshot.exe install <userSid> <address> <containerGuid> [--principal user]` / `Earshot.exe uninstall` | The one-time setup and its removal. Both need an elevated administrator and refuse to run as SYSTEM. The menu runs `install` with those three arguments filled in; a bare `install` is refused, so it is not a command to type by hand. |
| `Earshot.exe gate <verb> <nonce> [address]` / `Earshot.exe gate-protect <verb> <nonce>` | The elevated workers: one for the device nodes, one for the Bluetooth services. Started by Earshot's own scheduled tasks, not by hand. |
| `Earshot.exe diag <target>` | Single live actions for testing on real hardware: connect, disconnect, a raw driver request, a gate run, the unelevated service call, and the battery sweep. **All but the battery sweep and `gate status` change the state of the device**; those two only read. They exist for the live tests in `tools\live-tests` and are not part of normal use. |
