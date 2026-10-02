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
back are both on. Hand back reads as off when it is absent from the machine
folder, which is the case until the tray has first started and copied its
choice there (a new install does that on its first start). If either is off,
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

**Microphone off mode.** An opt-in alternative on the settings page, off by
default. Turning it on turns Protect audio quality off, so the Hands-Free link
stays up. The row's Open button opens Windows' own sound settings, at the AirPods'
capture endpoint when its id is well formed (`{0.0.1.<8 hex>}.{<GUID>}`, the form
a probe of this PC saw; Core Audio documents endpoint ids as opaque) and at the
list of sound devices otherwise, with one line of guidance: set the AirPods
microphone to "Don't allow". Earshot cannot
switch that microphone off itself: the Core Audio documentation lets a program
read an endpoint's state and gives no call that sets it, and the interface that
does is undocumented, so Earshot does not use it. The mode changes only the
saved protection choice. The idle block, the boot block and the hand-back still
select the AirPods' nodes by container and address, so a Hands-Free service
the mode leaves installed is blocked with the rest and the AirPods stay unpaged
at rest. Choosing Protect audio quality on its own turns the mode off. Whether
call quality is acceptable with the mode on is unproved until the owner has
tried it; the full block stays the default.

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

**Which AirPods it shows.** The owner opens the case next to the PC, and nothing
else is asked. Earshot reads the paired device's model from Windows (the device's
own record, never a guess) and keeps only messages of the documented 25-byte
form of that model. The two buds of one set broadcast from two addresses, with
the bud values in swapped order, so senders with the same model, colour and case
value, and the same two bud values in either order within two seconds, are
merged into one set.

*Linking.* A pair in a case with the lid open sends a message with the case level
known, about four times a second between its two buds. A pair in use sends one
with the case level unknown (0xF) every second or two. That level is what tells
the owner opening the case from every pair worn nearby, so a set is linked only
when it sends at least five case-known messages inside five seconds, the first
and last two seconds apart, with a median signal of at least -70 dBm. Of sets that
qualify at the same message the strongest is linked; in practice the first set to
qualify is, and a pair that opens its case later but at least 8 dB stronger takes the
link from it (below). A pair worn nearby, however near, is never linked. The set's
colour is learned and held. The figures are named constants in
`BroadcastRules` (`src/Earshot/Widget/BroadcastSenderSets.cs`), and they are
design choices, not facts about the device. -70 dBm comes from the saved
records of the owner's pair with both buds in the case and the lid open (the 30
September capture and two set-up records of 1 October, about 300 case-known
messages): the median over each run of five consecutive messages (both buds together,
in time order) ran from -72 to -51 dBm, the run in the middle of each record read -54,
-61 and -58, and about 95% of the weakest record's runs were above -70. A test replays
those three records and holds these figures. A pair worn nearby on the evening of 1 October, which was shown
as the owner's, read -64 to -76 dBm with the case level unknown. So no level
separates the owner from a same-model stranger who opens a case at the same
distance. The level only keeps out a pair that is not next to the PC.

*Following.* The link stands on its anchors, the senders that were in the set
when it was linked. A sender that only merged into the set (it said what the set
said, close in time) is not an anchor until it has matched anchor messages three
times over three seconds, and one of its messages counts as the linked set's only
when it matches an anchor's message within two seconds, so a stranger that
matched once never has its different messages shown. The addresses rotate. When
every anchor has been silent for longer than the ten second window, a set whose
fields continue the linked set's last (the same model, colour and two bud levels, and
the same case level unless either message gives none, since a pair sends it only with a bud in the case), heard for two seconds with three messages and within 30 seconds of
the old address's last message, is followed under its new addresses, and the values
already shown stay. A set with other fields is never followed, however near and
however long it is heard. A case opened during the loss links as it would with no
link. A different pair that opens its case at least 8 dB nearer than the linked set
takes the link, which is how the owner corrects a link made to somebody else's
pair: open the case next to the PC.

*Dropping.* When the linked set has not been heard under any address for two
minutes, the link is dropped, what this link said is cleared from memory, and only
the saved last readings (below) are shown until the next case open. Two minutes covers an address change, which takes seconds, and
a pair put back in the case for a moment. It does not cover a session. It is a
margin, not a measurement: the saved records do not show when the addresses
rotate. A clock check every ten seconds notices the silence, since silence brings
no message.

