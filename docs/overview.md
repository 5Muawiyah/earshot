# Overview

This page is for anyone using Earshot, not just for engineers. It explains
what the tray icon shows, what each menu item does, and what the small cards
near the tray mean, without needing to know how any of it is built. For the
mechanism behind it, see [architecture.md](architecture.md).

## What Earshot is for

Windows connects every paired Bluetooth device the moment the PC turns on.
For a pair of AirPods that is usually the wrong moment: they get pulled off a
phone mid-call or mid-podcast, with nothing on the PC actually wanting them
yet. Earshot stops that by switching the AirPods off at the Windows level
while nobody is using them on this PC, and switches them straight back on
with a click on the card when someone is.

It also stops something quieter: Windows can drop a Bluetooth headset from
its normal stereo sound down to a narrow, phone-call-quality channel the
moment any program opens a microphone. Earshot can turn that fallback off for
the AirPods, so a browser tab asking for a microphone cannot pull them down
to that channel while it is on. Turning it on costs you the AirPods
microphone on this PC.

## The tray icon

Left-click the icon to open a card, then click Connect or Disconnect on it,
whichever they are not already doing. The icon itself shows which of four
states they are in:

| Icon | Meaning |
|---|---|
| Outlined earbuds | Disconnected |
| Solid earbuds | Connected |
| Faded solid earbuds | Busy: connecting, disconnecting or allowing |
| Outlined earbuds with one diagonal slash | Blocked at boot |

Hovering over the icon reads `Earshot: <device name> - connected`, and
likewise `disconnected`, `blocked` or `not found`. If Earshot cannot read the
audio devices at all it says `unknown`, rather than guessing at one of the
four states.

<img src="images/tray-menu.png" alt="An earlier version of Earshot's right-click menu, with the AirPods disconnected" width="360" align="right">

<p align="center"><i>Pending: this picture was taken before the menu gained its widget, update and hand-back items. A new capture is owed. The list below is the current menu.</i></p>

## The right-click menu

Top to bottom, with the exact wording. Separators sit between the groups.

