# Verification

The read-only rows were done on this machine with the AirPods paired and
connected. The first live sitting on the AirPods was on 19 September 2026:
Tests 01 and 05, and ordinary use of the tray with its log read afterwards.
Every time below is a single run, measured once, not a specification. A row
stays pending until a run on the device has written the evidence for it.

## Verification status

| What | Status |
|---|---|
| Builds with no warnings, and no suppressed or downgraded analyser rule | Done, checked on every build |
| Unit tests: device-node matching, block and connection state, the elevated worker's argument validation, settings handling, icon bytes, card placement | Done |
| The live test scripts parse, and every Earshot command line they pass is accepted by the application's own argument parsers | Done. The unit tests run the self-test scripts (`LiveTestSelfTestTests`) |
| Read-only probe of the audio endpoints on this PC | Done. The AirPods container is found by name and grouped correctly |
| Read-only walk from the endpoints to the audio driver, including reading a pin property from both the A2DP and Hands-Free filters | Done. Both filters answer, so the connect path is reachable |
| Read-only reads of the device nodes, installed Bluetooth services and scheduled tasks | Done |
| Self-contained release runs from an unzipped folder | Done |
| Connect and disconnect on the AirPods | Connect: done. Test 01 passed all 11 criteria. With Protect audio quality on, the A2DP filter accepted the reconnect request and the AirPods were playing from this PC 2749 ms later, and 3413 ms on the second run, when the request was sent to all filters, inside the 15 s allowed. The Hands-Free assisted fallback is not needed (`hfpAssistedFallbackNeeded` = no). With protection off one run took 14371 ms, which is close to the limit. Disconnect: each of the three disconnects in Test 01 was confirmed in under 60 ms and the AirPods went back to the phone. Test 02, disconnect in detail, is pending |
| Connect while blocked: allow, then reconnect | Seen in the application's log during ordinary use: a click while blocked allowed the nodes, and Windows then connected the AirPods itself about a second later. Test 03, which scores it, is pending |
| Block and allow, and the disable surviving a restart | Block and allow: done. Test 05 showed all 8 device nodes disabled with the persistent flag before the allow, and none after it (`enableClearsConfigFlagsDisabled` = yes); the log shows a block disabling all 8. Test 05 is recorded as failed overall, because the test scripts could not read Earshot's exit code at the time; that fault is fixed and the run was not re-scored. Surviving a restart, Test 04: pending |
| The power cycle test: after a full power cycle the AirPods stay on the phone | Pending |
| Shutting down while connected | Seen once, unscripted, on 20 September 2026: the PC was restarted with the nodes left enabled after a test sitting and the tray closed. Windows connected the AirPods at boot, the BootBlock task then blocked the nodes, and the AirPods went back and forth between the phone and the PC. The task's start time was 14 seconds after boot; how long the AirPods were held was not measured. So the BootBlock task does block, but too late to prevent the bounce, as the design expected. Test 09, which scores it, is pending |
| Turning the Hands-Free profile off and on | Done through the SYSTEM task, in Test 01 and from the tray: off and on each completed in about 3 to 4 seconds, and the microphone endpoint went and came back with it. Test 06, what the same call returns without elevation, is pending |
| Starting the SYSTEM task from the tray without a prompt | Seen in the application's log: the tray, not elevated, started the Gate and Protect tasks and both reported success. Test 07, which checks the arguments arrive, is pending |
| The battery check with the AirPods disconnected | Pending |
| Fast Startup | Pending |
| Keyboard shortcuts: registering a real one and pressing it (`tools\live-tests\16-FastSwitch.ps1` records whether it was the first real press) | Pending: no live run yet |
| Switching between the phone and this PC, and the `Switch to-pc:` and `Switch to-phone:` log lines (`tools\live-tests\16-FastSwitch.ps1`) | Pending: no live run yet. No timing is recorded here until a run measures one |
| Spoken status heard on a real run | Pending |
| Play from a phone, with a real phone | Pending |
| Shutdown refusals on a real shutdown | Pending. No live test scores it |
| Hand back on shut down while connected (`tools\live-tests\17-HandBackOnShutdown.ps1`) | Pending: no live run yet |
| Hand back on sleep while connected (`tools\live-tests\18-HandBackOnSleep.ps1`) | Pending: no live run yet |
| Hand back on Exit while connected (`tools\live-tests\20-HandBackOnExit.ps1`) | Pending: no live run yet |
| Connect from every node state inside 20 seconds with one retry, a plain reason and the allow put back (`tools\live-tests\23-ConnectEveryState.ps1`, test 23) | Pending an owner run: the script is in place, with its self-test against fake inputs. Stand-in tests only |
| The Open on startup value read the per-user copy's path on 25 October 2026 just after the tray logged writing the installed copy's | Open. No second writer and no ordering in the code explains it. Each write and delete is now read back and logged, so the next sign-in shows what the value reads straight after the tray writes it |
| wpr and the radio's driver properties on the owner's PC, 25 October 2026 | Recorded only in the code's comments, written on the branch that day (`WprCommand.cs`, `TraceProfile.cs`, `CfgMgr32.DevNodeStatus.cs`); the output was not kept. `wpr -start` with the profile file and `BluetoothStack.Verbose` with `-filemode` started a recording, `-stop` and `-cancel` ran, and the id `BluetoothStack.Verbose.File` was refused with 0xc5600611. `Get-PnpDeviceProperty` gave the radio's driver version and date that the CfgMgr32 read gives. Test 24 and the release run on the PC run the trace again |
| New pairing on the owner's PC, 25 October 2026 | Done, by hand, with a Bluetooth HCI trace. With one version of the PC's Bluetooth driver, no device would pair: the PC found devices, but every connection the PC started (Create Connection and Remote Name Request) ended in HCI status 0x04, Page Timeout. Rolling the driver back to the version the PC maker supplied fixed it at once: the AirPods paired, played clean sound (the owner listened), and Earshot connected them 6 times and disconnected them 4 times, every one returning S_OK. The figures are from the owner's own record of that sitting; the trace and the log are not in the repository. One sitting, not a measurement of how often a driver does this |
| Pair AirPods from the tray with the case in pairing mode, the "Bluetooth is off." line after a failed Connect from a real radio turned off, Ok again once it is on, adoption, still set up with at most one prompt, one Connect, and a trace saved (`tools\live-tests\24-PairAirPods.ps1`, test 24) | Pending an owner run: the script is in place, with its self-test against fake inputs. The scan's minor class, the pairing kind Windows asks for and whether Windows shows a box of its own are asked of the owner, not read from the log. Test 24 still has to prove, on the owner's PC with the working driver: Earshot's own Pair AirPods end to end, the connect history entry with its codes and times, the radio line from a real radio, a recording that `-recordtempto` and `-instancename` really start and stop, and the file in `%LOCALAPPDATA%\Earshot\traces`. Test 24 does not ask whether the trace's note came before the prompt, how many prompts there were, or whether a run folder was left in `%ProgramData%\Earshot\trace`: the tests on fakes cover those, and the owner's release run checks them by eye. The Look for AirPods window and Fix's question to pair again, each with its two pictures, and the trace's Stop card staying up, have not been seen on a screen; the owner's release run looks at both, and at a Stop pressed while the administrator prompt is up, which should record nothing. That Earshot's `-cancel` and `-stop` leave another program's wpr session alone, and where a Fix run that Setup.exe starts as administrator writes its log (the machine folder's, not the person's), have not been run either. Test 24 starts set up and pairs the same AirPods again, so set-up right after a pairing (with its one prompt), the first run with nothing paired and nothing set up, and dropping a replaced pair's readings run only under fakes. The Switched off, Problem, No adapter, No Classic Bluetooth and Unreachable lines, the question before a different pair is used, the empty-name signal, a device that is not AirPods left paired and unused, and the recovery after a crash between pairing and adoption stay unproved |
| Fix connection, including removing the AirPods from Windows and pairing them again | Pending: unproved on real AirPods. Stand-in tests only |
| Setup.exe on a real PC (SmartScreen, the administrator prompt, install, repair, uninstall) | Pending: no live run yet |
| Settings > Apps > Installed apps: the entry, Uninstall and the Modify button | Pending: no live run yet |
| Send to from the card, the gauge menu and the shortcut | Pending: no live run yet |
| Pause when the AirPods leave this PC (`tools\live-tests\21-PauseOnLeave.ps1`) | Pending: no live run yet |
| Service hand-back with the tray not running (`tools\live-tests\22-ServiceHandBack.ps1`) | Pending: no live run yet |
| The service finds nothing to do after the tray's hand-back (`tools\live-tests\17-HandBackOnShutdown.ps1`) | Pending: no live run yet |
| The AirPods widget: the taskbar gauge, its card, the broadcast battery with greying, and Refresh (`tools\live-tests\19-Widget.ps1`) | Pending: no live run yet. Test 19 is worded for the one card, with no estimates |
| The card opening with the case: open the case near the PC and the main card appears on the gauge's display without taking focus; shut the case and it closes; its close button closes it; a case left shut shows nothing (`tools\live-tests\25-OneCard.ps1`, test 25) | Pending: no live run yet. See 1.7.0, below |
| The gauge's Connect story with the card closed (ring spins, then a tick or a cross), and the card's ring when it is open (`tools\live-tests\25-OneCard.ps1`) | Pending: no live run yet. The hold times and the sweep are design choices |
| Moved to: the small card with Take back when another device takes the AirPods while this PC plays to them, and nothing for Earshot's own Disconnect or a walk out of range (`tools\live-tests\25-OneCard.ps1`) | Pending: no live run yet. See 1.7.0, below |
| Last readings away from this PC: the case mark with its last reading and age, kept until a newer one is heard (`tools\live-tests\25-OneCard.ps1`) | Pending: no live run yet. There are no estimates to verify |
| The card shortcut, Ctrl+Alt+Shift+E, the fully charged notice, the low battery alert for the case, and the battery history page | Pending: no live run yet, and no live test script covers them |
| Copy diagnostics pasted somewhere public with no address, id, name or user path left in it | Pending: no live run yet |
| Motion on a real display, and a screen reader reading the gauge and the cards | Pending: no live run yet. The pacing is tested against a fake display clock only |
| Start, idle and first card open figures from the precompiled release | Pending: the figures printed so far are from a local build |
| Microphone off mode: call quality with it on | Pending: the owner's own test. Unproved until then |
| Updating a real install from a published release | Pending: no live run, and no live test script covers it |