*In memory only.* The link is held in memory and nothing about it is written, no
address included, so a restart of Earshot (an update restarts it) has no link, and
the next case open makes one. What is written is the last reading of each part
(below), never the link. A message from a device that is not linked is counted
and nothing else about it is recorded. The log says that a link was made, followed
or dropped, with counts only.

**Opening and closing the case.** `CaseOpenTracker` works out from the linked
pair's own messages, and no other pair's, when its case opens and closes; the
case-open card (below) is shown and closed from it. An open is either the
linked pair's case-known messages starting after silence (a case-known message
with none before it for the close time, or none since the link was made, so
the case open that links a pair is an open too), or its lid open counter
changing while those messages never stopped (the lid was shut and opened again
inside the close time). The counter is the one-byte "Lid Open Counter" the
furiousMAC notes list right after the charging and case byte, "Counter for
opening lid" (github.com/furiousMAC/continuity,
messages/proximity_pairing.md); the whole byte is read, as the notes give it.
Each sender's counter is compared only with that sender's own last value,
since nothing says the two buds carry the same value, and the first value from
a sender is only a baseline; a change from the other bud within two seconds of
an open is the same open. A case-unknown message (0xF, buds in use) never opens
anything, and a closed case sends nothing, so worn buds and a shut case never
show the card.

A close is the linked pair's case-known messages stopping for eight seconds.
No source documents a bit that says the lid is shut, only the counter of opens,
and the in-ear bits have one note only, so the cadence closes it; a timer due at
that time notices the silence. Eight seconds is a design choice from the
cadence the saved records show: with the lid open the set sends about four
case-known messages a second between its two buds, the longest gap in a merged
set's messages in any record is 3.11 s, and the longest from one sender 6.14 s.
With one bud in the case only that bud may give the case level, so the
single-sender gap is the one to clear: eight seconds is about 30% over it and
over two and a half times the set's longest gap, while a shut case closes the
card eight seconds after its last message. Buds taken out of an open case close
it the same way, since their messages then say the case level is unknown.
`IWidgetStatus` raises `CaseOpened` for every open and `CaseClosed` once for
each open whose case has closed, including when the link is dropped or moves.

**The accepted risk.** A same-model pair that opens its case next to the PC more
strongly than the owner's can be linked instead, and a same-model pair whose
fields equal the linked set's last, heard within 30 seconds of it going quiet, can
be followed as if it were the owner's. And once the linked pair has been unheard
for more than ten seconds (the case shut, or the buds out of range), any same-model
pair that opens its case at -70 dBm or stronger is linked at once, with no margin to
clear: the 8 dB margin is asked only while an anchor of the linked set has been heard
inside the ten second window. These need a stranger of the same model
(and, once one is held, the same colour) to be close and, for the first two, to match. A pair
that is linked this way is read like the owner's: what it says is saved as the last readings,
replacing the owner's, and is shown and estimated from, until the owner opens their own case next
to the PC again and the link moves back (a pair that does not take the link is never saved). The case-open
card follows the link: an open that links a pair by these rules, including one that moves the link to a
pair 8 dB nearer, counts as an open and shows the card, and a set that does not take the link never does. This is an
owner decision, not an oversight: the owner accepted that risk rather than ask for
a set-up step to rule it out. There is no consistency check against another
device.

**What is shown, and for how long.** `BatteryFreshness.Shown` is the one
definition of what the card, the gauge and its tooltip show. The owner's pair is
shown wherever it is. A part's value is one of three kinds:

- *Live*: a reading of the linked set heard by this run within 30 seconds, drawn
  as current. In use the set sends about 35 documented messages a minute; a closed
  case sends nothing, so values stop being live about half a minute after the lid
  closes.
- *Last*: the last reading of the part, of any age, drawn in the stale style
  (tertiary ink) with its age, until a newer reading is heard.
- *Estimated*: a last reading of a part that was charging, grown at its rate
  (`ChargeEstimator`), drawn like a last reading with "≈" before the value and the
  age of the reading it grew from; the tooltip says "Estimated".

From the linked set Earshot reads the left bud, the right bud and the case, each in
steps of 10% (that is what the message carries), and the charging bits. Each fresh
reading of the linked set, and of nothing else, goes into the last reading store
(`LastReadingStore`, `last-reading.json` in the widget folder, under
`EARSHOT_DATA_ROOT` when that is set): per part the value, the charging flag, the
read time and the model, and nothing about the sender. A changed value, flag, model
or rate is written at once and a read time alone at most once a minute, and the
file is read at start. A saved reading is shown only when its model is the paired
model, and is never live, since this run did not hear it. A dropped link or a
restart therefore leaves the last readings on show.

