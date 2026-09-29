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
| 2 | Connect and disconnect the AirPods: left-click the tray icon, then Connect or Disconnect on the card | Connection-state unit tests confirm the state machine each click drives | Test 01 passed on the AirPods on 19 September 2026, in that day's shipping default (left click connected directly) with Protect audio quality on. Test 02, disconnect in detail, is pending |
| 3 | Keep the AirPods on A2DP, so a browser tab or a game cannot drop them to call quality | The read-only walk from the audio endpoints to both the A2DP and Hands-Free filters shows both filters answer, so the connect path is reachable | Test 06, pending |
| 4 | Show no battery figure that was not read off the device | Three independent read-only checks, each run against a positive control so a broken query could not be mistaken for a missing value | Done, see [What it does not do](#what-it-does-not-do). The same check with the AirPods disconnected is Test 11, pending |
| 5 | Ask for exactly one administrator prompt, at setup, and nothing after | The elevated worker's argument-validation unit tests confirm it refuses anything it does not expect | Test 15, Uninstall reversal, which exercises the live setup and its reversal, pending |
| 6 | Hand the AirPods back at shut down, sleep and Exit: release them, then block their device nodes again, before this computer can grab them back | `tests/Earshot.Tests/Integration/Coordinator/HandBackTests.cs` proves the disconnect-then-block order, the two caps, and each reason a block is withheld, against fakes and a moved clock; `tests/Earshot.Tests/Phase1/TrayHandBackTests.cs` proves the reply is actually held open on the real window procedure for `WM_ENDSESSION` and `WM_POWERBROADCAST`, that a connect click is still refused once the hold returns because the session is still ending, and that the menu item toggles the setting; `tests/Earshot.Tests/Integration/Coordinator/ExitHandBackTests.cs` proves the same order for Exit | No live run yet. Tests 17, 18 and 20, pending; see [verification.md](verification.md) |

## Widget and later requirements

Every test below runs against a stand-in. None of these has had a live run.

| # | Requirement | Covered by | Live proof |
|---|---|---|---|
| 7 | Keyboard shortcuts: Ctrl+Alt+Shift+A switches to this PC and Ctrl+Alt+Shift+D to the phone, on by default, editable and clearable; a chord another app holds is reported; the last press wins; older settings files are migrated and a typed chord is kept | `tests/Earshot.Tests/Hotkeys` (including `HotkeyDefaultsMigrationTests.cs`) and the tray wiring in `tests/Earshot.Tests/Phase1` | Test 16, pending |
| 8 | Spoken status, off until the owner turns it on from the menu | `tests/Earshot.Tests/Voice` and the tray wiring in `tests/Earshot.Tests/Phase1` | None yet |
| 9 | Play from a phone, off until the owner turns it on in the settings file | `tests/Earshot.Tests/Streaming` and the tray wiring in `tests/Earshot.Tests/Phase1` | None yet |
| 10 | The taskbar gauge: the earbud mark with a ring and the lower proved bud's number, placed at the right end or next to the apps, raised again when covered, every hide, show, cover and raise logged with a reason and window class only, one failed read keeping it in place, the tray icon as the fallback | `GaugeContentTests.cs`, `GaugeRendererTests.cs`, `GaugePlacementTests.cs`, `GaugeControllerTests.cs`, `GaugeEventLogTests.cs`, `ForegroundChangeHookTests.cs` and `WindowCoverProbeTests.cs` in `tests/Earshot.Tests/Widget` | Test 19, pending |
| 11 | Show a battery, charging, in-ear or lid field only once it is proved: bud order and the case each need two set-ups that agree with the iPhone, a disagreeing set-up withdraws the field, in-ear and lid cannot be proved by set-up | `DecodeProofTests.cs`, `DecodeProofStoreTests.cs`, `BatterySetupFlowTests.cs`, `ClaimStoreTests.cs` and `OwnershipRuleTests.cs` in `tests/Earshot.Tests/Widget` | Test 19, pending |
| 12 | The card's settings page, with the rows in order and their defaults | `WidgetCardSettingsTests.cs`, `WidgetCardHostTests.cs` and `WidgetCardLayoutTests.cs` | None yet |
| 13 | Pause when the AirPods leave this PC: only when this PC was playing to them, before Earshot's own disconnects, never resumes, none when two sessions play | `PauseOnLeaveTests.cs` and `WindowsMediaSessionsTests.cs` | Test 21, pending |
| 14 | Updates: nothing downloads before Update, the zip is checked against the published `.sha256`, the installed copy checks it again and installs after one administrator prompt, Update is offered only by an installed copy | `tests/Earshot.Tests/Update` | None yet. Nothing has updated a real install |
| 15 | Switch timing: one `Switch to-pc:` or `Switch to-phone:` log line per switch | `tests/Earshot.Tests/App/SwitchTimelineTextTests.cs` | Test 16, pending |

## What it does not do

- **Show a battery level Windows itself exposes for the device.** It does
  not expose one at all. Three read-only checks on 15 September 2026, with
  the AirPods connected to this PC, found no battery value Windows exposes
  for them: the PnP battery query returned nothing, a dump of every
  property on the AirPods' device nodes held no battery key, and WinRT
  returned the standard battery key empty for the AirPods' audio endpoint.
  Each check carried a positive control in the same read, so a broken query
  could not be mistaken for a missing value. The same check with the
  AirPods disconnected has not been run yet; it is Test 11 in the live
  tests, and it cannot change the outcome, because a value would only be
  expected while connected. The AirPods widget reads battery a different
  way, from the AirPods' own Bluetooth broadcast rather than from Windows;
  see [overview.md](overview.md#the-airpods-widget) for what it can show
  and its limits. Noise control and battery read to the nearest 1% ride on
  Apple's own protocol over a channel Windows does not open to ordinary
  programs, which needs a kernel driver and is out of scope here; see
  [architecture.md](architecture.md#what-still-needs-a-kernel-driver).
- **Disable a device node that is not present.** Connect the AirPods to this
  PC once from Windows Bluetooth settings before blocking; Earshot reports
  this rather than silently doing nothing.
- **Survive a driver re-enumeration cleanly.** A driver update can give the
  AirPods fresh device nodes, which would not carry the disable. Earshot
  blocks again the next time it sees them enabled and unused, but a boot in
  between can let Windows page them.
- **Guarantee the shutdown-while-connected case.** With Hand back on shut
  down, sleep and Exit on, Earshot releases the AirPods and blocks the nodes
  again inside the time Windows gives it: up to 4 seconds at a shut down,
  restart or sign-out, up to 1.5 seconds at sleep. Exit uses the 4 second cap
  too. It cannot do any of that when
  Windows gives it no notice at all: a power cut, a held power button, a
  kernel stop error, a forced shutdown such as `shutdown /f`, and the
  battery reaching a critical level all send nothing. Earshot not running
  (closed, crashed, or not yet started) is covered only in part: the
  hand-back service can block the nodes at a shut down, but it cannot
  disconnect the AirPods, and it has not had a live run. Otherwise nothing can run when
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
- **Tell whether a bud is in your ear or whether the case lid is open.**
  Battery set-up cannot prove either, so ear detection, auto-pause and the
  case-open card stay off.
- **Rule out a stranger's AirPods completely.** A same-model stranger with a
  lower battery reading than yours can pass the ownership rule. The owner
  accepted that risk.
- **Protect an update against a compromised release or account.** The
  checksum comes from the same release and the app is not signed.
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
