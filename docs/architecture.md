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
them back the moment it restarts or wakes. The setting is on for a new
install: a PC with no `settings.json` and no `settings.json.bak` gets it on
(and Open on startup on) the first time Earshot starts, and the tray then
copies the choice to the service's `HandBackAtShutdown`. It is never turned on
for an existing install. A settings file that already exists keeps whatever it
holds, and one written before the setting existed has no member for it, which
reads as off, as does a file that was unusable and reset to defaults. While it
is off, Exit while the AirPods are in use closes Earshot without blocking
them, and the service does nothing at shut down. The setting is in the tray
menu and on the widget card's settings page.

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
opens the case by the PC. Only senders of the documented message form are
candidates; senders of other forms (a nearby iPhone sends one) are kept as
evidence and never compete. The two buds of one set broadcast from two
addresses, with the bud values in swapped order, so senders with the same
model, colour and case value, and the same two bud values in either order
within two seconds, are merged into one set. It picks the one set it treats as
yours: the strongest by median signal, with at least three messages, and ten
decibels clear of the next set. It sets the signal threshold ten decibels under
that set's weakest message. Those three figures are design choices held as named
constants (`SetupRules`), not facts about the device. The owner then answers
three pickers (left bud, right bud, case, in steps of 10) and a Charging
toggle for each, to match what the iPhone shows.

What one set-up saw is kept as a record under `%LOCALAPPDATA%\Earshot\widget`,
written once and never replaced. A message of the documented form is kept as
its first nine bytes only. Each sender is named by a tag, a hash of its
address under a key made for that listen and never stored, so no address is
kept. There is no field for a name. The picker values are evidence for `DecodeProof` and are never
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

The setting **Gauge display** chooses which display's taskbar holds the gauge.
It is stored as the monitor's device interface name, the path Windows registers
for `GUID_DEVINTERFACE_MONITOR`, which `EnumDisplayDevices` returns in
`DeviceID` when called with `EDD_GET_DEVICE_INTERFACE_NAME`
(https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumdisplaydevicesw).
It names the monitor and the output it is on, not its place in the
enumeration, so it survives a restart and a change of which display is
primary. A local read of two monitors of one model found paths that differ
only in the output's id at the end, so the whole path is compared. An empty
value means the main display. Displays are listed with `EnumDisplayMonitors`,
each with its bounds and work area from `GetMonitorInfo` and its own scale
from `GetDpiForMonitor`.

The display is named "Display N" from the number in its GDI device name (`\\.\DISPLAYN`).
Settings numbers displays by its own rules and may show another number for the
same display, so the name is Earshot's, and the settings page adds the
resolution. The page lists the displays as they are each time it is drawn, is
redrawn on `WM_DISPLAYCHANGE` while it is open, and does not store a choice of
a display that is no longer connected.

The main display's taskbar is `Shell_TrayWnd`; every other display has a
`Shell_SecondaryTrayWnd`. The reader (`UiaTaskbarReader.ResolveTarget`) picks the visible one whose window is on
the chosen display's monitor (`MonitorFromWindow`,
https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-monitorfromwindow)
and measures it through UI Automation exactly as it measures the main one. A
local read of a secondary taskbar showed the clock under the same `SystemTray.`
class names the main taskbar's notification area uses, so the right-end
position is measured from it with the same 8 pixel gap. A secondary taskbar
with no clock has no notification area, and its own right edge is the end. The
gauge size comes from the chosen display's own scale (100, 125 or 150%). A
secondary taskbar window whose rectangle cannot be read (`GetWindowRect`) is left
out of the list and the first such failure is carried to the log with its raw
code, so a display whose taskbar was skipped for that reason is not mistaken for
one that shows none.

Wiring: the tray passes the chosen display to the taskbar watcher, the watcher
passes it to the reader on every read, the reader resolves it (falling back to the
main display when the chosen one is gone or shows no taskbar), and the tray gives
the gauge controller its foreground-window reader for the full-screen rule. Each
link has a test through the real tray (`GaugeDisplayWiringTests`), with only the
display list, the taskbar windows, the foreground window and the UI Automation
read replaced.

If the chosen display is not connected, or is connected but its taskbar is not
shown, the reader returns the main display's taskbar and says why in the
layout; the controller writes one line when the reason changes, and another
when the display returns. `WM_DISPLAYCHANGE` and `TaskbarCreated` already ask
for an immediate read, so the return is seen at once. The card and the
case-open card take the work area of the display the gauge is on.