## 1.6.0

Live test 24 (Pair AirPods) is still owed. Nothing in 1.6.0 beyond what the
rows above record has had a live run.

## 1.7.0: what was not taken, and what is unproved

The live records planned for 1.7.0 were not taken. The owner did not answer
inside the limit set for them, so the run went on without them. These were never recorded: the battery side by side with
the iPhone (buds worn, both in the open case, each bud out alone, the case on
charge, the buds charging); a shut case watched for ten minutes (plugged in,
unplugged, moved, with three lid opens as a baseline); the iPhone taking the
AirPods three times, one walk out of range and one Disconnect in Earshot, each
timed; and the owner's memory of when the old card showed with the case shut.
Nothing here stands in for them.

What the case-open rule rests on is seven recorded false shows in the saved
logs, and synthetic replays built from them. Each of seven links was
followed about a minute later by a second show, from one or two case-known
messages of the linked set. The logs prove a card was on screen each time. They
do not prove the lid was shut. The new rule has been run only against those
replays and synthetic messages.

Still unproved at release:

- Whether a step is rounded or floored against the iPhone's figure.
- Which bud is left and which is right.
- What the charging bits mean.
- Whether the lid was shut at each recorded false show.
- What a shut case sends when it is charged or moved.
- The signature of a real walk out of range.
- The Moved to bound (10 seconds), message count (3) and signal level (-80 dBm).
  All three are design choices, and no trial was run.
