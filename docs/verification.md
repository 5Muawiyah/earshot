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
| The live test scripts parse, and every Earshot command line they pass is accepted by the application's own argument parsers | Done. Checked in the unit tests, which never run a script |
| Read-only probe of the audio endpoints on this PC | Done. The AirPods container is found by name and grouped correctly |
| Read-only walk from the endpoints to the audio driver, including reading a pin property from both the A2DP and Hands-Free filters | Done. Both filters answer, so the connect path is reachable |
| Read-only reads of the device nodes, installed Bluetooth services and scheduled tasks | Done |
| Self-contained release runs from an unzipped folder | Done |
| Connect and disconnect on the AirPods | Connect: done. Test 01 passed all 11 criteria. With Protect audio quality on, the A2DP filter accepted the reconnect request and the AirPods were playing from this PC 2749 ms later, and 3413 ms on the second run, inside the 15 s allowed. The Hands-Free assisted fallback is not needed (`hfpAssistedFallbackNeeded` = no). With protection off one run took 14371 ms, which is close to the limit. Disconnect: each of the three disconnects in Test 01 was confirmed in under 60 ms and the AirPods went back to the phone. Test 02, disconnect in detail, is pending |
| Connect while blocked: allow, then reconnect | Seen in the application's log during ordinary use: a click while blocked allowed the nodes, and Windows then connected the AirPods itself about a second later. Test 03, which scores it, is pending |
| Block and allow, and the disable surviving a restart | Block and allow: done. Test 05 showed all 8 device nodes disabled with the persistent flag before the allow, and none after it (`enableClearsConfigFlagsDisabled` = yes); the log shows a block disabling all 8. Test 05 is recorded as failed overall, because the test scripts could not read Earshot's exit code at the time; that fault is fixed and the run was not re-scored. Surviving a restart, Test 04: pending |
| The power cycle test: after a full power cycle the AirPods stay on the phone | Pending |
| Shutting down while connected | Seen once, unscripted, on 20 September 2026: the PC was restarted with the nodes left enabled after a test sitting and the tray closed. Windows connected the AirPods at boot, the BootBlock task then blocked the nodes, and the AirPods went back and forth between the phone and the PC. The task's start time was 14 seconds after boot; how long the AirPods were held was not measured. So the BootBlock task does block, but too late to prevent the bounce, as the design expected. Test 09, which scores it, is pending |
| Turning the Hands-Free profile off and on | Done through the SYSTEM task, in Test 01 and from the tray: off and on each completed in about 3 to 4 seconds, and the microphone endpoint went and came back with it. Test 06, what the same call returns without elevation, is pending |
| Starting the SYSTEM task from the tray without a prompt | Seen in the application's log: the tray, not elevated, started the Gate and Protect tasks and both reported success. Test 07, which checks the arguments arrive, is pending |
| The battery check with the AirPods disconnected | Pending |
| Fast Startup | Pending |
| Keyboard shortcuts: registering a real one and pressing it | Pending |
| Spoken status heard on a real run | Pending |
| Play from a phone, with a real phone | Pending |
| Shutdown refusals on a real shutdown | Pending. Covered by the same sitting as the power cycle test (08), since that is a real shutdown |

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
Six of the tests are in two halves, because a script cannot survive a
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
