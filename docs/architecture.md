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

With **Hand back on shut down, sleep and Exit** on, Earshot releases the
AirPods and blocks their device nodes again before the session actually ends,
the machine actually sleeps, or Exit closes the tray, so this PC does not take
them back the moment it restarts or wakes. The setting is off by default: the
tray's `HandBackOnShutdownAndSleep` and the service's `HandBackAtShutdown` both
read as off when absent. Until it is ticked, Exit while the AirPods are in use
closes Earshot without blocking them, and the service does nothing at shut
down. The setting is in the tray menu and on the widget card's settings page.

**Where it runs.** Entirely inside the tray. For shut down, restart and
sign-out and for sleep it runs on the real Windows messages the tray's hidden
window already receives: `WM_ENDSESSION` (with the reply held open) and
`WM_POWERBROADCAST` with `PBT_APMSUSPEND`. For Exit it runs as the block
before closing, by the same procedure. Nothing runs unless the tray gets the
message or the click, which is why the tray has to be running for any of this
to happen. On Exit, a disconnect that outlasts its share of the cap does not
stop the block: the block is sent anyway, because the block is what keeps this
PC off the AirPods at rest. Exit with the setting off, or with the AirPods not
connected to this PC, is the ordinary Exit.

**The fixed order**, the same for every trigger:

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
| Exit from the tray menu | 4 seconds, the shut-down cap (Exit has no Windows deadline, so it borrows the longest existing one) |

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
the race. The one exception is the hand-back service described next, which
blocks the AirPods at shut down when the tray is not running.

**The hand-back service.** `EarshotHandBack` runs as the local system account,
starts with Windows, and accepts only stop, interrogate and pre-shutdown
notices from Windows. It opens no pipe, socket or window, and standard users
can query it but not control it. On start it checks that it is running as the
system account, that its image is inside the install folder, and that the
install folder is not writable by standard users. The machine settings folder
is checked and logged at start, and checked again for real at pre-shutdown.
When a start check fails the service logs why and stops at once, reporting
service error 1066 (`ERROR_SERVICE_SPECIFIC_ERROR`) with the failure's own
code; it does not stay idle. At pre-shutdown it reads the settings and the
device from the machine folder only. It acts only when Block at boot and Hand
back are both on, and Hand back reads as off when it is absent, so a fresh
install does nothing at shut down until Hand back is ticked. If either is off,
or the AirPods are already fully blocked, it makes no call. It never
disconnects. Otherwise it blocks them through the
same routine the boot-time task uses, retries a vetoed node once when there is
room, and writes a status file with each node's code.

It does not disconnect. That is a design choice, not a fact about Windows: the
tray's disconnect runs in the signed-in session, and a disconnect from a system
service was not tried, so whether it would help with a node a driver refuses is
unproved. The work is held to 8,000 ms of the 10,000 ms Windows allows. The two
lock waits together take at most 2,500 ms, so that a block as long as the one
sample on record (about 5.5 s, when a driver refused a node while the AirPods
played) still ends inside 8,000 ms, and a second try of a refused node is made
only when its delay and a block of that length still fit. The 5.5 s is a single
sample, not a limit; a driver slower than that would run the work past its
budget. Before it writes its status file the service holds the machine folder
open, so the folder cannot be renamed, deleted or replaced by a link while the
file is written, and it checks the folder's owner and access list again on that
open folder. The tray tells it about the Hand back tick by sending the setting to
the same routine that writes the machine settings.

**Threat notes for the service.** The service runs from the install folder, and
only administrators can change that folder, the service's registration or its
access list. What the code controls is the search order of the libraries it
loads by name once it is running: the system folder and its own folder, never
the working directory or the PATH. It does not control what the host and the
runtime load before that, and those loads may use the machine PATH. If a folder
on the machine PATH can be written by a standard user, the install folder's
access list does not protect that step. Nobody has checked what is loaded at
that step, so nothing here claims it is safe; a PC whose machine PATH lists only
folders that administrators can write does not have the gap.

**The log lines**, all written by one formatter so nothing here drifts from
what a reader, or a live-test script, actually parses:

- `Hand-back (shutdown): started at ...`, `Hand-back (sleep): started at ...`
  and `Hand-back (exit): started at ...`. The lines below are written with
  the prefix of the trigger that raised them; the resume lines have their own
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
can also lapse: reconnecting or restarting is reported to bring the Hands-Free
service back, so Earshot re-reads the installed services after a connect and after
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

**Battery set-up and proof.** The message's battery, charging, in-ear and lid
fields are held as unproved until the owner's own set-ups prove them. **Set up
battery** (on the card and in the menu) listens for 20 seconds while the owner
opens the case by the PC. It picks the one sender it treats as the case: the
strongest by median signal, with at least three messages, and ten decibels
clear of the next. It sets the signal threshold ten decibels under that
sender's weakest message. Those three figures are design choices held as named
constants (`SetupRules`), not facts about the device. The owner then answers
three pickers (left bud, right bud, case, in steps of 10) and a Charging
toggle for each, to match what the iPhone shows.

What one set-up saw is kept as a record under `%LOCALAPPDATA%\Earshot\widget`,
written once and never replaced. A message of the documented form is kept as
its first nine bytes only. There is no field for a device address, a sender
tag or a name. The picker values are evidence for `DecodeProof` and are never
shown as a reading.

`DecodeProof` works out, from the records alone, which fields can be read, and
its result is saved as the proof. Every rule needs its evidence twice:

- **Bud order and the case nibble** each need two records that agree with the
  owner's picks (within one 10% step, since the iPhone's rounding is not
  established). A record that disagrees withdraws what an earlier pair
  proved. A status bit that flips the bud order needs four discriminating
  records.
- **A charging bit** is proved only when it equals the owner's flag in every
  record, the flag varies, and no other bit or part varies the same way.
- **In-ear and lid are marked not provable by set-up**, because three battery
  pickers carry no truth about ears or the lid. Nothing then decodes them, so
  ear detection, auto-pause and the case-open card stay off, and the settings
  rows for them say "Earshot cannot yet tell...".

Only fields the proof holds as proved reach the snapshot. A reading older than
one hour counts as no recent reading. A claim is tied to its set-up record.

**Whose AirPods it shows.** A room can hold several sets of the same model.
`OwnershipRule` decides, on every advertisement, whether it is the owner's:
model and colour bytes must match the claim a set-up made; the signal must
clear the strength recorded at that claim; and the battery must be consistent
with the last reading held for him, where "consistent" means the same, lower,
or exactly one 10% step higher, and higher by more than that only while the
matching charging bit is set. A live connection to this PC does not shortcut
any of these checks; the same rule runs every time, and re-syncing after a
jump the rule cannot explain is a re-claim (opening the case by the PC).
Anything that fails is counted and nothing else is recorded about it. This is
an owner decision, not an oversight: a same-model stranger with a lower
battery reading than the owner's last one can pass the rule, and he chose to
accept that risk rather than tighten it and risk the widget missing his own
AirPods.