- Whether a move from a silent PC could be shown truly. Moved to shows only when
  this PC was playing at the leave, a choice made without a live trial. In the saved
  logs, six of eight leaves Earshot did not start were followed by minutes of
  messages and seven had nothing playing. The log records what each would have said.
- Whether Windows' figure ever exists, and in what steps. It is shown as Windows
  gives it, so it may not be in steps of 10%.
- Whose the 17-byte messages are. They are counted by shape and not used.

Left and right, rounds or floors, and the charging bits stay marked unproved
wherever they appear.

## Live tests

The pending rows above are settled by the scripts in `tools\live-tests`, with
one exception: no single script settles Fast Startup on its own. Tests 08 and
09 read the setting and record it, but they power the machine down with
whatever it happens to be, so a full sitting leaves that row where it is
unless it was on. To cover it by hand, turn Fast Startup on and run 08 or 09,
the two tests that shut the machine right down rather than restarting it, then
read the finding back beside the criteria: `fastStartupAtPowerDown` from test
08, `fastStartupAtShutdown` from test 09. A restart always performs a full
shutdown and a cold boot, so no restart test can cover it.

The scripts are run by hand, with the AirPods and the phone there, against an
installed copy or an unzipped release. Nothing runs them for you, and none of
them runs during a build.

    powershell -NoProfile -ExecutionPolicy Bypass -File .\Run-LiveTests.ps1 -List
    powershell -NoProfile -ExecutionPolicy Bypass -File .\Run-LiveTests.ps1 -Test 01 -ExePath "C:\Program Files\Earshot\Earshot.exe"

They are numbered riskiest first. Test 08 is the acceptance test the whole
application exists for: after a full power cycle, the AirPods are still
connected to the phone. Every step that changes a device, a scheduled task or
a folder is printed first, with what it will do, and waits for you to agree.
Some of the tests are in two halves, because a script cannot survive a
restart; the first half prints the command to run afterwards and saves it as
well.

Each run writes its evidence to `%LOCALAPPDATA%\Earshot\livetest`, one folder
per run, with a `result.json` that records every criterion as pass, fail or
inconclusive against a stated rule. Where a criterion cannot be settled it
goes down as inconclusive rather than being left out.

`00-Restore.ps1` puts the machine back if a test stops in the middle. Read
[`tools\live-tests\README.md`](../tools/live-tests/README.md) before the
first run, so you know how to run it before you need it.

The scripts themselves are tested by `tools\live-tests\selftest`, which runs
every one of them, and both halves of each that needs a restart, against a
fake machine with no device, no scheduled task and no `Earshot.exe` anywhere
in it. Reading the source cannot tell you whether a script gets to its end
and records everything it should; running it against a fake machine can. It
runs as part of `tools\check.ps1`.