The two full-screen signals, `ABN_FULLSCREENAPP`
(https://learn.microsoft.com/en-us/windows/win32/shell/abn-fullscreenapp) and
`SHQueryUserNotificationState`
(https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shqueryusernotificationstate),
are global: neither carries a window or a monitor, and the appbar message goes
to every appbar. So they are triggers to look, not the verdict. The gauge
hides for them only when the foreground window is on the gauge's monitor and
its rectangle covers that monitor's full bounds. An appbar notice that arrives
before the foreground has moved stays pending until a foreground change, a
closing notice, or a poll that finds no full-screen state. With one display the
signal stands alone, as before; with several and a foreground window that
cannot be read, the gauge hides, as before. Presentation settings hide it on
every display. A hide writes the class and the display the full-screen window
was on, never a title.

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

**Type and text size.** Text is Segoe UI Variable in the Windows 11 type ramp
(https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/typography):
12 on 16 for captions, 14 on 20 for body text, semibold for titles and section heads,
and 20 on 24 semibold for the battery figure. GDI+ cannot pick the font's optical size on its
own, so each style names one of the font's own instances (Small, Text or Display), found by name
among the installed families (GDI+ cuts family names at 31 characters); where one is missing the
card falls back to Segoe UI, then to the system message font. Every size is multiplied by
Settings > Accessibility > Text size
([`UISettings.TextScaleFactor`](https://learn.microsoft.com/en-us/uwp/api/windows.ui.viewmanagement.uisettings.textscalefactor),
1 to 2.25), and the heights of the rows that hold text grow with it while widths and paddings follow
the display scale only. The card keeps its 360 epx width, so a row whose control would squeeze its
label puts the control under the label, and text that still does not fit ends in an ellipsis. The
gauge is the exception: its width is fixed and its number sits in a 22 px slot, so its type does not
follow the text size. Whether `TextScaleFactorChanged` is raised in a process with no core window is
not documented, so an open card also reads the look afresh on every `WM_SETTINGCHANGE` and every
show.

**Theme, accent, contrast and transparency.** The theme, the accent, a high-contrast theme and the
Transparency effects setting are followed while the card is open. On a settings change the card takes
the taskbar's ink again, re-applies the dark-mode attribute, reads the look and draws again, keeping
its bottom edge. With Transparency effects off, or under high contrast, it paints its own opaque
colour over the whole window instead of leaving it clear for the backdrop; Windows shows a solid colour
there itself, but painting it makes the result the same on every build.

**Motion.** Taskbar flyouts slide up when they are invoked and down when they are dismissed
(https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/motion). The card enters
over 250 ms and leaves over 167 ms on the page's cubic-bezier(0, 0, 0, 1) curve, fading in over 83 ms
and out over its 167 ms, and travels one taskbar thickness (read from where the gauge sits relative to
the work area; 48 px at 100% when that is not known), away from the taskbar's edge. The curve is
`3t^(2/3) - 2t`, which the tests use as an oracle. The motion is a pure function of the elapsed time,
driven by a timer on the tray's clock and tested on a fake one. With Windows' Animation effects off
(`SPI_GETCLIENTAREAANIMATION`, read at each show and hide) nothing moves and nothing fades. The fade is
a constant window opacity (a layered window with `SetLayeredWindowAttributes`), and Microsoft does not
say whether the system backdrop and the rounded corners survive that style, so there is one switch,
`CardMotion.UseAlphaFade`, that turns the fade off and drops the layered style if they do not.

**Focus.** No control draws a dotted focus rectangle. Keyboard focus draws the Windows 11 focus visual:
a gap of 1 px, a 1 px inner stroke and a 2 px outer stroke round the control, scaled with the display, in
the colours of Microsoft's WinUI theme resources (white over black at 70% on dark, black at 89% over white
at 70% on light, the system's own window text and window colours under high contrast). It shows only for
keyboard use: from a Tab, an arrow key, Home or End, or when the card was opened from the keyboard, and
a mouse press hides it. The dialogs use a button that draws the same visual just inside its own bounds,
since a child control cannot paint outside itself, and a list view that keeps its own item rectangle
hidden.

**Icons and words.** The icons are Segoe Fluent Icons, from the system font and never bundled, and a
Segoe MDL2 Assets glyph stands in on a PC without it. A settings row is an icon and one to three words,
the old description is its tooltip, and the card says less where an icon will do (a pin and the place, a
clock and the age, a download arrow and the version). Every icon-only control has a tooltip and an
accessible name, and the card describes its current controls to assistive technology as children in the
keyboard's order.

**Gauge order.** The setting **Gauge order** lays the ring (with the earbud mark inside it), the number
and the charging bolt in any of their six orders. The window, its padding and each piece's width stay
the design's, so the gauge never changes size and the bolt's slot is always kept. The gap the default order
leaves between ring and number goes next to the ring when it is at an end, and half each side when it is in
the middle. The number sits against the ring when it is next to it, and otherwise against the window's outer
edge, so it never meets the bolt. The settings page shows each order as a picture of the gauge itself, drawn
by the same renderer, with a neutral bar where there is no number and an outlined bolt when it is not
charging; choosing one changes the gauge on the taskbar at once.

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
The rows are, in order: Position, Display (a button that steps through
Main display and the connected displays), Order (six pictures of the gauge),
Other device, Pause on removal, Pause on leave, Case card, Low battery (the
threshold), Click connects, Hand back; then the shortcuts for Connect and
Disconnect; then the installed version with Check, Repair when an install
exists, and Auto check. Each is an icon and its words; what each row does is
its tooltip. The two rows for features that need in-ear or lid proof carry
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

Only a click on Update starts a download. Any running copy can offer it once
Earshot is installed: the tray checks that the installed `Earshot.exe` is there
and that the install folder grants no one but administrators write, which is the
check the elevated run repeats, and then the update is handed to the installed
program whichever copy is running. The elevated program is the installed
`Earshot.exe`, which is in a folder only administrators can change, never the
running copy. A copy that is not the installed one measures the update against
the installed version (the controller's installed version is read from the
installed file), because that is what the update replaces; a newer copy run from
a download folder is still offered the update that brings the install up to date.
The running copy's own process id is what the elevated
run waits on before it touches the install folder (it waits for any process named
`Earshot.exe` with that id, so a copy run from a download folder counts). With
nothing installed, or an install that cannot be used, the update card offers Set
up or Repair instead. The zip is checked against the `.sha256` file the release
publishes beside it before anything is unpacked, and a failure at any step
deletes what was staged. The tray then starts the installed `Earshot.exe` with
the update verb, which asks for one administrator prompt. That run copies the
zip into a folder only administrators can write, hashes the copy, goes on only
if the hash matches the one recorded at download, and installs from there. The
staging folder is writable by the signed-in user, which is why the check is
repeated in a folder that is not.

The order in the tray is: download and check, then the tray's own closing device
work, then the prompt, then the tray ends. The controller takes a
`beforeHandOver` step that runs between the download and the launch. For the tray
that step is `PrepareHandOverAsync`: it does what Exit does up to the point where
Exit ends (no more input, `BeginShutdown` with the hand-back plan, the coordinator's
block before closing with the same limits), waiting for everything in flight
except the update action itself. Only then is the elevated program started, and
the tray ends at the launch with no further device call (no second hand-back or
block): from the moment the closing work has run, `BlockCoordinator.EndDeviceWork`
makes a session end, a sleep or a resume do nothing on the device, so a sign-out
while the administrator prompt is open cannot send a hand-back or a block beside
the install. Before the closing work, with Hand back off and the AirPods in use
(`ClosingWouldLeaveAirPodsEnabled`), the card says they stay connected and are
blocked again at the next start, and the closing notice is kept up for
`ExitNoticeTime` before the launch, so the prompt does not cover it. The order matters for installed versions that do not wait for a copy run
from another folder to end: the installed 1.2.0 and the first published 1.2.1
ignore the process id, so an install started while the tray was still handing back
and blocking could replace files under it. A refusal or a failed launch after the
closing work cannot go back to the running tray, so the card says what happened
and the tray ends and starts itself again (only when it is not elevated; an
elevated tray would start an elevated one, so it says to start Earshot from the
Start menu). A tray that is already closing when the hand-over comes starts nothing.

One elevated operation at a time: the tray claims `setup`, `repair` or `update` on
the UI thread before anything awaits, and a second is refused with "Finishing the
repair first." (the menu items and the settings rows say the same while one
runs). Exit waits, up to 90 seconds, for the elevated program of a setup or repair
that is still running before it begins its hand-back and block, because Exit
otherwise cancels what is in flight, and the launcher stops waiting for a program
that keeps running, which let the block meet a scheduled task being registered
again. If the wait runs out, the card and the log say so. Between processes,
setup, update and repair take the machine-wide lock `Global\Earshot.Install.RunLock`
(`InstallRunLock`, the same kind of object as the gate's lock: a named mutex only
SYSTEM and Administrators can open, whose owner is checked). A run that cannot
take it stops before it changes anything, exits with `busy` (25), and logs
it; the install the update starts waits up to 10 seconds for the update run to end
instead of refusing. An object someone else created under that name is a failure,
not a held lock. Uninstall takes the gate's lock and not this one.

A copy that is not the installed one never writes itself into the Open on
startup Run value or the Start menu shortcut once an install exists (when the
installed program has gone missing they have no target at all, rather than this
copy). It offers a switch instead: this copy exits through the ordinary Exit
(which hands the AirPods back and blocks them when Hand back is on, and the
card says so before the button), and after its single-instance lock is released
the installed program is started with the signed-in user's own token. A tray
that is itself elevated offers no switch, since the token it would pass on is
elevated too.

### Repair

The elevated half of Repair is the `repair` verb of the installed
`Earshot.exe`. It is install's own repair run from the installed copy: it
refuses to run from any other folder, checks that the install folder grants no
one but administrators write, checks every file in the installed
`Earshot.files.json` against its SHA-256, and only then stops the hand-back service
and registers the machine configuration, the device file, the three scheduled tasks and
the hand-back service again, each step with its raw code. The folder and the files
are checked before the service is stopped, so a repair that finds a file damaged
leaves the service running. It records how it ended in
`update-outcome.json` in the machine folder, as an update does.

The tray decides the route from read-only facts (`RepairPlanner`): the same hash check of the
installed files, the folder check, and the installed file version. The rule is
that while the install folder exists and passes its check, the only program run
elevated is the installed `Earshot.exe`, which only administrators can change. The
routes are:

| What the tray found | Route |
|---|---|
| Every file matches | The installed program repairs itself: its repair verb, or its install verb, which every version runs from its own folder as the same repair, when the installed program is older than 1.2.2. The first published 1.2.1 has no repair verb and answers "Unknown command: repair" with exit 64, and a later build of 1.2.1 carries the same number, so the verb is assumed only from 1.2.2 (`RepairPlanner.RepairVerbSince`). |
| A file is missing or does not match, or there is no usable file list | The release of the installed version is read from the feed's tag route (the same feed, HTTPS and size rules as a check), downloaded, checked against its `.sha256` file, and handed to the installed program's update verb. That verb runs from the install folder, which only administrators can change, even when the installed `Earshot.exe` is itself one of the files that differs; what it installs is only the verified zip. |
| A file or the file list could not be read, or the install folder's permissions could not be read (`InstallProblem.FolderNotRead`) | Nothing is elevated and nothing changes. "Couldn't read the installed files ... Try again in a moment." The raw code (a sharing violation 0x80070020, say) is logged. Not treated as missing or as writable: a standard user can open an installed file with no sharing for as long as they like, which makes the hash read fail, and a missing file is one whose read says so (file or path not found). |
| Every file matches, but the installed version could not be read (held open, or no version in the file) | The installed program's install verb runs, as for any version below 1.2.2 (`RepairPlanner.Decide` treats an unknown version as too old for the repair verb). The log says which it was: a file that could not be read keeps its raw code, and a file with no version says it carries none. If a file is also missing or different, nothing is elevated, because the release to fetch is named by the installed version. |
| The running copy is newer than the installed one | Not elevated. Repair opens the update path (a check against the installed version, then Update). |
| `Earshot.exe` is truly missing (the folder lists without it), or its folder can be written by a standard user | The running copy's own setup puts a new install in place. A program that could not be found, where the folder could not be listed or lists the file, is not truly missing and is read as unreadable, and so is a folder whose permissions could not be read: only a folder read as writable by a standard user is this route. |
| Nothing is installed | Set up, not Repair. |

The same hash check, the same release
feed read and the same hand-over are what the tests run for real; only the
elevated run is faked.

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
  `install`, `uninstall`, `update`, `repair`, `gate` and `gate-protect` refuse to
  run while either is set. A `diag` target is refused in safe mode only, so
  `EARSHOT_DATA_ROOT` on its own does not stop a device action; the live
  test scripts stop the run themselves when it is set, rather than let a
  real device action write its evidence somewhere else.

## Setup and removal

Setup is the one-time flow started from **Set up Earshot...** in the tray
menu. The item is offered only when nothing is installed; once Earshot is
installed, in any state, **Repair Earshot...** takes its place (see
[Repair](#repair)). It asks for one administrator
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

### The install script

`installer\earshot.ps1` installs, updates, repairs and uninstalls Earshot from
a GitHub release, and is attached to every release as `earshot.ps1`. A person
runs `irm https://github.com/5Muawiyah/earshot/releases/latest/download/earshot.ps1 | iex`
in a normal PowerShell window and picks from a small menu. A tool passes the
action instead, `& ([scriptblock]::Create((irm <the same address>))) -Action
Install` (or `Update`, `Repair`, `Uninstall`), and when there is no one to
answer and no action the script prints its usage line and stops, before it
makes any request. It runs in Windows PowerShell 5.1 and in PowerShell 7, and
ends with one line: `Earshot: done.` or `Earshot: stopped.` and the reason. It
never uses `exit`, which would close the person's window when run through
`iex`.

**What it does, in order.** It refuses an administrator shell, because the
tray must never run elevated. It reads the latest release once, with
PowerShell's own default headers. It downloads that release's zip and its
`.sha256` into a new folder under `%TEMP%` with a progress bar (plain percent
lines when output is redirected), and checks the zip's SHA-256 before anything
is unzipped; any mismatch ends the run with one plain line. It then runs the
unpacked copy's read-only `Earshot.exe probe setup-values --out <file>`, which
reports the signed-in user's SID, the AirPods' address and container, and what
is installed, so the script never asks for them. It closes a running tray
through the tray's own Exit (`Earshot.exe --exit`), so AirPods in use are let
go and blocked first, and never stops the process. Then it shows the one
administrator prompt. `-DryRun` does everything up to that prompt and prints
the command it would have run. Afterwards it starts Earshot unelevated.

**Which verb gets the prompt.**

| Action | Installed copy | What is elevated |
|---|---|---|
| Install | none, or unusable | the verified copy's `install-zip <zip> <sha256> <userSid> <address> <containerGuid>`, which copies the zip into a folder only SYSTEM and Administrators can use, hashes the copy against the value on its command line, checks every file against the release's file list, and runs that release's `install` from there; it refuses with exit code 26 when a usable install is already there |
| Update | usable and older | the installed `Earshot.exe update ...`, the same verb the tray's Update uses, so it works against an install made before the script existed; the script waits for the outcome record in `%ProgramData%\Earshot\update-outcome.json` |
| Repair | usable | the installed `update` verb with the zip of the installed version's own release, so a repair never changes the version |
| Uninstall | usable | the installed `Earshot.exe uninstall`; with the program gone, the latest release's verified copy runs `uninstall` instead |

Install over a current install, and Update when nothing is newer, say so and
change nothing. Uninstall keeps `%APPDATA%\Earshot` and
`%LOCALAPPDATA%\Earshot` unless `-RemoveSettings` is given (the menu asks);
it removes the Open on startup entry only when it names Earshot's own program.
The pairing is never touched.

**No AirPods paired, or several.** The installed program cannot be set up
without a device to set up for, so the script installs nothing elevated. It
copies the verified release to `%LOCALAPPDATA%\Programs\Earshot` for the
signed-in user, starts it, and says to pair the AirPods (or to choose one in
the menu) and run the line again. That second run takes the normal route and
removes the per-user copy once the install has finished.

**New installs.** A PC with no settings file gets Hand back on shut down,
sleep and Exit on, and Open on startup on. The script's last step starts the
tray, whose first start writes both and copies the Hand back choice to the
service, so the AirPods are handed back from the first shut down. A settings
file that exists is never changed by an install, an update or a repair.

**What the checksum does and does not prove.** The script, the zip and the
checksum come from the same release. The checksum therefore catches a damaged
or cut-short download; it does not catch a compromised release, because
whoever could change the zip could change the checksum and the script with it.
What the elevated verbs add is narrower and real: what lands in Program Files
is exactly the zip whose hash was fixed on the command line when the prompt
was shown, copied first into a folder no ordinary program can write to.
Nothing stops an older release's zip, with its own matching hash, being
offered, as with the app's own updater. On a first install the program that
does the checking, the verified copy's `Earshot.exe`, is in a folder the
person can write to, as it already is when setup is run from an unzipped
release.

**What it never does.** It does not change or persist an execution policy,
turn off or weaken Defender, SmartScreen or UAC, add or remove a download's
Zone.Identifier mark, send a header of its own, fetch or run any other
script, run or unzip anything before its SHA-256 matched, ask for more than one
administrator prompt, start Earshot elevated, or stop a process. The file is
plain ASCII, because Windows PowerShell 5.1 does not decode a download served
as `application/octet-stream` as UTF-8.

The elevated verbs still refuse to run while `EARSHOT_SAFE_MODE` or
`EARSHOT_DATA_ROOT` is set, and so does the script's own prompt, which is why
no test can reach a real install. `tests\Earshot.Tests\Installer` runs the
script in both shells against a local release feed and a stub install root,
with only the prompt, the check of this PC, the running tray and the tray start
replaced. The hosted build is the one that runs the PowerShell 7 half. The real
`Start-Process -Verb RunAs` has not been run by any test; the first real
execution is an update on the owner's PC.

## Command line

One program, `Earshot.exe`, chosen by its first argument.

| Command | What it does |
|---|---|
| `Earshot.exe` | The tray application. `--startup` is the same thing, and is what the startup value passes. |
| `Earshot.exe probe [audio\|topology\|nodes\|services\|task\|battery\|all] [--json] [--out <path>]` | Read-only diagnostics. Reads endpoints, walks the audio topology, reads the device nodes, lists the installed Bluetooth services, reads the scheduled tasks, and reports the battery answer described in [requirements.md](requirements.md). It changes nothing. On a machine where Earshot is not set up and no device is pinned it still writes a full report, and exits 78 to say so: the nodes and services targets had no device to read. So read the report rather than the exit code. |
| `Earshot.exe probe icon --out <folder>` | Writes the tray icon to files, in each of its four states, at three screen scalings and in both inks, for checking how it looks. |
| `Earshot.exe probe widget --out <folder>` | Writes the taskbar gauge, the card and the case-open card to files, from fixed synthetic snapshots, at three DPIs and in both taskbar inks. See [The AirPods widget](#the-airpods-widget). |
| `Earshot.exe install <userSid> <address> <containerGuid> [--principal user]` / `Earshot.exe uninstall` | The one-time setup and its removal. Both need an elevated administrator and refuse to run as SYSTEM. The menu runs `install` with those three arguments filled in; a bare `install` is refused, so it is not a command to type by hand. |
| `Earshot.exe install-zip <zip> <sha256> <userSid> <address> <containerGuid>` | The elevated half of a first install by the install script, run from the verified download's own copy, never by hand. It refuses (exit code 26) when a usable install is already there. See [The install script](#the-install-script). |
| `Earshot.exe probe setup-values [--out <path>]` | Read-only. Writes JSON with the signed-in user's SID, the AirPods' address and container, whether one device could be chosen, and the state and version of the installed copy. The install script reads it. |
| `Earshot.exe --exit` | Asks the running tray to exit through its menu's own Exit, so AirPods in use are handed back first, and ends. Exit code 0 when the tray was asked, 75 when none is running. |
| `Earshot.exe update <zip> <sha256> <pid> <userSid> <address> <containerGuid>` | The elevated half of Update. Started by the installed Earshot after the one administrator prompt, not by hand. See [Updates](#updates). |
| `Earshot.exe repair <userSid> <address> <containerGuid>` | The elevated half of Repair. Started by the tray after the one administrator prompt, from the installed copy only, not by hand. See [Repair](#repair). |
| `Earshot.exe service` | The hand-back service's run mode. Started by Windows from the service's registration, not by hand. |
| `Earshot.exe gate <verb> <nonce> [address]` / `Earshot.exe gate-protect <verb> <nonce>` | The elevated workers: one for the device nodes, one for the Bluetooth services. Started by Earshot's own scheduled tasks, not by hand. |
| `Earshot.exe diag <target>` | Single live actions for testing on real hardware: connect, disconnect, a raw driver request, a gate run, the unelevated service call, and the battery sweep. **All but the battery sweep and `gate status` change the state of the device**; those two only read. They exist for the live tests in `tools\live-tests` and are not part of normal use. |
