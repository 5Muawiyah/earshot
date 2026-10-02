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
| 2 | Connect and disconnect the AirPods: left-click the tray icon, then Connect or Disconnect on the card | Connection-state unit tests confirm the state machine each click drives | Test 01 passed on the AirPods on 19 September 2026 with Protect audio quality on. It sent the driver requests directly; it did not click the tray icon. The click itself is scored by Test 08's `left-click-connects`, pending. Test 02, disconnect in detail, is pending |
| 3 | Keep the AirPods on A2DP, so a browser tab or a game cannot drop them to call quality | The read-only walk from the audio endpoints to both the A2DP and Hands-Free filters shows both filters answer, so the connect path is reachable | Test 06, pending |
| 4 | Show a battery figure only from the documented broadcast of the AirPods linked by a case open, or from Windows' own Hands-Free figure read from the paired device's device nodes. A live figure is a reading heard within 30 seconds; an older one is kept and drawn greyed with its age, and an estimate is marked `≈` with the age of the reading it grew from. An estimate never falls, a newer reading replaces it, and no figure is invented | `BatteryFreshnessTests.cs`, `ShownValueTests.cs`, `ProximityDecoderTests.cs`, `HandsFreeBatteryTests.cs` and `HandsFreeBatteryRealQueryTests.cs` in `tests/Earshot.Tests/Widget` | Test 19, pending. Windows' figure was seen empty on this PC with Hands-Free off; see [What it does not do](#what-it-does-not-do) |
| 5 | Ask for one administrator prompt at setup and one per update, and none in everyday use | The elevated worker's argument-validation unit tests confirm it refuses anything it does not expect | Test 15, Uninstall reversal, which exercises the live setup and its reversal, pending |
| 6 | Hand the AirPods back at shut down, sleep and Exit, once Hand back is ticked (it is on for a new install and never changed for an existing one; a settings file with no member for it reads as off): release them, then block their device nodes again, before this computer can grab them back | `tests/Earshot.Tests/Integration/Coordinator/HandBackTests.cs` proves the disconnect-then-block order, the two caps, and each reason a block is withheld, against fakes and a moved clock; `tests/Earshot.Tests/Phase1/TrayHandBackTests.cs` proves the reply is actually held open on the real window procedure for `WM_ENDSESSION` and `WM_POWERBROADCAST`, that a connect click is still refused once the hold returns because the session is still ending, and that the menu item toggles the setting; `tests/Earshot.Tests/Integration/Coordinator/ExitHandBackTests.cs` proves the same order for Exit | No live run yet. Tests 17, 18 and 20, pending; see [verification.md](verification.md) |

## Widget and later requirements

Every test below runs against a stand-in. None of these has had a live run.