**The taskbar gauge.** Earshot does not use a taskbar docking API. The gauge is
an owned, topmost, layered overlay window positioned over free taskbar space, which it finds by
reading the taskbar's own button layout through UI Automation and polling it
for changes; the window's alpha-zero pixels let a click reach the taskbar
underneath rather than the gauge
(https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#layered-windows).
It draws the earbud mark with a ring in the Windows accent colour, filled to
the lower proved bud's battery, and that number.

The setting **Gauge position** chooses between two placements. At the right
end (the default), its right edge sits 8 pixels (scaled for DPI) left of the
notification area. Next to the apps, its left edge sits 4 pixels after the
last button that is not part of the notification area. Either way the result
must not touch anything already there and must stay inside the taskbar, or
there is no placement and the tray icon stays. A left or right taskbar is not
supported.

A change of the foreground window, which Start, a flyout, a taskbar click and
a full-screen application closing all cause, is watched by one read-only
`SetWinEventHook` for `EVENT_SYSTEM_FOREGROUND`
(https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwineventhook).
When something covers the gauge, it raises itself again, and the poll finds
anything the hook missed. Raises are rate limited. Every hide, show, cover and
raise is logged on a line starting `Gauge `, with a reason and the covering
window's class only, never its title. One failed taskbar read keeps the gauge
where it is; it hides only after three in a row. If there is no free space,
or the window cannot be shown, the gauge hides itself and the ordinary tray
icon takes over automatically. It re-measures and re-attaches after Explorer
restarts, on the documented `TaskbarCreated` broadcast
(https://learn.microsoft.com/en-us/windows/win32/shell/taskbar). Because
this is unofficial, a Windows update to the taskbar's own layout could break
it; the tray icon fallback is what keeps the widget usable if that happens.

**The card and the case-open card.** A borderless window with rounded
corners and Windows' own translucent card backdrop, applied through the
`DWMWA_SYSTEMBACKDROP_TYPE` window attribute
(https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmsetwindowattribute)
and the documented DWM extended-frame call
(https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmextendframeintoclientarea),
following the system's light or dark theme; on a Windows build older than
22621, or if either call fails, the card falls back to an opaque colour
instead of the translucent one. It opens above the gauge, closes
when it loses focus, and works from the keyboard. The case-open card is the
same window in a separate, unfocused instance: the case-open event shows it
(which needs the lid state, so it stays off until that is proved), it reads its own dismiss time from
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

**The settings page.** The gear on the card opens a page drawn by the card
itself, with no child controls. Its rows are read from the real settings each
time it is drawn, so a row shows what is saved and never what was last asked
for. Every change goes through the same path the tray menu's own item uses.
The rows are, in order: Gauge position, Other device, Pause when a bud comes
out, Pause when AirPods leave this PC, Case-open card, Low battery alert (the
threshold), Left click connects, Hand back on shut down, sleep and Exit; then
the shortcuts for Connect and Disconnect; then Check for updates and Check
automatically. The two rows for features that need in-ear or lid proof carry
a caption saying Earshot cannot yet tell, while that proof is missing.

### Shortcuts and switch timing

Shortcuts use `RegisterHotKey` through `Earshot.Hotkeys`. Two are on by
default: Ctrl+Alt+Shift+A switches to this PC (the connect) and
Ctrl+Alt+Shift+D switches to the phone (the disconnect). A chord Windows
refuses because another program holds it is recorded as that shortcut's
registration outcome, and the card and the settings row read it. Both
shortcuts enter the same toggle path as a click, so every guard the click has
applies unchanged. A press asks for an end state: the same direction as the
one in flight changes nothing, and the other direction replaces it, so the
last press wins.

`HotkeySettings` reads an older settings file so that a chord the owner typed
is kept, a file that never held one gets the two defaults, and a file that had
shortcuts off with no chord typed reads as on. `System.Text.Json` calls a
property's setter only for a member the file contains, which is how the code
tells a file that predates the two defaults from one that has them.

Every switch the tray starts is timed by `SwitchTimeline` on one monotonic
clock and written by one formatter (`SwitchTimelineText`) as a `Switch to-pc:`
or `Switch to-phone:` line. A connect line carries its phases (queued,
first-pass, status, allow, endpoints, connect, protection), its path and, when
it did not reach ACTIVE, whether the nodes are blocked again. A disconnect
line carries the release, whether the PC is at rest, and the block. A cancelled
switch says so. Test 16 reads these lines; none has been measured on a live
run yet.

### Pause when the AirPods leave this PC

`PauseOnLeave` pauses playback when the AirPods stop being this PC's output
while this PC was playing to them. It reads the AirPods' audio activity before
Earshot's own disconnects (Disconnect, the hand-back at shut down, sleep or
Exit, a fast switch) and pauses first, then lets them go. For any other leave,
such as the phone taking them, it acts as soon as it sees the change, on the
last reading taken. It pauses through Windows Media Controls
(`SessionPause`), which does not say which output a session renders to, so it
pauses the one session that is playing whichever output it uses, and none when
two or more are playing. It never plays and never resumes. Each decision is
one `Pause on leave:` line with its reason. No live run yet.

### Updates

The update source (`UpdateService`) reads the latest release of the project's
GitHub repository. The feed address is a compile-time constant: nothing a user
can write reaches it or a download address. Every address is HTTPS, redirects
are followed by hand and each hop is checked, and no credential is sent. A
check downloads nothing.

Only a click on Update starts a download, and only an installed copy offers
it, because only that copy can hand over safely. The zip is checked against
the `.sha256` file the release publishes beside it before anything is
unpacked, and a failure at any step deletes what was staged. The tray then
starts the installed `Earshot.exe` with the update verb, which asks for one
administrator prompt. That run copies the zip into a folder only
administrators can write, hashes the copy, goes on only if the hash matches
the one recorded at download, and installs from there. The staging folder is
writable by the signed-in user, which is why the check is repeated in a folder
that is not.

What the checksum protects against: a damaged or cut-short download, and a
file that differs from what the release lists. What it does not protect
against: a compromised release or account, because the checksum comes from the
same release, and the app is not signed. **Check automatically** is off by
default and checks at most once a day, the first a little after startup; a
check that fails is not retried until the next day.

### Where settings and data live

| Where | What |
|---|---|
| `%APPDATA%\Earshot\settings.json` | The owner's settings |
| `%LOCALAPPDATA%\Earshot\logs` | The log |
| `%LOCALAPPDATA%\Earshot\livetest` | Live-test evidence |
| `%LOCALAPPDATA%\Earshot\widget` | The widget's claim, set-up records and proof |
| `%ProgramData%\Earshot` | Machine files the elevated tasks and the service read: `device.json`, `config.json`, `protection.json`, `protection-intent.json` and the per-run status files |

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
- Real-time in-ear detection and the lid state. Battery set-up cannot prove
  either, so they stay off; whether the advertisement can give them at all is
  still an open question.

**Why.** These all go through Apple's own accessory protocol, carried over a
Bluetooth L2CAP channel, not through anything in the
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
guarantees. Getting a driver trusted without Test Mode means
signing it through Microsoft's own driver programme: since the April 2026
Windows update, Windows no longer trusts a kernel driver signed only through
the older cross-signing route by default
(https://techcommunity.microsoft.com/blog/windows-itpro-blog/advancing-windows-driver-security-removing-trust-for-the-cross-signed-driver-pro/4504818).

**On top of the cost, one more limit.** Earshot does not read the name of the
device the AirPods are connected to; the widget's "On your iPhone" is the
owner's own label, never a name read off the AirPods.

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
  (Block at boot, and the hand-back setting the service reads), `protection.json` and `protection-intent.json` (which
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
menu. The item is offered before setup, for a damaged install, and when the
running copy is newer than the installed one. It asks for one administrator
prompt, then Earshot copies itself into Program
Files, verifies every copied file, registers the three scheduled tasks
described above, and installs the hand-back service. Connect and disconnect do
not need setup. Block at boot and Protect audio quality do, because both
change the device through Earshot's SYSTEM tasks.

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
task folder, removes the hand-back service, removes `C:\ProgramData\Earshot`, and removes
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
| `Earshot.exe update <zip> <sha256> <pid> <userSid> <address> <containerGuid>` | The elevated half of Update. Started by the installed Earshot after the one administrator prompt, not by hand. See [Updates](#updates). |
| `Earshot.exe service` | The hand-back service's run mode. Started by Windows from the service's registration, not by hand. |
| `Earshot.exe gate <verb> <nonce> [address]` / `Earshot.exe gate-protect <verb> <nonce>` | The elevated workers: one for the device nodes, one for the Bluetooth services. Started by Earshot's own scheduled tasks, not by hand. |
| `Earshot.exe diag <target>` | Single live actions for testing on real hardware: connect, disconnect, a raw driver request, a gate run, the unelevated service call, and the battery sweep. **All but the battery sweep and `gate status` change the state of the device**; those two only read. They exist for the live tests in `tools\live-tests` and are not part of normal use. |