| Item | What it does |
|---|---|
| `Safe mode: no device actions` | A caption, not a command. Shown only when Earshot is running in a mode that turns every device action off, used for testing. |
| `Connect`, or `Disconnect` when connected | The same as the button on the card the tray icon opens. Greyed out while a change is already under way. |
| `Play from a phone` | Shown only once this setting is turned on. Opens a submenu listing the phones paired in Windows that can send audio to this PC, with `Refresh the list` and, once one is open, `Stop playing from <name>`. Greyed out before Windows 10 version 2004 (build 19041). |
| `Block at boot` | A tick. Keeps the AirPods' device nodes disabled while they are not in use. Turning it on before setup has run starts setup. The tick shows what is in force; when the setting cannot be read it shows neither state. |
| `Hand back on shut down, sleep and Exit` | A tick. Releases the AirPods and blocks the device nodes again when you shut down, restart, sleep or choose Exit. See [below](#handing-the-airpods-back). |
| `Protect audio quality` | A tick, on by default. Turns off the Hands-Free profile, described above. |
| `Turns off the AirPods microphone` | A caption under that setting, always visible and never clickable, telling you plainly what it costs. While the Hands-Free microphone off mode is on it reads `Microphone off mode` instead. The mode is switched on the settings page. |
| `Open on startup` | A tick, on by default. Writes one value named `Earshot` under the current user's `Run` key, with the `--startup` argument. Earshot has to be running for it to put the block back once you stop using the AirPods, so this keeps it running from sign-in. |
| `Speak status`, or `Speak status (no voice)` when no speech voice is installed | A tick, off by default. Clicking it does not itself speak anything. |
| `Show on the taskbar` | A tick. Whether the gauge (or, when it cannot be placed, the tray icon) shows the AirPods widget. |
| `Left click connects straight away` | A tick, off by default. A left click on the gauge or icon then connects or disconnects at once instead of opening the card. |
| `Low battery alert` | A tick. |
| `Threshold` | A submenu of 10% to 90% in steps of 10, greyed out while the alert is off. |
| `Refresh battery` | Opens the card and starts a battery refresh. See [Refresh](#refresh). Shown while the widget is shown on the taskbar. |
| `Name your other device...` | Sets the label used in "On your <name>". |
| `Choose device...` | Lists the Bluetooth devices paired to this PC, so you can point Earshot at a different one, for instance if your AirPods were renamed, so that the default match "AirPods" no longer fits. Choosing one pins it, and points the elevated worker at the same device. A device that cannot play audio from this PC, a phone for instance, is refused. |
| `Set up Earshot...` | Runs the one-time setup. Offered only when nothing is installed. Once Earshot is installed, in any state, the next item takes its place. |
| `Repair Earshot...` | Offered whenever Earshot is installed, healthy, damaged, older or newer than the running copy. At most one administrator prompt. Disabled, with the reason in its text, while a setup, repair or update is running. See [Repair](#repair). |
| `Check for updates` | Looks for a newer release. Downloads nothing. See [Updates](#updates). |
| `Check automatically` | A tick, off by default. Contacts GitHub once a day when on. |
| `Exit` | Closes Earshot. With Block at boot on and the AirPods not in use, it blocks the device nodes first. With Hand back ticked and the AirPods in use on this PC, it lets them go first, then blocks the device nodes. With Hand back off and the AirPods in use, it closes without blocking and says so: "Closed while in use, so the AirPods are not blocked." |

A shortcut typed into the settings file for connect, audio protection or block
at boot is shown beside the item's own label, as `Block at boot
(<shortcut>)`. The two default shortcuts (see [Shortcuts](#shortcuts)) do not
appear in the menu.

<br clear="all">

<img src="images/connect-card.png" alt="An earlier version of the small card Earshot shows near the tray after a connect" width="360" align="right">

## The connect and disconnect card

A small card appears near the tray with the device name and what is
happening, then closes itself without taking the focus away from whatever you
were doing. If the AirPods do not arrive the ordinary way, the card says so
honestly, for instance "Trying another way" while Earshot works around a
driver refusal.

<p align="center"><i>The card Earshot shows near the tray when the AirPods connect.</i></p>

<br clear="all">

<img src="images/boot-block.png" alt="An earlier version of the card Earshot shows when the AirPods are blocked at boot" width="360" align="right">

## The boot block

With Block at boot ticked, the AirPods' device nodes are disabled the moment
they stop being used. The block is designed so that Windows has nothing to page
at the next boot: a disabled node is meant to stay disabled through a restart,
so nothing has to run at shutdown. The power cycle test that checks this (test
08) has not run yet, and a shut down with Fast Startup on is not covered.

<p align="center"><i>The card Earshot shows when the AirPods are blocked at boot.</i></p>

<br clear="all">

<img src="images/audio-protection.png" alt="An earlier version of the card Earshot shows when audio quality protection is switched on" width="360" align="right">

## Audio quality protection

With Protect audio quality ticked, Windows has no Hands-Free profile left to
fall back to, so a browser tab or a game asking for a microphone cannot pull
the AirPods down to a narrow voice channel. This does not make the sound
better than usual; it stops something else from making it worse. The tray
says plainly that the setting turns off the AirPods microphone on this PC
while it is on.

Connecting can need the setting off for a moment, because the quick reconnect
Earshot uses depends on the very profile this setting turns off. If Windows
refuses the quicker way, the card says "Trying another way": Earshot turns
the protection off, connects, then turns it back on, and the microphone works
again on this PC while that runs. If putting the protection back fails, the
card says "Connected, but audio quality protection did not apply." and the
microphone stays available until the next connect fixes it.

There is an opt-in alternative, off by default: [Microphone off mode](#microphone-off-mode).

<p align="center"><i>The notice Earshot shows when audio quality protection is switched on.</i></p>

<br clear="all">

## Shortcuts

Shortcuts are on by default. Ctrl+Alt+Shift+A connects, which means it
switches the AirPods to this PC. Ctrl+Alt+Shift+D disconnects, which means it
switches them to the phone. On the settings page (the gear on the card) each
one can be changed, by pressing the new keys, or cleared.

A shortcut runs the same thing the button does, and shows the same card with
the result. If another program already holds a shortcut, Earshot says so on the
card and under that shortcut's row on the settings page. If a second press
arrives while an earlier one is still running, the last press wins: a press for
the other direction replaces the one in flight, and a press for the same
direction changes nothing.

Older settings files are read like this. A shortcut the file already holds is
kept. A file written before the two shortcuts existed and never given a typed
shortcut keeps the defaults, and if that file said shortcuts were off, they are
read as on. Once the file is saved it is not read that way again, so turning
shortcuts off stays off. A default that another of your own shortcuts already
uses is left empty. Shortcuts for toggle connection, audio protection, block at
boot and speak status can still be typed into the settings file.

## Switching between the phone and this PC

A switch to this PC is a connect, and a switch to the phone is a disconnect.
The labels stay Connect and Disconnect. Earshot times every switch it starts,
from one monotonic clock, and writes one line to the log for each: `Switch
to-pc:` or `Switch to-phone:`, with the phases the handover went through, how
it started (click, menu or shortcut) and, for a connect, which path it took.
Each line measures that one switch. No sitting on the real AirPods has
recorded any yet (test 16 is pending), so this page gives no timings.

## Speak status and Play from a phone

Neither has had a live run, and both are off by default.

**Speak status.** Off until turned on from the menu. Earshot speaks only from
a fixed, short list of phrases: Connected, Disconnected, Blocked at boot,
Allowed at boot, Connect failed, Block failed. No device name, address or
number ever reaches the speech engine, and it says nothing for a state it only
assumed, such as connecting or unknown; the tooltip is shown instead. It uses
the speech voices already installed on Windows.

**Play from a phone.** Off until turned on in the settings file. It turns this
PC into a Bluetooth speaker a paired phone can send audio to, through a
Windows API built for it. The phone has to already be paired in Windows
Settings; Earshot cannot pair it itself, because Windows does not support
in-app pairing for a desktop program. Your own AirPods are never offered as the
source, only one device plays at a time, and the feature never touches the
boot block, the scheduled tasks or elevation. It is released when Earshot exits
and when Windows is closing it. It needs Windows 10 version 2004 (build 19041)
or later, which every Windows 11 has. It has never been run against a real
phone, so whether this PC's Bluetooth radio offers the source role at all, and
what turning it on does to an AirPods link already open, are both open
questions.

## Handing the AirPods back

If the AirPods are playing from this computer when you shut down, restart,
put the computer to sleep, or choose Exit, Earshot lets go of them and stops
this computer grabbing them straight back. In order: release, disconnect,
confirm, block. If the disconnect does not confirm, the block is still sent.
What happens to the AirPods after that is not something this computer
decides: they go back to whatever device they prefer, which has been the phone
every time this was tried; Earshot cannot choose the device for them, command
the phone, or see what the phone does.

Sleep works the same way, on a much shorter clock, because Windows gives an
application far less time to act before the computer actually sleeps than it
gives at shut down. Exit has no Windows deadline, so it uses the shut-down
cap, and a stuck disconnect does not stop the block.

**Hand back on shut down, sleep and Exit** is on for a new install: a PC with
no settings file gets it on, and Open on startup on, the first time Earshot
starts. An existing install keeps the choice it has saved, and an older settings
file with no entry for it reads as off. Tick or untick it in the right-click
menu, or on the settings page. While it is off, Exit while the AirPods are in use
does not block them, and the hand-back service does nothing at shut down. The
tests cover the hand-back against stand-ins. It has
not been tried on a real shut down, sleep or Exit (tests 17, 18 and 20 are
pending).

**The honest limits.** This needs Earshot running. A power cut, a forced
shutdown, or the battery reaching a critical level gives Windows no chance
to run it at all, because none of those send Earshot any notice. When that
happens, the fallback is the same one Earshot always has: the block Earshot
runs at the next start-up. That fallback can lose the race, so the AirPods
may connect to this computer briefly before it catches up and blocks them
again.

## Pausing when the AirPods leave this PC

On by default, and a row on the settings page. If this PC was playing to the
AirPods when they leave it, Earshot pauses playback so the sound does not jump
to the speakers. It acts before Earshot's own disconnects (Disconnect, the
hand-back, a fast switch) and as soon as it sees the AirPods leave any other
way, such as the phone taking them. It never resumes anything.

Windows does not say which output a media session is using, so Earshot pauses
the one media session that is playing, whichever output it uses. With two
playing, it pauses none. It pauses nothing when this PC was not playing to the
AirPods. Each decision is written to the log on a `Pause on leave:` line with
its reason. No live run yet (test 21 is pending).

## Updates

**Check for updates** in the menu and on the settings page looks at the latest
release on GitHub and says whether it is newer. **Check automatically** does
the same once a day after startup; it is off by default because a check
contacts GitHub. A check downloads nothing. Nothing downloads until you press
**Update**. Update works from any copy that is running, including one unzipped
in a download folder and one newer than the installed copy, as long as Earshot
is installed in Program Files. A copy that is not the installed one measures
the update against the installed version, because that is what gets replaced.

When you press Update, Earshot downloads the release zip and checks it against
the `.sha256` file published with it. Earshot then finishes its own closing work
(the hand-back and the block that Exit does, with the same limits), starts the
installed Earshot with one administrator prompt, and ends without another call to
the AirPods: the installed 1.2.0 and the first 1.2.1 do not wait for a copy run
from another folder to end, so the install must not start while this copy is
still letting go. With Hand back off and the AirPods in use nothing can block
them, so the card says before the closing work that they stay connected and are
blocked again at the next start, and suggests turning Hand back on or
disconnecting them first, and keeps that notice up for its time before the prompt
(any notice the closing work ends with is kept up the same way). Once the closing
work has run, a sign-out, shut down, sleep or resume while the prompt is open
sends nothing to the AirPods, because the install that follows works on the
scheduled tasks and the service. The installed Earshot checks the zip again, from a folder only
administrators can write, and installs from there. The program that runs with
that prompt is the installed one, which is in a folder only administrators can
change, never the copy you were running. If the prompt is declined after the
closing work, the card says so and Earshot starts again, because it cannot carry
on from a half-closed state. A copy started as an administrator starts no other
copy, so it says so instead.

When the update has finished, Earshot is running again: the install that ends the
update starts the new Earshot for you, not as an administrator, once the scheduled
tasks and the service are in place, and the card says once how the update went. It
does this only for an update (or a repair that downloads the files again), and not
when Earshot is already running. If Windows will not start it now, for example
because you have signed out meanwhile, Earshot starts at your next sign-in when
Open on startup is on, or when you start it.

Only one setup, repair or update runs at a time. While one does, **Repair
Earshot**, **Set up Earshot** and **Check for updates** are disabled in the menu
with the reason in their text ("finishing the repair first"), the settings page
says the same on its rows, and a click from the card is refused with the same
words. Exit waits for a setup or repair that is running (up to 90 seconds, with
the card "Finishing the repair first.") before it hands back and blocks, so the
block never meets a scheduled task that is being registered again; if the wait
runs out it says so and logs it. The installed program also takes a
machine-wide lock for setup, update and repair, so two runs from different
places never work on the folder, tasks and service at once; a second one is
refused with "Another Earshot setup, update or repair is still running."

If nothing is installed, the card says to set up first and offers **Set up
Earshot** on the same card. If something is installed but cannot be used (its
program is missing, or its folder can be changed by a standard user), the card
says to repair first and offers **Repair Earshot**.

A copy that is not the installed one, started while Earshot is installed, says
so on one card and offers **Switch to it**. The card says before the button
that switching closes this copy first, and that with Hand back on it hands the
AirPods back and blocks them, because the switch is an ordinary Exit. The
installed one then starts. Such a copy never points **Open on startup** or the
Start menu shortcut at itself, so the next start from either is the installed
copy. A copy that runs as an administrator offers no switch, because the
installed copy would start as an administrator too: it says to start Earshot
from the Start menu.

### Repair

**Repair Earshot...** is in the menu whenever Earshot is installed, and on the
settings page in the Updates section. Earshot first reads every installed file
and checks it against the list the release published, and checks that the
install folder can be changed only by administrators. Then:

- If every file matches, the installed Earshot repairs itself after one
  administrator prompt: it registers the scheduled tasks, the hand-back service,
  the machine settings and the device file again, exactly as setup does. It
  checks the folder and every file before it stops the hand-back service, so a
  repair that finds a file damaged leaves the service running.
- If a file is missing or does not match, Earshot downloads the release of the
  version you have installed, checks it against the `.sha256` file published
  with it, and hands it to the installed Earshot's update, so the files come
  back only from checked bytes. The installed Earshot that runs that update is in
  a folder only administrators can change, even when its own file is one that
  differs; what it installs is only the verified zip.
- If a file or the file list could not be read, or the install folder's
  permissions could not be read (another program holds it open, or access was
  refused), nothing is changed and nothing is run elevated. Earshot says it
  could not read the installed files and to try again, and logs the raw code.
  A file that could not be read says nothing about what is in it, and a
  standard user can make any installed file unreadable for as long as they
  like, so it is not treated as missing.
- If every file matches but the installed version could not be read (the file
  is held open, or carries no version), the installed program's install verb
  runs, which every version runs from its own folder as the same repair. If a
  file is also missing or different, nothing is changed and nothing is run
  elevated, because the release to download is named by the installed version.
- If the running copy is newer than the installed one, Repair does not run it
  elevated, because that copy may be in a folder a standard user can write. It
  opens the update path instead: Check for updates, then Update.
- If the installed `Earshot.exe` is truly missing (its folder lists without it),
  or its folder can be changed by a standard user, the running copy's own setup
  puts a new install in place. That copy must be an unzipped release you
  downloaded and checked. Nothing else makes Earshot run the running copy
  elevated while an install exists.
- If nothing is installed, the menu offers Set up instead.

Your settings and chosen AirPods are kept. How the repair ended
is recorded where the next start can say it, in case the card has gone.

What the checksum does and does not protect against: it catches a damaged or
cut-short download, and a file that differs from what the release lists. It
does not protect against a compromised release or account, because the
checksum comes from the same release and the app is not signed.

## Shutdown safety

Once Windows says the session is ending, Earshot refuses to start a connect,
an allow, or a setting change from any trigger, and says so on a card. A
block that is already due still runs, and one known to have failed is tried
once more. A close request from Windows, such as the one the Restart Manager
can send, is treated the same as choosing Exit.

The same refusal covers a sleep hand-back too, even though sleep is not a
session end in Windows' own terms: every menu item, keyboard shortcut and
click is refused with its own card while either hold is running, so nothing
can start a change that would fight what the hand-back is doing.

If the Earshot icon has been closed, a small background service, installed by
setup and removed by uninstall, can block the AirPods at shut down instead. It
acts only when Block at boot and Hand back are both on. It blocks and never disconnects them, so the AirPods may stay
connected until the computer is off. It has not had a live run, does not cover a shut down with Fast Startup
on, and whether a restart gives it the shut-down notice is not proved. See
[architecture.md](architecture.md#handing-the-airpods-back-at-shut-down-and-sleep).

For what this does and does not guarantee when the PC is actually shut down,
see [requirements.md](requirements.md#what-it-does-not-do) and
[verification.md](verification.md).

## The AirPods widget

Alongside the tray icon, Earshot can show a small gauge on the taskbar, and
from it a card with more detail. It works by listening to the AirPods' own
Bluetooth broadcast, the same short signal a phone reads for its own battery
widget. There is no set-up: you open the AirPods case next to the PC once and
Earshot links to your pair. The figures show wherever the AirPods are: live while
they are being heard, then greyed with their age, kept across a restart until a
newer reading comes. That is also why it has firm limits; see
[below](#what-the-widget-cannot-know). The battery has not had a live run.

**The gauge.** The earbud mark with a ring round it in your Windows accent
colour. The ring fills to the lower bud's battery, that number sits beside it,
and a bolt appears when a bud it is drawn from says it is charging. The ring
changes colour when the battery is at or below the low battery threshold. The
gauge has five looks, and its tooltip says why:

| When | What the gauge shows | Tooltip |
|---|---|---|
| On this PC with a bud reading of any age | The ring, the number, and a bolt when it applies; grey when the reading is not live | Three lines: `AirPods` (or `Charging`, or `Low battery`), then the buds that have a reading such as `L 70%   R 60%`, then `Read just now`, `Last read 2 h ago` or `Estimated, read 2 h ago` |
| On this PC, with no bud reading | The earbud mark alone | `No recent reading` |
| Not on this PC, with a case reading | A case mark, the ring and the case's number, all in grey, and a bolt if the case was charging | `Not on this PC`, then `Case 80%`, then `Last read` or `Estimated` with the age |
| Not on this PC, with no case reading | The earbud mark, faded | `Not on this PC` |
| Not on this PC, and your AirPods are near and in use on another device | The earbud mark and a phone | `On your iPhone`, or the label you set, then the case when there is a reading of it |

A reading stays on the gauge, greyed, however old it is, until a newer one is
heard. A bud with no reading is left out of the tooltip, never shown as a
dash or a guess. When no bud has a current broadcast value, the AirPods are on
this PC and Windows has a Hands-Free figure for them, the gauge shows that
figure and the tooltip says `Windows reads 70%`. **Gauge order** on the
settings page chooses how the ring, the number and the bolt line up, from six
pictures of the gauge.

**Where it sits.** On the taskbar, at the right end by default, 8 pixels left
of the notification area, or next to the apps, 4 pixels after the last app
button. Choose with **Gauge position** on the settings page. If there is no
free space on the taskbar, or Earshot cannot read the taskbar at all, the
gauge steps aside and the ordinary tray icon takes over, without asking.

**Which display.** With more than one display, **Gauge display** on the settings
page chooses whose taskbar holds the gauge: **Main display** (the default),
**All displays**, or one of the connected displays, named plainly, for example
`Display 2 (1920 x 1080)`. Click the button to step through the list. **All
displays** puts a gauge on the taskbar of every display that has one, at once, all
with the same content, order and tooltip; each is placed and scaled for its own
display, raised when its own taskbar covers it, and hidden when a full-screen
application is on its own display, so a full-screen game on Display 2 hides only
Display 2's gauge. A click on a gauge
opens the card above that gauge, on its display, and the hover tooltip and the
right-click menu work on each. A display or a taskbar that is plugged in or
turned on gets a gauge, and one that goes loses it, within about a second; with
no other taskbar it is the same as **Main display**. While any gauge is on a
taskbar the tray icon stays out of the way. On another display's taskbar the
gauge keeps the same two positions: at the right end, 8 pixels left of the
clock (or of the taskbar's end when that taskbar shows no clock), or 4 pixels
after the last app button, and it never overlaps anything. Each display's own
scale sets the gauge's size. If the chosen display is unplugged, or shows no
taskbar (Windows can turn that off for other displays), the gauge goes to the
main display's taskbar, the log says why, and it moves back when the display
returns. The card opens on the same display as the gauge. "Display 2" is the
number in the display's GDI device name, which is how Windows enumerates it;
Windows' own Settings page may number the same display differently, so use the
resolution beside the name to tell them apart. If a display is plugged in or
out while the settings page is open, the page redraws with the displays that
are there, and a choice of a display that has just gone is not stored.

**Staying visible.** The gauge belongs to the taskbar it sits on, so Windows keeps it
above the bar when Start, a flyout or a taskbar click raises the bar. If it cannot,
Earshot raises it again, less and less often while the bar keeps covering it. Every hide, show, cover and raise is written to the
log with a reason and the window's class only, never a title. One failed read
of the taskbar does not move or hide the gauge; it stays where it is until
three reads in a row have failed.

**Full-screen applications.** The gauge hides for a full-screen application
only on the display that application covers. A full-screen game on another
display leaves it shown. With one display nothing changes: a full-screen
application hides the gauge. Presentation settings, which are the owner's own
switch and not a window, hide it on every display.

<img src="images/widget-gauge.png" alt="Earshot's taskbar gauge: a ring round the earbud mark, the number 55 and a charging bolt" width="220" align="right">

<p align="center"><i>The taskbar gauge in the light theme, charging, with made-up values, rendered by Earshot's own code.</i></p>

<br clear="all">

Left-click the gauge, or the tray icon when that is what is showing, to open
the card. With **Left click connects straight away** on, a left click connects
or disconnects immediately instead. It is off by default.

**The card.** A title with a gear that opens the settings page. Then three
columns, left bud, right bud and case, each with a bar, its number and a
charging mark when it applies. The bars use your Windows accent colour, and
switch to the caution colour at or below the low battery threshold. A value
read within the last 30 seconds is drawn as current. An older one is greyed
with its age beside it (`80% · 2 h`), and an estimate also has `≈` before it
(`≈90% · 2 h`); hovering over it says `Last read` or `Estimated` and how long ago. A column with no value says
"No reading". Below them: one line for where the AirPods are (`On this PC`,
`On your iPhone` or the label you set, `Not in use`, or `Not seen yet`), one
line for when the battery was last read (`Battery read 2 min ago`, or `Battery
not read yet`) beside a refresh control, a line `Version <number> is
available` when a check found a newer release, and the Connect or Disconnect
button.

<img src="images/widget-card-light.png" alt="The widget card in the light theme, with the left bud, right bud and case at 70, 60 and 90 percent" width="320" align="right">

<p align="center"><i>The widget card in the light theme, with made-up values, rendered by Earshot's own code onto one sample of the card's backdrop colour.</i></p>

<br clear="all">

<img src="images/widget-card-dark.png" alt="The widget card in the dark theme, with the left bud, right bud and case at 70, 60 and 90 percent" width="320" align="right">

<p align="center"><i>The same card in the dark theme, with the same made-up values.</i></p>

<br clear="all">

### Refresh

The refresh control on the card, and **Refresh battery** in the menu, ask the
AirPods for a fresh reading by listening again. Earshot restarts its listener
and waits up to 12 seconds for a message from your linked AirPods or, when none
is linked, for the case to be opened. The card says it is reading while it waits,
and the refresh ends one of these ways:

- **Values.** A message arrived, or the case was opened and linked, and the card
  shows them as current.
- **Windows' figure.** Nothing was heard, but Windows has a Hands-Free figure.
- **"Nothing heard. Open the case."** A closed case sends nothing, so open it
  by the PC and refresh again.
- **Bluetooth is off, or the listener is not running.** It says so.

The 12 seconds is a choice made in the code, sized to the longest gap seen
between two messages, not a measured guarantee.

### The settings page

The gear on the card opens it. Rows, top to bottom:

| Row | Default | Notes |
|---|---|---|
| Gauge position | Right end | Right end, or next to apps |
| Gauge display | Main display | Main display, All displays (offered with more than one display), or one of the connected displays; a line under the row says when the chosen one is not connected or shows no taskbar |
| Other device | `iPhone` | The label used in "On your <name>", up to 40 characters |
| Gauge order | Ring, number, bolt | Six pictures of the gauge; pick one |
| Pause when a bud comes out | On | Built but inactive. Says "Earshot cannot yet tell when a bud is in your ear." and nothing acts, because no documented source gives an in-ear value |
| Pause when AirPods leave this PC | On | See [above](#pausing-when-the-airpods-leave-this-pc) |
| Low battery alert | 20% | A stepper, 10% to 90% in steps of 10 |
| Left click connects | Off | |
| Hand back on shut down, sleep and Exit | On for a new install | A tick, as in the menu. See [above](#handing-the-airpods-back) |
| Microphone off | Off | See [Microphone off mode](#microphone-off-mode). While on, a line under the row says what to do next, and a Sound settings row with an Open button appears |
| Shortcuts: Connect | Ctrl+Alt+Shift+A | Press keys to change, Clear to remove; a chord another app holds is named under the row |
| Shortcuts: Disconnect | Ctrl+Alt+Shift+D | The same |
| Updates: Check for updates | | A Check button, and the version you have |
| Updates: Repair Earshot | | A Repair button. Only there while Earshot is installed |
| Updates: Check automatically | Off | Contacts GitHub when on |

The low battery alert's own on and off is a tick in the right-click menu.

**The case-open card.** When you open your AirPods' case near the PC, a card
with the left, right and case figures and the Connect or Disconnect button
appears by itself, where the gauge is, without taking the focus. It never
connects the AirPods by itself. It closes when you shut the case (about eight
seconds after the case stops sending), or after 5, 10, 30 or 60 seconds if you
choose one, or on its close button. The Case-open card row in the settings
turns it off, and its expander holds the close time and the displays it shows
on (where the gauge is, all displays, or the displays you tick). It is never
shown over a full-screen application, and a screen reader says it once.

**The low battery alert.** A Windows notification, or the card if a
notification cannot be shown, the moment a shown value first reads at or below
a threshold, 20% by default. It only fires again once that part has read at
least one 10% step back above the threshold, so a value sitting on the edge
does not repeat itself.

**Pausing when a bud comes out.** A row on the settings page, on by default.
It is built but inactive. It is meant to pause what this PC is playing to the
AirPods when a bud is taken out, and to play it again if the bud goes back: only
the session it paused, only within 60 seconds, only while the AirPods are still
this PC's output, only if nothing was played or paused by hand since as far as
Windows reports it, and only with every bud that was in back in. It would read
the in-ear state from the broadcast, and no documented source gives that value,
so Earshot reads none, nothing pauses, and nothing resumes. Turning it on
changes nothing until a documented in-ear value exists. Nothing resumes after the
AirPods leave this PC.

<a id="microphone-off-mode"></a>

### Microphone off mode

An option on the settings page, off by default. It is for someone who wants
Windows to keep the Hands-Free link up, so Windows can hold a battery figure,
without apps being able to use the AirPods' microphone. Turning it on only
turns Protect audio quality off. The row's Open button opens Windows' sound
settings, at the AirPods' microphone when Earshot has a capture endpoint id for
it, and at the list of sound devices when it has none, with one line: set the AirPods microphone to "Don't allow". Earshot
cannot do that for you, because Windows documents no call that switches that
microphone off. The row says when Windows already lists the microphone as off.
Choosing Protect audio quality on its own turns the mode off.

Call quality with the mode on is unproved until the owner has tried it, and the
full block, Protect audio quality, stays the default. The block that keeps the
AirPods unpaged at rest picks the AirPods' device nodes by container and
address, so it does not depend on the mode.

### What the widget cannot know

- **The other device is your own label, not a reading.** "On this PC" comes
  from Windows itself. "On your iPhone", or whatever you type, is only your
  name for "in use, somewhere that is not this PC"; the AirPods never say what
  they are actually connected to.
- **Battery is a snapshot, never live.** It is read in steps of 10% (0, 10,
  20 to 100) from the last broadcast Earshot heard. A value older than 30
  seconds is greyed with its age, and is kept, across a restart too, until a
  newer one is heard. It is only ever of the pair you linked by opening the
  case. A part that was charging when last read may be shown as an estimate,
  marked `≈` with the age of the reading it grew from: it rises at a rate learned
  from your own pair's charging (none until one is learned) and stops at 100. It
  never falls, and a newer reading replaces it.
- **Left and right rest partly on a published description of the broadcast
  and partly on one local capture, and are unproved.** No permitted source
  documents the bit that swaps the two buds. If it is read the wrong way round,
  the left and right figures are swapped. The lower bud's number on the gauge
  is not affected by that.
- **Ear detection and whether the lid is shut are not read.** No documented
  source gives either, so ear detection stays off and the case-open card
  closes when the case stops sending rather than on a lid bit.
- **Windows' own figure is usually empty.** Earshot reads the Hands-Free
  battery property from the AirPods' device nodes only. With Hands-Free off, the
  default, it was seen empty on this PC.
- **The widget hears everyone's AirPods nearby, not only yours.** A room full
  of the same model broadcasts the same way yours do. See below.

### Whose AirPods it shows

There is no set-up, so Earshot has to find your AirPods in what it hears. It takes
the model of the AirPods paired to this PC from Windows. **You link them by opening
the case next to the PC.** A pair in an open case sends its case level, and a pair
in use does not, so Earshot links the set of that model that sends a case level,
close to the PC (a signal of at least -70 dBm), steadily for about two seconds. The
first set to do that is linked, and a pair that opens its case later but 8 dB stronger
takes the link. Your two buds count as one set. A pair worn nearby
is never linked, however near.

After that Earshot follows your pair when its Bluetooth addresses change, which it
does by the levels the pair last said: a set with other levels is never taken for
yours. If it cannot hear your pair for two minutes it drops the link and shows
its last readings, greyed, until you open the case again. The link is kept in
memory only, so restarting Earshot, as an update does, needs the case opened once
more for live figures; the last readings are kept.

With the AirPods connected and nothing linked, the card and the gauge's tooltip say
"Open the case to show battery", and **Refresh** listens for the case to be opened.
Windows' own Hands-Free figure, when it has one, shows for the connected AirPods
whether or not a pair is linked.

**The accepted risk.** A pair of the same model, and the same colour once one is
held, that opens its case next to the PC more strongly than yours can be linked
instead, and one whose levels equal your pair's last, heard within 30 seconds of
yours going quiet, can be followed as if it were yours. The owner accepted that risk
rather than add a set-up step to rule it out. Opening your own case next to the PC,
at least 8 dB stronger than the other pair, takes the link back.

### What still needs a kernel driver

Noise control, battery to the nearest 1%, and a few other AirPods features
are not built, and are not close to being built, because of what Windows
does and does not let an ordinary program do. See
[architecture.md](architecture.md#what-still-needs-a-kernel-driver) for the
full list and why, with sources.

### Privacy

The widget only listens. It never connects to, or sends anything to, a
Bluetooth device to get this data, and it does not need the AirPods connected
to this PC to listen. It does read the paired AirPods' model from Windows to know
which broadcast to link. The link is held in memory and is not written to disk.
The last reading of each bud and the case of your linked pair (the level, whether
it was charging, when it was read and the model number) and the charge rates
learned from them are kept in `last-reading.json` in Earshot's local folder; no
address and no name. A device that is not linked is counted and nothing else
about it is kept.