The same store object also keeps which parts have had their fully charged notice
(`ISpentStore`, a `SpentMark` per part: the read time and value of the reading it was
spent on), so the notice is one per charge across a restart. A mark counts only while
the part is still shown from that reading, and a live reading below 100 re-arms the
part. The same rules as the readings apply: nothing is written over a file that could
not be read or is of a newer schema.

Beside it, `battery-history.json` (`HistoryStore`, same folder and data root) keeps the
live readings of the linked pair for the history page: per sample the part, the level,
the charging flag and the time. Never a last reading, an estimate or Windows' figure,
and the file's shape has no member for an address, a name or a model. One sample per
part per minute at most, 7 days kept, and the same load and write rules as above.

*The estimate.* A part charging at its last reading rises from it at its rate until
100, then stops. Buds charge only in the case, so a bud that was not charging, and a
case that was not itself charging, keep their value. A clock behind the read time
gives no rise, and the service hands in the latest time it has worked anything out
at. An estimate is also never shown lower than one already shown for the same
reading (`EstimateHighWater`, keyed by the reading's read time and value, raised by
`BatteryFreshness.Shown` for every surface and written into `last-reading.json`), so a
clock put back, or a restart with the clock set back, never makes an estimate fall.
A newer live reading replaces the estimate, even with a lower value: it is a reading.
Each book written carries a sequence number taken under the service's lock, and the
store drops one that arrives after a later one. A file that could not be read (locked,
corrupt, cut short) is not written over until it can be read or is removed. Rates are per model and part (`ChargeRates`): one rate for both buds of
a model and one for the case, learned from the owner's own live readings by
`ChargeRateLearner` (two steps up of one part, both while charging, the first seen
within two minutes of the reading before it, the last below 100%, 15 minutes to
3 hours apart; a new sample replaces the rate held) and kept beside the last readings.
There is no case estimate until a charge of the case has been learned. The buds of
one model have a seed from Apple's published fast-charge figure (one hour of eight
hours of listening in five minutes, 150 points an hour), which is off until the
owner has confirmed the wording (`ChargeRates.UseAppleBudSeed`).

*The gauge.* On this PC its number is the lower bud, live or else last or estimated
in tertiary ink, or Windows' own figure while that is current and no bud is live.
Not on this PC, a case mark (our own shape, never in the accent colour) takes the
earbud mark's place, and the ring and the number are the case's last or estimated
value in tertiary ink, with the bolt if the case was charging; with no case value
the gauge keeps the faded earbud mark. "On your iPhone" keeps its phone mark, with
the case in its tooltip. The low battery alert still acts only on live values with
the AirPods on this PC.

**Left and right.** Which bud is the left and which the right rests partly on
a published description of the message and partly on one local capture. No
permitted source documents the status bit that swaps the two, so the
capture's reading of it is unproved, and with it which physical side a figure
belongs to. The charging bits come from the labels of a published figure and
are not proved here either. In-ear and a lid open or shut bit are in neither
source and in no saved capture, so they are not decoded; the lid open counter
is, from the furiousMAC notes (see Opening and closing the case).

**Windows' own Hands-Free figure.** When no bud has a current broadcast value
and the AirPods are on this PC (linked or not), Earshot also reads the Hands-Free battery
property Windows may hold for the device. It reads the paired device's device
nodes only, once a minute, and the figure counts as current for two minutes.
The slower query through the paired device's association object (about a minute
each, measured) is used by the sweep (`diag battery-sweep`) only, never by the
background read and never by `probe battery`, which reads the device nodes once
and says so. With Hands-Free off, which is the default, it was seen empty
on this PC. Whether Windows fills it with Hands-Free up has not been observed
here. The figure is one number for the headset, never a bud's or the case's.

**Refresh.** The card has a refresh control and the menu has **Refresh
battery**. It restarts the listener and waits up to 12 seconds for a message
of the linked pair, or, when no pair is linked, for the case-open link. It ends
on values, on Windows' figure when that is what it finds, or on "Nothing heard.
Open the case.", because a shut case sends nothing. It also ends when Bluetooth is off or the listener is not running, and
says so. The 12 seconds is a design choice sized to the longest gap seen
between messages.

