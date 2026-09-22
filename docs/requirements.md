# Requirements

What Earshot has to do, and the test that proves each point. A row is proven
only once a test has actually run against the real hardware or the real
scripts; where that has not happened yet, the row says so rather than being
left out. See [verification.md](verification.md) for the full status table
and the evidence behind each run.

## Core requirements

| # | Requirement | Supporting work | Proof |
|---|---|---|---|
| 1 | Stop Windows paging the AirPods at boot | The persistent-disable unit tests confirm the disable and block matching logic | Test 04 (block survives a restart) and Test 08 (the full power cycle acceptance test), both pending a live run |
| 2 | Connect and disconnect the AirPods with one click | Connection-state unit tests confirm the state machine each click drives | Test 01 passed on the AirPods on 19 September 2026, in the shipping default with Protect audio quality on. Test 02, disconnect in detail, is pending |
| 3 | Keep the AirPods on A2DP, so a browser tab or a game cannot drop them to call quality | The read-only walk from the audio endpoints to both the A2DP and Hands-Free filters shows both filters answer, so the connect path is reachable | Test 06, pending |
| 4 | Show no battery figure that was not read off the device | Three independent read-only checks, each run against a positive control so a broken query could not be mistaken for a missing value | Done, see [What it does not do](#what-it-does-not-do). The same check with the AirPods disconnected is Test 11, pending |
| 5 | Ask for exactly one administrator prompt, at setup, and nothing after | The elevated worker's argument-validation unit tests confirm it refuses anything it does not expect | Test 15, Uninstall reversal, which exercises the live setup and its reversal, pending |
| 6 | Hand the AirPods back at shut down and sleep: release them, then block their device nodes again, before this computer can grab them back | `tests/Earshot.Tests/Integration/Coordinator/HandBackTests.cs` proves the disconnect-then-block order, the two caps, and each reason a block is withheld, against fakes and a moved clock; `tests/Earshot.Tests/Phase1/TrayHandBackTests.cs` proves the reply is actually held open on the real window procedure for `WM_ENDSESSION` and `WM_POWERBROADCAST`, that a connect click is still refused once the hold returns because the session is still ending, and that the menu item toggles the setting | No live run yet. Tests 17 and 18, pending; see [verification.md](verification.md) |

## v1.1 requirements

All three are built and reviewed, off by default, and none has had a live
run: no real keyboard shortcut has been pressed, nobody has heard Earshot
speak, and no real phone has played through it. Every test below runs
against a stand-in.

| # | Requirement | Covered by |
|---|---|---|
| 7 | Keyboard shortcuts for connect, audio protection, block at boot and speak status, off until the owner types one into the settings file | `tests/Earshot.Tests/Hotkeys` and the tray wiring in `tests/Earshot.Tests/Phase1` |
| 8 | Spoken status, off until the owner turns it on from the menu | `tests/Earshot.Tests/Voice` and the tray wiring in `tests/Earshot.Tests/Phase1` |
| 9 | Play from a phone, off until the owner turns it on in the settings file | `tests/Earshot.Tests/Streaming` and the tray wiring in `tests/Earshot.Tests/Phase1` |

## What it does not do

- **Show a battery level.** There is no battery element in the tray at all.
  Three read-only checks on 15 September 2026, with the AirPods connected to
  this PC, found no battery value Windows exposes for them: the PnP battery
  query returned nothing, a dump of every property on the AirPods' device
  nodes held no battery key, and WinRT returned the standard battery key
  empty for the AirPods' audio endpoint. Each check carried a positive
  control in the same read, so a broken query could not be mistaken for a
  missing value. The same check with the AirPods disconnected has not been
  run yet; it is Test 11 in the live tests, and it cannot change the
  outcome, because a value would only be expected while connected.
  Per-earbud battery and the noise control modes ride on Apple's own
  protocol over a channel Windows does not open to ordinary programs, which
  needs a kernel driver and is out of scope here.
- **Disable a device node that is not present.** Connect the AirPods to this
  PC once from Windows Bluetooth settings before blocking; Earshot reports
  this rather than silently doing nothing.
- **Survive a driver re-enumeration cleanly.** A driver update can give the
  AirPods fresh device nodes, which would not carry the disable. Earshot
  blocks again the next time it sees them enabled and unused, but a boot in
  between can let Windows page them.
- **Guarantee the shutdown-while-connected case.** With Hand back at shut
  down and sleep on, Earshot releases the AirPods and blocks the nodes again
  inside the time Windows gives it: up to 4 seconds at a shut down, restart
  or sign-out, up to 1.5 seconds at sleep. It cannot do any of that when
  Windows gives it no notice at all: a power cut, a held power button, a
  kernel stop error, a forced shutdown such as `shutdown /f`, and the
  battery reaching a critical level all send nothing. Earshot not running
  (closed, crashed, or not yet started) is the same: nothing can run when
  nobody is there to run it. In every one of those cases the nodes are left
  as they were, and the fallback is the boot-time block task, which can lose
  the race against Windows re-paging the AirPods first.
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
  Windows picks the codec itself, from what both ends support, and exposes
  no public way to override that choice.
- **Keep the block current without running.** The tray must be running for
  the block to go back on when you stop using the AirPods, which is why Open
  on startup is on by default.
- **Confirm a named voice was actually selected.** On the machine this was
  built and tested on, selecting any installed voice by name throws inside
  the speech engine, so Earshot falls back to the default voice and logs the
  fallback. `VoiceName` has no proven effect there, and choosing a named
  voice is unproven anywhere.
- **Guarantee a shutdown refusal clears itself.** If Windows abandons a
  shutdown after telling programs the session is ending, Earshot keeps
  refusing a connect, an allow and every setting change until it is
  restarted, because Windows sends nothing to say a shutdown was abandoned;
  the card says to restart it.