| # | Requirement | Covered by | Live proof |
|---|---|---|---|
| 7 | Keyboard shortcuts: Ctrl+Alt+Shift+A switches to this PC and Ctrl+Alt+Shift+D to the phone, on by default, editable and clearable; a chord another app holds is reported; the last press wins; older settings files are migrated and a typed chord is kept | `tests/Earshot.Tests/Hotkeys` (including `HotkeyDefaultsMigrationTests.cs`) and the tray wiring in `tests/Earshot.Tests/Phase1` | Test 16, pending |
| 8 | Spoken status, off until the owner turns it on from the menu | `tests/Earshot.Tests/Voice` and the tray wiring in `tests/Earshot.Tests/Phase1` | None yet |
| 9 | Play from a phone, off until the owner turns it on in the settings file | `tests/Earshot.Tests/Streaming` and the tray wiring in `tests/Earshot.Tests/Phase1` | None yet |
| 10 | The taskbar gauge: the earbud mark with a ring and the lower bud's number (live, or its last or estimated value in grey; with the AirPods on this PC), or, with them not on this PC, a case mark with the ring and the case's last or estimated value, grey, with "Open the case to show battery" as its tooltip while the AirPods are connected and nothing is linked; in one of six orders, placed at the right end or next to the apps, raised again when covered, every hide, show, cover and raise logged with a reason and window class only, one failed read keeping it in place, the tray icon as the fallback | `GaugeContentTests.cs`, `AwayGaugeTests.cs`, `GaugeOrderLayoutTests.cs`, `GaugeRendererTests.cs`, `GaugePlacementTests.cs`, `GaugeControllerTests.cs`, `GaugeEventLogTests.cs`, `ForegroundChangeHookTests.cs` and `WindowCoverProbeTests.cs` in `tests/Earshot.Tests/Widget` | Test 19, pending |
| 11 | Link the owner's AirPods when the owner opens the case next to the PC: the paired model, a known case level, at least -70 dBm, five messages in five seconds over two seconds, the first such set (the strongest if several qualify at once, and a set 8 dB stronger that opens its case later takes the link), and never a worn pair; follow the linked set across address changes only to a set that continues its last levels within 30 seconds; drop the link after two minutes lost and show only the saved last readings until the next case open; the link is in memory only; with the AirPods connected and nothing linked the card says "Open the case to show battery"; grey a value after 30 seconds with its read time; Refresh (card and menu) waits up to 12 seconds and ends on values, Windows' figure or "Open the case". In-ear and a lid open or shut bit are not decoded | `BroadcastSelectorTests.cs`, `LinkOnCaseOpenSceneTests.cs`, `BroadcastSenderSetsTests.cs`, `BatteryRefreshTests.cs`, `WidgetRefreshWiringTests.cs`, `WidgetCardRefreshTests.cs` and `BatteryFreshnessTests.cs` in `tests/Earshot.Tests/Widget` | Test 19, pending |
| 12 | The card's settings page in the Windows 11 grouping: Taskbar (Display, Order), Behaviour (Case-open card, Hand back), Audio (Microphone off), Shortcuts (Connect, Disconnect, Card), About (the Updates page, with Check, Repair and Check automatically, which is off for a new install) and More (Position, Other device, Pause on removal, Pause on leave, Low battery, Fully charged notice, Left click connects, Battery history, Copy diagnostics), with the rows in order and their defaults | `WidgetCardSettingsTests.cs`, `SettingsPageDesignLayoutTests.cs`, `UpdatesPageDesignLayoutTests.cs`, `DesignSettingsDefaultsTests.cs`, `SettingsMigrationTests.cs`, `WidgetCardHostTests.cs` and `WidgetCardLayoutTests.cs` | None yet |
| 13 | Pause when the AirPods leave this PC: only when this PC was playing to them, before Earshot's own disconnects, never resumes, none when two sessions play | `PauseOnLeaveTests.cs` and `WindowsMediaSessionsTests.cs` | Test 21, pending |
| 14 | Updates: nothing downloads before Update, the zip is checked against the published `.sha256`, the installed copy checks it again and installs after one administrator prompt, Update is offered only by an installed copy | `tests/Earshot.Tests/Update` | None yet. Nothing has updated a real install |
| 15 | Switch timing: one `Switch to-pc:` or `Switch to-phone:` log line per switch | `tests/Earshot.Tests/App/SwitchTimelineTextTests.cs` | Test 16, pending |
| 16 | The install script: one line installs, updates, repairs or uninstalls; the zip's SHA-256 is checked before anything is unzipped and any mismatch ends the run with one plain line; one administrator prompt, handed to Earshot's own verbs, which check the zip again in an administrators-only folder; no menu when no one can answer; Uninstall keeps the settings unless asked. The checksum catches a damaged download, not a compromised release, because the script and the zip come from the same release | `tests/Earshot.Tests/Installer` runs `installer\earshot.ps1` in Windows PowerShell 5.1 and PowerShell 7 against a local release feed and a stub install root; `tests/Earshot.Tests/Update/InstallZipTests.cs`, `tests/Earshot.Tests/App/ProbeSetupValuesTests.cs` and `tests/Earshot.Tests/App/TrayExitCommandTests.cs` cover the verbs it uses. The PowerShell 7 rows run on the hosted build | None yet. Nothing has installed, updated or repaired a real PC with it, and the real administrator prompt has not been shown by any test |
| 17 | New installs: Hand back on shut down, sleep and Exit on, and Open on startup on; an existing settings file keeps its saved values | `tests/Earshot.Tests/Infra/JsonSettingsStoreTests.cs` (no file and no backup gives both on; a file, a backup, a file without the member and a reset file do not) | None yet |
| 18 | Hands-Free microphone off mode: off by default; turning it on only turns Protect audio quality off, and its Open button opens Windows' sound settings at the AirPods' microphone (the list of sound devices when the endpoint id is not well formed) with one line of guidance; it uses no undocumented interface; Protect audio quality on its own turns it off | `HandsFreeMicrophoneModeTests.cs` in `tests/Earshot.Tests/Widget` | None yet. Call quality with the mode on is unproved until the owner has tried it |
| 19 | Ear detection resume: resume only the session Earshot paused, within 60 seconds, on the same output, with nothing touched by hand as far as Windows' session change events and a read just before resuming show (nothing is resumed when those events are not being listened to), and only when every bud that was in is back in on fresh values of the same linked set | `AutoResumeTests.cs` and `EarSequenceTests.cs` in `tests/Earshot.Tests/Widget` | None. It is built and inactive, because no documented in-ear value exists to read |
| 20 | The case-open card: on by default for a new and an existing install; shown when the linked pair's case opens (its case-known messages starting after silence, the open that links it, or its lid open counter changing while it stays open), never from a closed case or worn buds; closes when the case-known messages stop for 8 seconds, after its close time (Until the case closes by default, or 5, 10, 30 or 60 seconds) and on its close button; shown where the gauge is by default, or on all displays, or on chosen displays, never on a display a full-screen application is on while still on the others; never takes the focus; a screen reader is told once per open; its switch and choices are in the Case-open card row's expander | `WidgetStatusServiceTests.cs` (the opens and the close through the real selection and link code), `CaseOpenCardChoiceTests.cs`, `CaseOpenCardTests.cs`, `CaseOpenCardRulesTests.cs`, `CaseOpenTrackerTests.cs`, `LinkOnCaseOpenSceneTests.cs`, `WidgetSettingsTests.cs`, `SettingsMigrationTests.cs`, `WidgetCardSettingsTests.cs` and `ProximityDecoderTests.cs` in `tests/Earshot.Tests/Widget` | Test 19, pending live test. The lid open counter and the close cadence have not been seen on a live case open |
| 21 | Away readings and estimates: the last reading of each bud and the case of the linked pair (level, charging flag, read time, model; no address, no name) is kept in `last-reading.json` and shown, greyed with its age, wherever the AirPods are; with them not on this PC the gauge shows the case mark; a part that was charging is shown rising as an estimate marked `≈` with the age, from a rate learned from the owner's own readings (none until one is learned; the Apple figure is switched off until its wording is confirmed), stops at 100, never falls (a high-water mark survives a clock put back and a restart) and is replaced by a newer live reading; a file that cannot be read is not written over | `LastReadingStoreTests.cs`, `AwayEstimateTests.cs`, `AwayRateLearnerTests.cs`, `AwayGaugeTests.cs`, `AwaySceneTests.cs`, `ShownValueTests.cs` and `WidgetStatusServiceTests.cs` in `tests/Earshot.Tests/Widget` | Test 19, pending live test. No estimate has been seen against a real charge |
| 22 | The card shortcut: Ctrl+Alt+Shift+E opens the card, or closes it when it is open, on by default, editable and clearable, with a chord another app holds reported; a file written before it takes the default and a cleared one stays cleared | `CardShortcutTests.cs` in `tests/Earshot.Tests/Hotkeys` and `TrayHotkeyTests.cs` in `tests/Earshot.Tests/Phase1` | Test 16, pending live test |
| 23 | The fully charged notice, on by default: one notice per charge per part, live or estimated (an estimate marked `≈` with its age), kept across a restart, re-armed by a live reading below 100 | `FullyChargedNoticeTests.cs` in `tests/Earshot.Tests/Widget` | Test 19, pending live test |
| 24 | Low battery alerts cover the case as well as the buds, on live readings only, and only while the AirPods are on this PC | `LowBatteryAlertServiceTests.cs` and `LowBatteryLatchTests.cs` in `tests/Earshot.Tests/Widget` | Test 19, pending live test |
| 25 | Battery history: the live readings of the linked pair, one per part per minute, kept 7 days in `battery-history.json` (the part, the level, the charging flag and the time, no address and no name), drawn as a 24 hour chart of left, right and case that steps back by day; never a saved reading, an estimate or Windows' figure | `HistoryStoreTests.cs` and `HistoryChartLayoutTests.cs` in `tests/Earshot.Tests/Widget` | None yet, pending live test |
| 26 | Screen readers: the gauge, the battery columns, the buttons, the settings rows, the case-open card (read out once per open) and the history chart have names and values, a part that is not live saying what kind of value it is and how old | `WidgetCardAccessibilityTests.cs`, `SpokenTextTests.cs` and `CaseOpenCardTests.cs` in `tests/Earshot.Tests/Widget` | None yet, pending live test with a screen reader |
| 27 | Copy diagnostics (tray menu and More): copies the app and Windows versions, the recent log and the UI stall log with Bluetooth addresses, container and other ids, SIDs, device paths, user paths and names removed, and removing them twice changes nothing more | `DiagnosticsRedactorTests.cs` and `UiStallMonitorTests.cs` in `tests/Earshot.Tests/Diagnostics`, and `MenuModelTests.cs` in `tests/Earshot.Tests/Phase1` | None yet, pending live test |
| 28 | Motion paced to the display: every animation (the card's enter and exit, pages, heights, expanders, toggles, hover fills, the gauge ring) is drawn once per vertical blank of the display it is on, on Windows' durations and curves, with nothing moving when Animation effects are off and every motion ending in one frame when no display can be waited on | `VBlankFrameClockTests.cs`, `FrameMotionTests.cs`, `CardAnimatorTests.cs`, `CardMotionTests.cs` and `WidgetCardMotionTests.cs` in `tests/Earshot.Tests/Widget` | None yet, pending live test. Smoothness on a real display has not been seen |
| 29 | Stillness: an open card does not redraw on a timer; the read time does not tick while a reading is fresh; the accent colour event and a changing display scale do not repaint or resize it; an unchanged gauge is not pushed again | `ReadLineStillnessTests.cs`, `CardStillnessTests.cs`, `CardDpiLatchTests.cs` and `GaugePushKeyTests.cs` in `tests/Earshot.Tests/Widget` | None yet, pending live test |
| 30 | Speed: the card is built before the first click, the taskbar is read on its own location events with a 10 second safety poll, the link check sleeps while nothing is linked, and the release build is precompiled (ReadyToRun) | `CardPrewarmTests.cs`, `TaskbarWatcherTests.cs`, `ShellWindowChangeHookTests.cs` and `WidgetStatusServiceTests.cs` in `tests/Earshot.Tests/Widget`, and `StartupIdleAndCardMeasureTests.cs` in `tests/Earshot.Tests/Measure`; `tools\build-release.ps1` publishes with ReadyToRun and checks the result | None yet, pending live test. The figures are from a local build, not a measured release |

## What it does not do

- **Count on Windows for a battery figure.** Three read-only checks on 15
  September 2026, with the AirPods connected to this PC, found no battery value
  Windows exposes for them: the PnP battery query returned nothing, a dump of
  every property on the AirPods' device nodes held no battery key, and WinRT
  returned the standard battery key empty for the AirPods' audio endpoint.
  Each check carried a positive control in the same read, so a broken query
  could not be mistaken for a missing value. The same check with the AirPods
  disconnected has not been run yet; it is Test 11 in the live tests. Earshot
  still reads Windows' Hands-Free battery property, from the device nodes only,
  and uses it only when no bud has a current broadcast value. It was seen empty
  on this PC with Hands-Free off, which is the default. The battery shown comes
  from the AirPods' own Bluetooth broadcast instead, for the pair you link by opening
  the case, shown wherever the AirPods are, with its age; see
  [overview.md](overview.md#the-airpods-widget) for what it can show and its
  limits. Noise control and battery read to the nearest 1% ride on Apple's
  own protocol over a channel Windows does not open to ordinary programs, which
  needs a kernel driver and is out of scope here; see
  [architecture.md](architecture.md#what-still-needs-a-kernel-driver).
- **Disable a device node that is not present.** Connect the AirPods to this
  PC once from Windows Bluetooth settings before blocking; Earshot reports
  this rather than silently doing nothing.
- **Survive a driver re-enumeration cleanly.** A driver update can give the
  AirPods fresh device nodes, which would not carry the disable. Earshot
  blocks again the next time it sees them enabled and unused, but a boot in
  between can let Windows page them.
- **Guarantee the shutdown-while-connected case.** With Hand back on shut
  down, sleep and Exit on (it is on for a new install), Earshot releases the
  AirPods and blocks the nodes again inside its own cap of 4 seconds at a shut
  down, restart or sign-out, and 1.5 seconds at sleep. Exit uses the 4 second
  cap too. It cannot do any of that when Windows gives it no notice at all: a
  power cut, a held power button, a kernel stop error and the battery reaching
  a critical level all send nothing. Earshot not running
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
- **Tell whether a bud is in your ear or whether the case lid is open or
  shut.** No documented source gives either in the broadcast, so Earshot does
  not decode them. Ear detection and auto-pause on removal stay off. The
  case-open card is shown from the linked pair's case-known messages and the
  documented lid open counter, and closes when those messages stop for 8
  seconds, not from a lid bit.
- **Prove which bud is the left and which the right.** That rests partly on a
  published description of the broadcast and partly on one local capture. No
  permitted source documents the bit that swaps the two, so it is unproved.
- **Rule out a stranger's AirPods completely.** Earshot links the pair by model
  and by which opens its case nearest the PC. A pair of the same model that opens
  its case next to the PC more strongly than yours can be linked instead, and one
  whose levels equal your pair's last can be followed if heard within 30 seconds of
  yours going quiet. The saved readings and the case-open card follow that link, so
  a pair that takes it is saved, estimated from and shown like yours. The owner
  accepted that risk.
- **Measure what an estimate says.** An estimate is a rise at a rate learned from the
  owner's own readings, shown with `≈` and the age of the reading it grew from. It
  is not a reading, there is none until a rate has been learned, and the case shows
  only after the owner's case has been seen once.
- **Promise call quality in microphone off mode.** The mode turns audio
  protection off and leaves the AirPods' Hands-Free microphone for the person
  to switch off in Windows' sound settings. Earshot cannot switch it off, and
  call quality with the mode on is unproved until the owner has tried it.
- **Protect an update or an install against a compromised release or account.**
  The checksum comes from the same release as the zip, and, for the install
  script, as the script itself, so it catches a damaged download and not a
  compromised release. The app is not signed.
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