**Ear detection.** Pausing when a bud comes out, and playing again when it
goes back, is built but inactive: there is no documented in-ear value to read,
so the settings row says "Earshot cannot yet tell when a bud is in your ear."
and nothing acts. The resume half is strict by design. It resumes only the
session Earshot paused, only within 60 seconds of the pause, only while the
AirPods are still this PC's output, only if nothing was played or paused by hand
since as far as Windows reports it, and only when every bud that was in is back
in on fresh values of the same linked set. "As far as Windows reports it" is the
session manager's own change events (a session came or went, a session's playback
info changed), which cancel the remembered pause, plus a read of the sessions just
before resuming that must find the paused session paused and nothing else playing.
If those events are not being listened to (the listening starts the first time the
manager is read and has had no live run), nothing is resumed. In-ear values and a
remembered pause belong to one linked set: when another set is linked, or the
same one is followed to new addresses after it went quiet, they are forgotten. A resume that
misses any of these is forgotten, never retried later. Pausing when the AirPods
leave this PC never resumes.

**The taskbar gauge.** Earshot does not use a taskbar docking API. The gauge is
a topmost, layered overlay window, owned by the taskbar it sits on (an owned window is always above its
owner in the z-order, so the system keeps the gauge above the bar wherever the shell raises the bar;
https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#owned-windows), positioned over free taskbar space, which it finds by
reading the taskbar's own button layout through UI Automation and polling it
for changes; the window's alpha-zero pixels let a click reach the taskbar
underneath rather than the gauge
(https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#layered-windows).
It draws the earbud mark with a ring in the Windows accent colour, filled to
the lower bud's battery, and that number. The setting **Gauge order** puts the
ring, the number and the charging bolt in one of six orders.

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
value means the main display. The reserved value `*all-displays` (`GaugeDisplayChoice.AllDisplays`)
means every display's taskbar at once; no interface path or `gdi:` name starts with `*`, so it
cannot be taken for a display. Displays are listed with `EnumDisplayMonitors`,
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

**All displays.** The main display's gauge is the ordinary one, read as the main display always is.
Every other connected display that shows a taskbar of its own (a visible `Shell_SecondaryTrayWnd` on
its monitor) gets a `SecondaryGauge`: its own `GaugeController`, window, `TaskbarWatcher` (one UI
Automation worker thread reading that display's taskbar, as `Read(shown, displayId)` does for a
chosen display) and its own scale. Each gauge is therefore placed, raised and hidden for a
full-screen window by the same code as the main one, against its own display's bounds. A gauge
that its display's own taskbar covers (a `Shell_SecondaryTrayWnd`, where the main gauge's is a
`Shell_TrayWnd`) is raised when the foreground window or a shell window changes, and the sliding
raise limit is counted per controller. `SecondaryGaugeSet.Reconcile` makes the set match the
displays and taskbars that are there now; it runs on the UI thread after each read of the main
taskbar, on `WM_DISPLAYCHANGE` and when the setting changes, adds a gauge for a display or taskbar
that appeared and stops and disposes the gauge of one that went (watcher first, then controller
and window), and a result already posted for a gauge that was removed is dropped. A taskbar window
whose rectangle could not be read removes nothing. A read that fell back to another display's
taskbar, or that is of the main taskbar with no fallback to name (the display has become the main
one), is no taskbar for that gauge and is not drawn. The foreground and shell-window hooks, the
appbar notices and the pokes go to every gauge. The tray icon goes through one vote per controller
(`TrayIconVotes`) and is visible only when no gauge wants it hidden; once Earshot is closing the icon
is held hidden, so taking the gauges down does not show it again. A click on a gauge opens the
card above that gauge's own rectangle, which `WidgetCardPlacement.WorkAreaFor` places on that
display, at that display's scale. That scale goes with the show request and the card keeps it while
it is open; the main gauge keeps the scale of its own last read, so a read of the main taskbar
neither changes the card's scale nor is drawn at the other display's. Choosing another value, turning
the gauge off and closing Earshot each remove every secondary gauge at once. `GaugeAllDisplaysTests`
covers the set, the per-gauge raise, the icon vote, the card's placement and scale and those removals
with fake displays, taskbar windows and gauge windows through the real tray; the real secondary
taskbar read stays under the existing real UI Automation test, which finds none on a private desktop.

If the chosen display is not connected, or is connected but its taskbar is not
shown, the reader returns the main display's taskbar and says why in the
layout; the controller writes one line when the reason changes, and another
when the display returns. `WM_DISPLAYCHANGE` and `TaskbarCreated` already ask
for an immediate read, so the return is seen at once. The card takes the work
area of the display the gauge is on; the case-open card takes the work area of
each display it is shown on.

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
The gauge is the taskbar's owned window, so the taskbar cannot be over it. Owning a window of
another process also joins this thread's input queue to Explorer's taskbar thread. No Microsoft page
says so; a probe on this machine showed it (the shell's `GetActiveWindow` answered with the gauge, and
`AttachThreadInput` with FALSE succeeded right after the owner was set and failed with error 87 when
the threads were apart). Earshot separates the two straight after it sets the owner
(https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-attachthreadinput); the probe
held the gauge above a shell window that raised itself hundreds of times with the queues apart, and the
separation survived showing, activating and focusing the gauge. A gauge that cannot be separated is not
left owned, and the raw code is logged. Before a wait that holds the UI thread up (the hand-back at shut
down and sleep, the closing of the tray) every gauge is taken off its taskbar's ownership, and is owned
again by the first layout after the machine wakes. A gauge that
could not be owned, or that another program's topmost window covers, is raised again when
that happens, and the poll finds anything the hook missed. A raise that does not hold is not
repeated at once: the next one waits a quarter second, then half a second, and so on up to
eight seconds, until the gauge is found on top a second after a raise. Every hide, show, cover and
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
instead of the translucent one. The card paints itself into a bitmap and copies the
finished pixels to the window in one step, never a clear followed by drawing on the window, which would
show the backdrop alone for a moment; a repaint that was asked for is compared with what the window
shows, and only the pixels that differ are invalidated, so most status updates touch nothing. It
opens above the gauge, closes
when it loses focus, and works from the keyboard.

The case-open card (`CaseOpenCardPresenter`) is the same window in separate,
never-activated instances, one per display it is shown on. It is shown when the
linked pair's case opens (Opening and closing the case, above) and is on by
default, for a new install and an existing one alike: the setting is a member
v1.4 added, `CaseOpenCardOn`, so a file written before it reads as on, and the
old `CaseOpenCard` member is no longer read (the card could never show before
v1.4 and its switch was hidden, so whatever that member held was never the
owner's choice of this card). It is the main card: live L, R and Case, the
Where line reading "Case open", and the Connect or Disconnect button, with a
close button (the Cancel glyph) where the gear is. It closes when the case
closes, after its own close time when one is chosen (5, 10, 30 or 60 seconds;
the default is when the case closes), on its close button, on a click on it
that misses its buttons; a press of Connect or Disconnect leaves it open, its
button turning to Disconnect or Connecting in place; one close closes every
display's card. The passive watcher now runs for it even with the gauge hidden
(listen-only, nothing is paged): `Enabled` is recomputed from the four consumers
when the settings load, so an older file that saved it off still has the watcher
running while the card is on. The close time replaces the dismiss time it used to
read from `SPI_GETMESSAGEDURATION`, which is no longer read. It goes where the
gauge is by default (the display Gauge display names; with All displays, the
main display), or on all displays, or on a chosen set of displays, stored as
the monitors' device interface names as Gauge display stores one; a set none of
whose displays is connected falls back to where the gauge is. On a display with
a gauge it sits above the gauge, elsewhere in the corner of the work area by
the taskbar's end, drawn at that display's scale. It is never shown on a
display a full-screen application is on, by the gauge's own rule:
`SHQueryUserNotificationState` saying a full-screen application runs is global,
so the foreground window must cover that display's bounds; with one display the
state alone says it, and presentation settings, quiet time and a locked or
switched session keep it off every display. It is still shown on the other
chosen displays, and a full-screen application that comes to the front later
(the foreground hook, `ABN_FULLSCREENAPP`) closes that display's card only. It
never takes the focus: `WS_EX_NOACTIVATE`, shown without activation
(`Form.ShowWithoutActivation`), and `MA_NOACTIVATE` for a mouse press. A screen
reader is told of each open once, from one card, through a UI Automation
notification
([`AccessibleObject.RaiseAutomationNotification`](https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.accessibleobject.raiseautomationnotification)),
for example "AirPods case open. Left 70%, Right 70%, Case 50%.", a part that is
not live saying what kind of value it is and how old; its two buttons are
described to a screen reader and never focused. Its Connect or Disconnect button
only ever fires on a genuine click on that button; nothing else in its code
path can press it.

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
drawing and layout code the real widget uses. `--set design` renders the design
set instead: every gauge state and each of the six orders, and the card with its
pages (fresh, greyed, refreshing, refresh with nothing heard, fresh and greyed
again with Windows' text size at 150%, settings, update available, update
downloading), each on light and dark at 100% and 150% display scale. No device, no window shown on
screen, and no `IWidgetStatus` connection: see
[docs/overview.md](overview.md#the-airpods-widget) for what the pictures
made this way actually show and do not show.

**The settings page.** The gear on the card opens a page drawn by the card
itself, with no child controls. Its rows are read from the real settings each
time it is drawn, so a row shows what is saved and never what was last asked
for. Every change goes through the same path the tray menu's own item uses.
The rows are, in order: Position, Display (a button that steps through
Main display and the connected displays), Order (six pictures of the gauge),
Other device, Pause on removal, Pause on leave, Low battery (the
threshold), Case-open card (a switch and a chevron that expands the row to its
Close choice, a button that steps through Until the case closes, 5 s, 10 s,
30 s and 60 s, and its Displays choice, a button that steps between Where the
gauge is and All displays, with a box for each display when there is more than
one), Click connects, Hand back, Microphone off (with a Sound settings
button while it is on); then the shortcuts for Connect and
Disconnect; then the installed version with Check, Repair when an install
exists, and Auto check. Each is an icon and its words; what each row does is
its tooltip. The Pause on removal row carries a caption saying Earshot cannot
yet tell when a bud is in the ear, while there is no in-ear value to read. The
card and the settings page follow the Windows 11 look: text size, accent
colour, light and dark theme, reduced motion and keyboard focus, read at run
time and re-read when Windows changes them.

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

**Earshot is running again afterwards.** The elevated update ends before the new
release's own install does, and a program an elevated process starts is elevated
too, which the tray must never be, so the install is what starts the tray
(`TrayRestarter`). The install verb takes the same three values as before, so the
one a 1.2.x tray's update starts behaves the same: what tells it that it is the
end of an update (or of a repair by download, which runs as one) is the
`Installing` record the update run left in `update-outcome.json`, still inside its
ten-minute window. When such an install finishes with `Success`, it records
`Installed` first, so the tray that starts reads it and says once how the update
went, and then starts the installed `Earshot.exe` for the user whose SID it was
given. A first install, a setup by hand, a repair run from the installed copy (the
tray is not closed for that one) and an install that did not finish start nothing:
the tasks and the service are in place only after a `Success`, and the tray it
starts blocks idle AirPods as any tray does.

The start is a one-shot Task Scheduler task, because that is the documented way for
an elevated process to run a program with the user's own interactive token and the
least run level. It is registered as `\Earshot\StartTray` (the folder only
administrators and SYSTEM can write, read again first and refused if anyone else
could write it) with the user's SID, `TASK_LOGON_INTERACTIVE_TOKEN` and
`TASK_RUNLEVEL_LUA` (least privilege: the tray gets the user's own token and no more, and nothing here
elevates it; with User Account Control off, or for the built-in Administrator, that token is already an
administrator's and this does not lower it), one `Exec` action (the installed `Earshot.exe`, no arguments:
it is a start by hand, so the tray does not treat it as the start at sign-in), no
trigger, no time limit (`PT0S`, or the scheduler would end the tray after its default
three days), normal task priority, and security that lets the user read the task
and nothing more. It is run at once with `RunEx` and deleted with `DeleteTask`;
a task an earlier run left behind is deleted first. Nothing the signed-in user can
write is read or run. The shell route (`IShellDispatch2.ShellExecute` through the
desktop's shell window) was not chosen: it gives no return code to record, it starts
the program as whoever runs the shell instead of the SID the install was given, and
it needs the shell to be running. No tray is started when one is already running in
this session: the probe opens the tray's own single-instance lock for the session
only (a lock that exists but cannot be opened by this account counts as held), so a
second start never relies on the lock to turn itself away, which would show the
running tray's card.

Nothing here can fail the install. A user who is not signed in, a task that does not
register or run, or a tray that does not appear within fifteen seconds is a step with
its raw code, a log line, and the sentence that Earshot starts at the next sign-in if
Open on startup is on, or when it is started. After the task is removed the tray is
looked for once more and the step says what was seen: Microsoft does not say what
removing a task does to an instance that is running, so that is recorded rather than
assumed. https://learn.microsoft.com/en-us/windows/win32/taskschd/security-contexts-for-running-tasks,
https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-iregisteredtask-runex,
https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-itaskfolder-deletetask

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
- Real-time in-ear detection and whether the lid is open or shut. No
  documented source gives either in the advertisement, so ear detection stays
  off and the case-open card closes by the messages stopping rather than by a
  lid bit; whether the advertisement gives them at all is still an open
  question. The lid open counter is documented and is read.

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

**What it does, in order.** It refuses an administrator shell, because a tray
started from it would hold an elevated token the user's own sign-in does not give it. It reads the latest release once, with
PowerShell's own default headers. It downloads that release's zip and its
`.sha256` into a new folder under `%TEMP%` with a progress bar (plain percent
lines when output is redirected), and checks the zip's SHA-256 before anything
is unzipped; any mismatch ends the run with one plain line. It then runs the
unpacked copy's read-only `Earshot.exe probe setup-values --out <file>`, which
reports the signed-in user's SID, the AirPods' address and container, and what
is installed, so the script never asks for them. It closes a running tray
through the tray's own Exit (`Earshot.exe --exit`) and never stops the process.
When Hand back is on, that Exit lets go of AirPods in use and blocks them
first; with it off, Exit leaves them as they are. Then it shows the one
administrator prompt. `-DryRun` does everything up to that prompt and prints
the command it would have run, and changes nothing the install would change:
not the settings, not the Open on startup entry, not a per-user copy. The one
thing it writes is Earshot's own log, because the unpacked copy's read-only
`probe setup-values` writes a line there as every probe does. After an install,
update or repair it starts Earshot unelevated; after an uninstall it starts
nothing. An update's install starts the tray itself as well (see
[Updates](#updates)), so the script's own start, a moment later, finds the tray
running and only brings up its card.

**When a run stops.** A run that stopped after it closed the tray starts that
tray again, so Earshot is not left closed until the next sign-in (the closed
tray's program must still be there). It does not when the run reported success,
and it does not when the elevated program may still be working in the install
folder: a first install or an update still running when the script stopped
waiting (the script says so, and that the install was left to finish) is not
started over. The hold begins before the administrator prompt is shown and ends
when the elevated program's exit code has been read (for an update, when its
record has been read). A declined prompt or a failed launch ends it at once,
because no program was started. Ctrl+C while the prompt is still shown leaves the
hold on, because approving the prompt afterwards starts the program: the script
says that Earshot was not started and that setup may still be approved and run.
Ctrl+C while the program works leaves it to finish, says that setup is still
running, and starts nothing. Both messages say to start Earshot from the Start
menu, or from the installed program's path, once setup has finished, because
running the line again on an install that is current prints done and starts
nothing. A Ctrl+C before the prompt, or after the program has reported, starts
the closed tray again. Otherwise the restart runs
whenever the script ends after closing the tray, including when it is stopped
with Ctrl+C, and it is skipped for a tray that is still running (one that was
slow to close). Closing the PowerShell window itself is not covered; then start
Earshot by hand.
The waits are budgets chosen in the script, not measured: 60 s
for the tray to close, a little over the 45 s the update gives it, and 300 s,
as long as a first install's own wait, for the update's record.

**Which verb gets the prompt.**

| Action | Installed copy | What is elevated |
|---|---|---|
| Install | none, or unusable | the verified copy's `install-zip <zip> <sha256> <userSid> <address> <containerGuid>`, which copies the zip into a folder only SYSTEM and Administrators can use, hashes the copy against the value on its command line, checks every file against the release's file list, and runs that release's `install` from there; it refuses with exit code 26 when a usable install is already there |
| Update | usable and older | the installed `Earshot.exe update ...`, the same verb the tray's Update uses, so it works against an install made before the script existed; the script waits for the outcome record in `%ProgramData%\Earshot\update-outcome.json` |
| Repair | usable | the installed `update` verb with the zip of the installed version's own release, so a repair never changes the version |
| Uninstall | usable | the installed `Earshot.exe uninstall`; with the program gone, or its folder not usable, the latest release's verified copy runs `uninstall` instead |

Uninstall downloads and checks the latest release too, even when a program is
installed, because the installed program cannot vouch for its own folder:
the checked copy's `probe setup-values` says whether that folder is one only
administrators can change, and only then is the installed program run as
administrator. Uninstall therefore needs the network.

Install over a current install, and Update when nothing is newer, say so and
change nothing. Uninstall keeps `%APPDATA%\Earshot` and
`%LOCALAPPDATA%\Earshot` unless `-RemoveSettings` is given (the menu asks);
it removes the Open on startup entry only when its value is exactly the quoted
path of Earshot's own program (the installed copy or the per-user copy) and
`--startup`. The pairing is never touched.

**No AirPods paired yet.** The installed program cannot be set up without a
device to set up for, so the script installs nothing elevated. It copies the
verified release to `%LOCALAPPDATA%\Programs\Earshot` for the signed-in user,
starts it, and says to pair the AirPods and run the line again. That second run
takes the normal route and removes the per-user copy once the install has
finished. **The per-user copy does not stop the PC paging the AirPods.** It has
no part that runs before sign-in, so once the AirPods are paired they are paged
at boot as before until the install for the machine is done. That is why it is
offered only when none are paired. With several paired, or a list Windows did
not give, the script stops with one plain line instead of placing a copy that
would look like protection and not be: for several, remove the ones that are
not yours in Bluetooth settings and run it again.

**New installs.** A PC with no settings file gets Hand back on shut down,
sleep and Exit on, and Open on startup on. The script's last step starts the
tray, whose first start writes both and copies the Hand back choice to the
service, so the AirPods are handed back from the first shut down. A settings
file that exists is never changed by an install, an update or a repair.

**What the checksum does and does not prove.** The script, the zip and the
checksum come from the same release. The checksum therefore catches a damaged
or cut-short download; it does not catch a compromised release, because
whoever could change the zip could change the checksum and the script with it.
What the elevated verbs add is narrower, and real only while the program doing
the checking has not been swapped: what lands in Program Files is exactly the
zip whose hash was fixed on the command line when the prompt was shown, copied
first into a folder no ordinary program can write to. On an update, a repair
and an uninstall of a usable install that program is the installed,
administrator-owned `Earshot.exe`, and the script runs it as administrator only
after the checked download has found its folder one only administrators can
change; otherwise the download's own copy does the work.
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
| `Earshot.exe probe widget --out <folder> [--set design]` | Writes the taskbar gauge, the card and the case-open card to files, from fixed synthetic snapshots, at three DPIs and in both taskbar inks. With `--set design` it writes the design set instead: every gauge state and order and the card's pages, light and dark, at 100% and 150% display scale. See [The AirPods widget](#the-airpods-widget). |
| `Earshot.exe install <userSid> <address> <containerGuid> [--principal user]` / `Earshot.exe uninstall` | The one-time setup and its removal. Both need an elevated administrator and refuse to run as SYSTEM. The menu runs `install` with those three arguments filled in; a bare `install` is refused, so it is not a command to type by hand. |
| `Earshot.exe install-zip <zip> <sha256> <userSid> <address> <containerGuid>` | The elevated half of a first install by the install script, run from the verified download's own copy, never by hand. It refuses (exit code 26) when a usable install is already there. See [The install script](#the-install-script). |
| `Earshot.exe probe setup-values [--out <path>]` | Read-only. Writes JSON with the signed-in user's SID, the AirPods' address and container, whether one device could be chosen, and the state and version of the installed copy. The install script reads it. |
| `Earshot.exe --exit` | Asks the running tray to exit through its menu's own Exit, which hands AirPods in use back first when Hand back is on, and ends. Exit code 0 when the tray was asked, 75 when none is running. |
| `Earshot.exe update <zip> <sha256> <pid> <userSid> <address> <containerGuid>` | The elevated half of Update. Started by the installed Earshot after the one administrator prompt, not by hand. See [Updates](#updates). |
| `Earshot.exe repair <userSid> <address> <containerGuid>` | The elevated half of Repair. Started by the tray after the one administrator prompt, from the installed copy only, not by hand. See [Repair](#repair). |
| `Earshot.exe service` | The hand-back service's run mode. Started by Windows from the service's registration, not by hand. |
| `Earshot.exe gate <verb> <nonce> [address]` / `Earshot.exe gate-protect <verb> <nonce>` | The elevated workers: one for the device nodes, one for the Bluetooth services. Started by Earshot's own scheduled tasks, not by hand. |
| `Earshot.exe diag <target>` | Single live actions for testing on real hardware: connect, disconnect, a raw driver request, a gate run, the unelevated service call, and the battery sweep. **All but the battery sweep and `gate status` change the state of the device**; those two only read. They exist for the live tests in `tools\live-tests` and are not part of normal use. |
