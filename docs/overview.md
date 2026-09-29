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

<img src="images/tray-menu.png" alt="Earshot's right-click menu on a set up PC with the AirPods disconnected" width="360" align="right">

<p align="center"><i>The right-click menu on a PC that is set up, with the AirPods disconnected.</i></p>

## The right-click menu

Top to bottom, with the exact wording:

| Item | What it does |
|---|---|
| `Safe mode: no device actions` | A caption, not a command. Shown only when Earshot is running in a mode that turns every device action off, used for testing. |
| `Connect`, or `Disconnect` when connected | The same as clicking the button on the card the tray icon opens. Greyed out while a change is already under way. |
| `Play from a phone` | Shown only once this v1.1 setting is turned on. Opens a submenu listing the phones paired in Windows that can send audio to this PC, with `Refresh the list` and, once one is open, `Stop playing from <name>`. Greyed out before Windows 10 version 2004 (build 19041). |
| `Block at boot` | A tick. Keeps the AirPods' device nodes disabled while they are not in use. Turning it on before setup has run starts setup. The tick shows what is in force; when the setting cannot be read it shows neither state. |
| `Hand back at shut down and sleep` | A tick, on by default. Releases the AirPods and blocks the device nodes again when you shut down, restart or put the computer to sleep, so it does not grab them straight back. See [below](#handing-the-airpods-back). |
| `Protect audio quality` | A tick, on by default. Turns off the Hands-Free profile, described above. |
| `Turns off the AirPods microphone` | A caption under that setting, always visible and never clickable, telling you plainly what it costs. |
| `Open on startup` | A tick, on by default. Writes one value named `Earshot` under the current user's `Run` key, with the `--startup` argument. Earshot has to be running for it to put the block back once you stop using the AirPods, so this keeps it running from sign-in. |
| `Speak status`, or `Speak status (no voice)` when no speech voice is installed | A tick, off by default, for this v1.1 feature. Clicking it does not itself speak anything. |
| `Choose device...` | Lists the Bluetooth devices paired to this PC, so you can point Earshot at a different one, for instance if your AirPods were renamed, so that the default match "AirPods" no longer fits. Choosing one pins it, and points the elevated worker at the same device. A device that cannot play audio from this PC, a phone for instance, is refused. |
| `Set up Earshot...` | Runs the one-time setup. Shown only while setup is still needed. |
| `Exit` | Closes Earshot. With Block at boot on, it blocks the device nodes first. With Hand back ticked and the AirPods connected to this PC, it lets them go first, then blocks the device nodes. |

Once a keyboard shortcut is set and switched on, it is shown beside the
item's own label, for instance `Connect (Ctrl+Alt+P)`.

<br clear="all">

<img src="images/connect-card.png" alt="The small card Earshot shows near the tray after a connect" width="360" align="right">

## The connect and disconnect card

A small card appears near the tray with the device name and what is
happening, then closes itself without taking the focus away from whatever you
were doing. If the AirPods do not arrive the ordinary way, the card says so
honestly, for instance "Trying another way" while Earshot works around a
driver refusal.

<p align="center"><i>The card Earshot shows near the tray when the AirPods connect.</i></p>

<br clear="all">

<img src="images/boot-block.png" alt="The card Earshot shows when the AirPods are blocked at boot" width="360" align="right">

## The boot block

With Block at boot ticked, the AirPods' device nodes are disabled the moment
they stop being used, and stay disabled through a restart. Nothing has to run
at shutdown for that to hold; staying disabled is the steady state, and it is
what stops Windows paging them at the next boot.

<p align="center"><i>The card Earshot shows when the AirPods are blocked at boot.</i></p>

<br clear="all">

<img src="images/audio-protection.png" alt="The card Earshot shows when audio quality protection is switched on" width="360" align="right">

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

<p align="center"><i>The notice Earshot shows when audio quality protection is switched on.</i></p>

<br clear="all">

## v1.1 features

These three are built and reviewed, but off by default, and none has been
tried on real hardware yet: no shortcut has been pressed for real, nobody has
heard Earshot speak, and no phone has actually played through it. Turning any
of them on is switching on something unverified in practice.

**Keyboard shortcuts.** Off until you type one into
`%APPDATA%\Earshot\settings.json`; there is no settings window for this yet. A
shortcut runs exactly what its matching menu item runs, and shows the same
card near the tray with the result. A shortcut can turn Block at boot on but
never off, because turning it off stays a deliberate menu action, and a
shortcut never opens setup, because setup ends in an administrator prompt you
did not visibly ask for. A key with no modifier, Shift on its own, and F12
are all refused, Shift because it cannot be told apart from ordinary typing. If another program already holds the same shortcut, the
card names it.

**Speak status.** Off until turned on from the menu. Earshot speaks only from
a fixed, short list of phrases: Connected, Disconnected, Blocked at boot,
Allowed at boot, Connect failed, Block failed. No device name, address or number
ever reaches the speech engine, and it says nothing for a state it only assumed, such as
connecting or unknown; the tooltip is shown instead. It uses the speech voices
already installed on Windows.

**Play from a phone.** Off until turned on in the settings file. It turns
this PC into a Bluetooth speaker a paired phone can send audio to, through a
Windows API built for it. The phone has to already be paired in Windows
Settings; Earshot cannot pair it itself, because Windows does not support
in-app pairing for a desktop program.
Your own AirPods are never offered as the source, only one device plays at a
time, and the feature never touches the boot block, the scheduled tasks or
elevation. It is released when Earshot exits and when Windows is closing it.
It needs Windows 10 version 2004 (build 19041) or later, which every
Windows 11 has. It has never been run against a real phone, so whether this
PC's Bluetooth radio offers the source role at all, and what turning it on
does to an AirPods link already open, are both open questions.

The hand-back at shut down and sleep, described below, is built and covered
by its own tests. Unlike these three it is on by default, but it is in the
same position on one point: it has not been tried on a real shut down or a
real sleep either.

## Handing the AirPods back

If the AirPods are playing from this computer when you shut down, restart,
or put the computer to sleep, Earshot lets go of them and stops this
computer grabbing them straight back. What happens to the AirPods after that
is not something this computer decides: they go back to whatever device
they prefer, which has been the phone every time this was tried; Earshot
cannot choose the device for them, command the phone, or see what the phone
does.

Sleep works the same way, on a much shorter clock, because Windows gives an
application far less time to act before the computer actually sleeps than it
gives at shut down.

This is on by default. Turn it off from **Hand back at shut down and
sleep** in the right-click menu, the tick directly under Block at boot.

**The honest limits.** This needs Earshot running. A power cut, a forced
shutdown, or the battery reaching a critical level gives Windows no chance
to run it at all, because none of those send Earshot any notice. When that
happens, the fallback is the same one Earshot always has: the block Earshot
runs at the next start-up. That fallback can lose the race, so the AirPods
may connect to this computer briefly before it catches up and blocks them
again.

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
setup and removed by uninstall, blocks the AirPods at shut down instead. It
cannot disconnect them, so the AirPods may stay connected until the computer
is off.

For what this does and does not guarantee when the PC is actually shut down,
see [requirements.md](requirements.md#what-it-does-not-do) and
[verification.md](verification.md).

## The AirPods widget

Alongside the tray icon, Earshot can show a small battery gauge on the
taskbar, and from it a card with more detail. It works by listening to the
AirPods' own Bluetooth broadcast, the same short signal a phone reads for its
own battery widget, rather than needing them connected to this PC. That is
also why it has firm limits; see [below](#what-the-widget-cannot-know).

**The gauge.** A small readout on the taskbar, the way a laptop's own battery
indicator sits there: the earbud mark, the lower-battery bud's percentage as
a small bar with its number, and a charging mark when it applies. If there is
no free space on the taskbar to put it, or it cannot attach to the taskbar at
all, Earshot falls back to the ordinary tray icon on its own, without asking.

<img src="images/widget-gauge.png" alt="Earshot's taskbar gauge, drawn from made-up values" width="220" align="right">

<p align="center"><i>The taskbar gauge, drawn by Earshot's own code from made-up values (see the note below), not a screenshot of a real device.</i></p>

<br clear="all">

Left-click the gauge, or the tray icon when that is what is showing, to open
the card. Turning on **Left click connects straight away** in the menu
changes that: a left click then connects or disconnects immediately, the way
the tray icon alone used to. It is off by default, so a click opens the card
first.

**The card.** Three battery gauges, left bud, right bud and case, each with
its own charging mark; one line for where the AirPods are ("On this PC", or
the owner's own label such as "On your iPhone" when they are in use
somewhere else); one line for when the battery was last read; and a Connect
or Disconnect button, whichever they are not already doing.

<img src="images/widget-card-light.png" alt="The widget card in the light theme, drawn from made-up values" width="320" align="right">

<p align="center"><i>The card in the light theme, drawn by Earshot's own code from made-up values, composited onto one measured sample of Windows' own card backdrop colour. Not a screenshot of a real device, and not the translucency Windows itself draws.</i></p>

<br clear="all">

<img src="images/widget-card-dark.png" alt="The widget card in the dark theme, drawn from made-up values" width="320" align="right">

<p align="center"><i>The same card in the dark theme, drawn and composited the same way.</i></p>

<br clear="all">

**The case-open card.** When the AirPods' own case is opened near the PC,
the same card appears by itself near the taskbar for a few seconds, then
closes itself, using the same timing Windows' own notifications use on this
PC. It never connects the AirPods by itself: opening a case only ever shows
the card; pressing its button still takes a real click. A setting, **Card
when the case opens**, turns it off.

<img src="images/widget-case-open.png" alt="The case-open card, drawn from made-up values" width="320" align="right">

<p align="center"><i>The case-open card, drawn and composited the same way as the card above.</i></p>

<br clear="all">

**The low battery alert.** A Windows notification, or the card if a
notification cannot be shown, the moment a part first reads at or below a
threshold, 20% by default and adjustable in steps of 10 from the menu. It
only fires again once that part has read at least one 10% step back above
the threshold, so a value sitting on the edge does not repeat itself.

**Pausing when a bud comes out.** A switch on the card, on by default, that
pauses whatever this PC is playing to the AirPods the moment a bud is taken
out, and never resumes something the owner paused himself. It only ever acts
once in-ear state has actually been proved to work from the AirPods'
broadcast (see the honest state below); until then the switch does nothing.

**The menu.** `Show on the taskbar`, `Card when the case opens`, `Low
battery alert` with its threshold, `Left click connects straight away`, and
`Name your other device...`, which sets the label used for "On your
&lt;name&gt;".

### The honest state today

Battery, charging, in-ear state and the case-open card all wait on one
thing: a short recording the owner has not made yet, holding his own AirPods
and his phone's own battery reading beside the PC. Until that recording is
made and checked, Earshot cannot tell his AirPods apart from anyone else's
well enough to show anything about them. So, right now:

- **The card shows "No reading" for every battery figure**, and nothing
  else: no charging mark, no in-ear mark. The 70%, 60% and 90% in the
  pictures above are made up to preview the finished layout; they are not a
  figure the widget can show yet.
- **No AirPods can be claimed as the owner's**, so nothing is shown, no
  case-open card appears, and no low battery alert can fire.
- **The pause switch is on in the menu but has nothing to act on**, for the
  same reason.

Nothing about the widget, including this honest "No reading" state, has
been tried on the real hardware yet. The live test that covers it is
written and waiting to be run.

### What the widget cannot know

- **The other device is the owner's own label, not a reading.** "On this
  PC" comes from Windows itself. "On your iPhone", or whatever the owner
  types, is only his name for "in use, somewhere that is not this PC"; the
  AirPods never say what they are actually connected to.
- **Battery is a snapshot, never live.** It is read in steps of 10% (0, 10,
  20 … 100) from the last broadcast Earshot heard, and the card says how
  long ago that was. It is never interpolated and never shown as current.
- **In-ear detection is only as good as the broadcast.** If the AirPods stop
  broadcasting while worn and playing from this PC, Earshot has no fresher
  reading to show, and says so rather than guessing.
- **The widget hears everyone's AirPods nearby, not only the owner's.** A
  room full of the same model broadcasts the same way his do. What keeps a
  stranger's AirPods off the card is the rule below.

### Whose AirPods it shows

Once, with his own AirPods' case opened near the PC, Earshot records their
model, colour and the strength of that broadcast as the owner's claim. After
that, a broadcast is only shown as his if all of the following hold:

- the model and colour match the claim;
- the signal clears the strength recorded at that claim;
- and the battery is consistent with the last reading Earshot has for him:
  the same, lower, or one step (10%) higher, and higher by more than that
  only while the matching charging mark is set.

Being connected to this PC does not, by itself, count as proof: the same
checks run every time, connected or not. If a jump the rule cannot explain
happens, Earshot stops showing anything until the owner opens the case by
the PC again, which starts a fresh claim.

Short of all of the above, Earshot fails closed: no battery, no case-open
card, no pause. Nothing is recorded about an unmatched device beyond a count
of how many were seen.

**The accepted risk.** Because "the same or lower" always passes, a
stranger's AirPods of the same model and colour, near the PC with a lower
battery reading than the owner's last one, could be shown as his. This was
put to the owner plainly, and he chose to accept that risk rather than make
the rule stricter and risk missing his own AirPods on a false alarm.

### What still needs a kernel driver

Noise control, battery to the nearest 1%, and a few other AirPods features
are not built, and are not close to being built, because of what Windows
does and does not let an ordinary program do. See
[architecture.md](architecture.md#the-airpods-widget) for the full list and
why, with sources.

### Privacy

The widget only listens. It never connects to, or sends anything to, a
Bluetooth device to get this data, and it does not need the AirPods paired
to this PC at all. Nothing about a device that turns out not to be the
owner's is kept, beyond a count of how many were seen. The one-time
recording the widget still needs before it can show anything real stays on
this PC; it is never part of the repository.
