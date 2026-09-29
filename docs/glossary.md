# Glossary

Plain-English definitions of the terms used across these documents.

**A2DP.** Advanced Audio Distribution Profile: the ordinary stereo sound
profile a Bluetooth headset uses. This is the profile Earshot tries to keep
the AirPods on.

**At-rest invariant.** The rule Earshot holds to when nobody is using the
AirPods on this PC: the device nodes are disabled, so Windows has nothing to
page at boot. See [architecture.md](architecture.md).

**Battery set-up.** The flow behind **Set up battery** on the card and in the
menu: open the AirPods case by the PC (Earshot listens for 20 seconds), then
say what the iPhone shows with three pickers. The picker values are evidence
only, never shown as a reading. See
[overview.md](overview.md#set-up-battery).

**Block at boot.** The tray setting that keeps the AirPods' device nodes
disabled while they are not in use, so Windows cannot page them when the PC
starts.

**Claim.** The record a battery set-up makes of the owner's AirPods: model,
colour and the signal strength, with their last known battery. Everything a
later broadcast is checked against. See
[overview.md](overview.md#whose-airpods-it-shows).

**Container / container GUID.** Windows groups the several device nodes that
belong to one physical Bluetooth accessory (for instance, the AirPods'
headset node and its hands-free node) under one container identifier.
Earshot matches nodes by container and Bluetooth address, not by name alone.

**Device node.** The entry Windows creates for one function of a paired
Bluetooth device. Disabling a device node is what stops Windows talking to
that function at all, including at boot.

**Elevated worker.** The part of Earshot that runs with administrator rights,
started by a scheduled task rather than by the tray directly, to make the
changes an ordinary user account cannot.

**Fast Startup.** A Windows setting that skips a full shutdown by hibernating
the kernel session instead of ending it. Whether a persistent device-node
disable behaves the same way through a Fast Startup cycle as through an
ordinary one is unverified; see [requirements.md](requirements.md).

**Gate, Protect, BootBlock.** The three scheduled tasks Earshot registers at
setup, all running as SYSTEM: Gate blocks and allows the device nodes on
demand, Protect changes the Bluetooth services on demand, and BootBlock runs
once at startup as a safety net. See [architecture.md](architecture.md).

**Gauge.** The small readout the AirPods widget places on the taskbar: the
earbud mark with a ring in the Windows accent colour and the lower proved bud's
number. When there is no free space for it, the ordinary tray icon takes over. See [overview.md](overview.md#the-airpods-widget).

**Hands-Free profile.** The narrow, mono, phone-call-quality Bluetooth audio
profile Windows switches a headset to whenever a program opens a microphone.
Earshot can turn this profile off for the AirPods so there is nothing to
switch to.

**Live test.** A test run by hand against the real AirPods and, for some
tests, a real phone and a real restart or shutdown, as opposed to the
automated unit tests that run on every build. See
[verification.md](verification.md).

**Ownership rule.** The check the AirPods widget runs on every Bluetooth
broadcast it hears, so that only a device matching the owner's claim, at a
strong enough signal and with a consistent battery reading, is ever shown as
his. See [architecture.md](architecture.md#the-airpods-widget).

**Persistent disable / persistent flag.** Disabling a device node in a way
that survives a restart. Without this flag, Windows would re-enable the node
at the next boot on its own.

**Proved (a field).** A part of the AirPods' broadcast, such as bud order or
the case battery, that two of the owner's own set-ups have shown agrees with
the iPhone. Only proved fields are ever shown or acted on, and a set-up that
disagrees withdraws one. In-ear and lid cannot be proved by set-up. See
[architecture.md](architecture.md#the-airpods-widget).

**Proximity message.** The short, unencrypted part of the Bluetooth
broadcast AirPods send while nearby, carrying their model and colour, and,
once proved, their battery. What the AirPods widget reads
instead of pairing or connecting.

**Safe mode.** A mode Earshot can be started in, set by the
`EARSHOT_SAFE_MODE` environment variable, that turns off every action that
would change a real device, task or Bluetooth service, leaving only reads.
Used for testing away from real hardware.

**SYSTEM task.** A Windows Task Scheduler task that runs as the SYSTEM
account rather than as the signed-in user, which is what lets Earshot make
changes an ordinary account cannot, without asking the user to elevate every
time.
