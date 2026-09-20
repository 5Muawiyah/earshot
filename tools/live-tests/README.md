# Earshot live device tests

These scripts prove on the real hardware what the build could only test against
fakes. They are meant to be run by hand, with the AirPods and the phone to hand,
after Earshot is installed from a release build.

Nothing here runs during a build. Every step that changes a device, a scheduled
task or a folder is printed first, with what it will do, and only runs after you
type `y`.

## Read this first: how to put the machine back

If a test stops in the middle, the machine can be left with the AirPods Bluetooth
nodes disabled, or with the Handsfree service turned off. Neither is dangerous and
both are reversible.

```
powershell -NoProfile -ExecutionPolicy Bypass -File .\00-Restore.ps1 -ExePath "C:\Program Files\Earshot\Earshot.exe"
```

It reads the state, offers to enable the nodes, asks whether you want Handsfree on
or off, puts Block at boot back on if a test left it off, and leaves a record of the
state it finished in. Add `-OfferUninstall` to be offered a full uninstall at the
end, which also restores everything.

Two things it cannot put back, because both need either the tray or an administrator.
It reads each one and says so, rather than leaving a wrong one to be found by a block:

- **The pinned device** in `%ProgramData%\Earshot\device.json`. If it names the wrong
  device, move it with "Choose device..." in the tray menu, or uninstall and install
  again. Nothing in the restore script can move it.
- **The task principal**, if test 07 was run with `-AllowPlanB`. That registers a task
  as your user rather than as SYSTEM, which is not how Earshot ships. Only uninstalling
  and installing again without `--principal user` undoes it.

If the scheduled tasks are gone, the restore script cannot enable the nodes,
because enabling them needs administrator rights and Earshot only ever asks for
those through its own installed task. Do it by hand instead:

1. Open Device Manager.
2. View, then Show hidden devices.
3. Under Bluetooth, find the AirPods entries (they carry the device address).
4. Right-click each one and choose Enable device.

Windows keeps the pairing either way, so nothing needs pairing again.

## Every test checks the machine is at rest

Every script ends by reading whether the AirPods Bluetooth nodes are left Blocked, not only
whether its own criteria passed. "At rest" means the nodes read Blocked, so Windows has
nothing to page at the next boot. On 19 September a run left the nodes Allowed and the tray
closed, with nobody asked whether that was still all right; the next boot paged the AirPods,
the boot task blocked them about 14 seconds later, and they bounced between the phone and
the PC in between. This closing step is what now catches that.

- If Earshot is not set up, or Block at boot is off, being at rest does not apply, and the
  run says so.
- If the nodes already read Blocked, the run says the machine is at rest and stops there.
- If a half's whole point is leaving the nodes enabled, it says so instead of offering
  anything: test 09's first half and every variant of test 10's first half shut down or
  restart with the AirPods connected on purpose, and test 05's first half does too when you
  choose its optional restart. Blocking the nodes first would answer nothing about what
  those halves are testing.
- Anything else, it offers ONE live step, `diag gate block`, with the usual typed
  confirmation, described plainly: it blocks the nodes so this PC does not page the AirPods
  at the next boot, and if they are playing through this PC right now, that stops.
- If you decline, the step fails, or the nodes still do not read Blocked afterwards, the
  summary carries a warning block that is hard to miss, saying the machine is not at rest,
  what happens if it is shut down or restarted like that, and how to fix it: start the
  Earshot tray, whose own start-up check blocks the nodes when they are not in use, or run
  `diag gate block` yourself. (`00-Restore.ps1` does the opposite: it allows the nodes, so it
  is never the right remedy here.)

`result.json` carries this as a top-level `atRest` object, holding what was read before,
whether a block was offered and accepted, what it returned, and what was read after, plus a
finding named `leftAtRest`: `yes`, `no`, `no-on-purpose`, `not-applicable` or `unknown`. It
never changes a test's own criteria, its overall outcome, or its exit code.

## Running a test

```
powershell -NoProfile -ExecutionPolicy Bypass -File .\Run-LiveTests.ps1 -List
powershell -NoProfile -ExecutionPolicy Bypass -File .\Run-LiveTests.ps1 -Test 01 -ExePath "C:\Program Files\Earshot\Earshot.exe"
```

Seven tests take an option of their own. The launcher passes each one on, and refuses
it for a test that does not take it rather than dropping it quietly:

| Option | Test | What it is for |
|---|---|---|
| `-Variant 1` to `-Variant 5` | 10 | Which kind of restart to raise. Run the test once per variant. |
| `-AllowPlanB` | 07 | Also try the `--principal user` fallback, which raises an administrator prompt. |
| `-Note "..."` | 04 | Free text kept with the run, for whatever that run was meant to show. |
| `-WatchSeconds 120` | 03 | How long to watch for Windows paging the AirPods after an allow. |
| `-WatchMinutes 10` | 13 | How long to wait for the idle rule to block the nodes again. |
| `-PhoneAddress`, `-SpeakerAddress` | 14 | The addresses to try. Without them the test lists the ones it can see and asks which is which. |
| `-OfferUninstall` | 00 | Offer a full uninstall at the end of the restore. |

The tests are numbered riskiest first. Test 08 is the acceptance test the whole
application exists for: after a full power cycle, the AirPods are still connected
to the phone. Everything else is secondary to it.

Use a normal window, not an administrator one. Test 15 raises an administrator
prompt of its own, and so does `00-Restore.ps1 -OfferUninstall`. Test 07 raises
one only if you pass `-AllowPlanB`; its default path is specifically about what
a normal window can do without a prompt. Only you can approve any of them.

`EARSHOT_SAFE_MODE` and `EARSHOT_DATA_ROOT` must not be set. Earshot refuses
`gate`, `gate-protect`, `install` and `uninstall` while either is set, and
refuses every `diag` target in safe mode. `EARSHOT_DATA_ROOT` on its own does
not stop a `diag` target, so the scripts check both themselves and stop the run
rather than send a real device action with its evidence redirected.

## Which Earshot.exe

Pass the installed copy at `%ProgramFiles%\Earshot\Earshot.exe`, or the
`Earshot.exe` in an unzipped release folder. Install refuses anything else itself:
it copies only the files the publish manifest lists and checks each copy against its
recorded hash, and a build output folder has no manifest, so it stops with "Install
from a release build." Uninstall reads no manifest and has no such check, so the
scripts do it instead: every test that runs either verb (00 with `-OfferUninstall`,
07 with `-AllowPlanB`, and 15) refuses a build output folder before it removes
anything.

## Tests that need a restart

Six tests are in two halves, because a script cannot survive a restart. The first
half stops, tells you how to restart, and prints the exact command to run after you
log back in. That command is also saved as `resume.txt` in the evidence folder, so
it is never lost.

| Test | Restart |
|---|---|
| 04 | a normal restart |
| 05 | optional, to show the enable persists too |
| 08 | a full power down, not a restart |
| 09 | shut down while the AirPods are connected to this PC |
| 10 | one restart per variant, five variants: the fifth signs out and back in rather than restarting |
| 15 | a restart, to check the delayed file deletion |

## Where the evidence goes

```
%LOCALAPPDATA%\Earshot\livetest\<UTC timestamp>\<test>\
    summary.txt          everything that was printed, in order
    result.json          the criteria, their outcomes, the findings and every command
    NN-<label>.stdout.txt, NN-<label>.stderr.txt     what each command printed
    <label>.json         the read-only probe reports
    app-evidence\        the JSON Earshot itself wrote for each live step
    earshot-log\         a copy of Earshot's own log
    resume.txt           for a test in two halves
```

`result.json` is the one to read. Each criterion carries `pass`, `fail` or
`inconclusive` against a stated rule, and the `findings` list holds the measured
values and the decisions a test settles, such as whether the Handsfree assisted
connect fallback is needed. A finding whose value is null means the read behind it
did not answer, which is recorded rather than guessed at.

Each test also ends with an exit code, and the launcher passes it on: 0 for pass,
1 for fail, 2 for inconclusive. `result.json` is still where the detail is.

`inconclusive` is a real answer, not a soft failure. It means the test could not
reach the state it needed, usually because a precondition was not met, and it is
recorded that way rather than guessed at.

## What the tests settle

| Test | The decision it settles |
|---|---|
| 01 | Whether the A2DP filter accepts the one-shot reconnect with protection on, and so whether the Handsfree assisted connect fallback has to ship. |
| 02 | The disconnect budget, whether one filter drops the whole link, and what a block does to a link in use. |
| 03 | Whether enabling the nodes alone takes the AirPods off the phone. |
| 04 | Whether the persistent disable survives a restart. |
| 05 | Whether the enable clears the disabled bit, and how long the endpoints take to come back. |
| 06 | Whether an unelevated Handsfree change is possible in v1.1, and what Headset returns. |
| 07 | Whether the tray can start the SYSTEM tasks, and whether the `--principal user` fallback is needed. |
| 08 | The acceptance test, in the configuration Earshot ships in. |
| 09 | Whether the v1.1 pre-shutdown service is needed. |
| 10 | Which end-session messages arrive, per kind of restart. |
| 11 | The disconnected leg of the battery question. |
| 12 | The notification thread, apartment and event order. |
| 13 | The value of the idle grace window. |
| 14 | That a phone can never be pinned as the device Earshot disables. |
| 15 | Whether uninstall reverses everything and install passes its own checks. |

## What the backlog asks, and where it is answered

The build's backlog holds 138 `live_test_needs`. This table maps every one of them
onto the script that asks it, by its number in `live_test_needs`. It is deliberately
cautious: a need is named against a test only where that test has a criterion or a
recorded finding for it, so a need marked **not asked** may still be partly visible
in the evidence. Nothing here is settled until the test has actually been run.

**Not asked** is not the same as failed. It means no script puts the question, so
after a full sitting the answer is still unknown, and it should be recorded as
unknown rather than assumed.

| Backlog needs | Where they are answered |
|---|---|
| 001, 003, 010 | 08 (`icon-dpi`, `icon-theme`). The icon ink in a contrast theme is not asked; the card in one is, under 085. |
| 002, 011 | 08 (`icon-dpi`, `icon-theme`, `taskbarCreatedSeen`). No script restarts Explorer, so only the scaling change and the light and dark switch are actually raised; the finding counts the `TaskbarCreated` lines the log already holds. |
| 004, 005, 006, 012, 016 | 08 (`click-once`, `menu`, `clean-exit`, `startup-value`, `startup-agrees`, `single-instance`) |
| 007, 013 | 10, all five variants (`query-arrived`, `end-arrived`) |
| 009, 015 | 14 (`picker-lists-devices`, `pickerMarksAbsentDevices`). The rename and the re-pin through the picker are not asked. |
| 014 | 08 (`keyboardReachesTheIcon`) |
| 017, 064, 080 | 08, both halves of the click: `left-click-connects`, `click-agrees-with-endpoints`, `no-admin-prompt`, then `left-click-disconnects`, `disconnect-agrees-with-endpoints`, `no-admin-prompt-disconnect`, `blocked-again-after-click`, plus `card-no-focus`. 01 and 02 settle the driver requests underneath them. |
| 018, 021, 027, 060, 071, 079, 122 | 12 |
| 019 | 06, 12 (`protection-churn`) |
| 020, 025, 026, 037, 049, 078 | 04 |
| 023, 056, 057, 065, 072, 073, 076, 098, 112, 128 | 01 |
| 024 | 01, 02, 03 |
| 030, 031, 045, 054, 099, 134 | 15 |
| 032, 048, 125 | 07 (`ace-present`), 15 (`install-again`) |
| 033, 034, 035, 047, 051, 110, 116, 124 | 07 |
| 036 | 04, 06, 07: any gate run that completes |
| 038, 111, 126, 127, 138 | 08 (`ACCEPTANCE`, `still-blocked`). Need 126 also asks for Fast Startup, and **that half is not asked**: see below. |
| 039, 114 | 05 |
| 040, 133 | 09 (`not-paged-at-boot`, `nodes-after-boot`) |
| 041 | 07 (`setboot-round-trip`), 14 (the set-device half) |
| 044, 055, 136 | 07 with `-AllowPlanB` (`planb`) |
| 050, 117 | 05 (`bit-cleared`), 04 (`persisted`), 15 (`nodes-restored`). Need 117 also asks for the Fast Startup variant, and **that half is not asked**: see below. |
| 052, 108 | 06 (`services-readable-while-blocked`), 08 (`no-fresh-handsfree-node`) |
| 053, 119 | 09. A battery-saver boot is not asked. |
| 058, 068, 077 | 02 |
| 059 | 01 (`reconnectWhileActive`), 02 (`disconnectWhileUnplugged`) |
| 061, 066, 074 | 01 (`K1-budget`), 02 (`disconnect-budget`) |
| 081, 082, 083, 090, 092 | 08 (`cardFollowsTheCursorDisplay`, `cardClearsAnAutoHidingTaskbar`, `cardSuppressedInFullScreen`). The `ABM_GETTASKBARPOS` rectangle itself, the log line naming `QUNS_BUSY`, and a result card over an exclusive full-screen app are not read back. |
| 084, 086, 087 | 08 (`card-no-focus`) |
| 085 | 08 (`icon-theme`) for the light and dark half, and (`cardReadableInContrastTheme`) for the contrast theme half. |
| 093 to 097, 100, 102 to 106, 115, 130 | 06 |
| 113, 129 | 03 (`no-auto-page`), 02 (`block-recorded`) |
| 118, 132 | 10 (`block-queued`), 09 (`end-session-logged`) |
| 120, 131 | 13 |
| 137 | 11 |
| 008 | **Not asked.** No script watches a first sighting pin the container. |
| 022, 028 | **Not asked.** They need a tray session of hours. |
| 029 | **Not asked.** A code question, not a device one. |
| 042 | **Not asked.** It needs the AirPods unpaired from this PC. |
| 043 | **Not asked.** The tray's own `Set up Earshot...` prompt, and what a declined one gives. Test 15 elevates from PowerShell instead, so only an accepted prompt is ever seen. |
| 046 | **Not asked.** It needs a second account and a squatted task folder. |
| 062, 063 | **Not asked.** The controller's own return while blocked, and whether `staleSnapshotsIgnored` is ever non-zero. |
| 067 | **Not asked.** An unsupported request, and whether a null property buffer of length 0 is accepted. A redundant request is recorded under 059. |
| 069, 070, 075 | **Not asked.** Whether the render endpoint or its connector changes shape mid-connect. |
| 088, 089, 091 | **Not asked.** Moving a card between displays of different scaling (this machine has only 96 DPI displays), DWM rounding on other builds, and whether a secondary-display taskbar hosts the icon. |
| 101, 121 | **Not asked.** Two SYSTEM tasks started together, and the locks under the real task limits. |
| 107 | **Not asked.** A protection intent kept while blocked and applied after the next allow. |
| 109 | **Not asked.** The DACL on `device-change.lock`. |
| 123, 135 | **Not asked.** Removing a paired device while it is blocked. |
| 117 and 126, Fast Startup halves | **Not asked.** No script turns Fast Startup on, and none has a criterion for it. Tests 08 and 09 now read the setting and record it (`fastStartupAtPowerDown`, `fastStartupAtShutdown`), so the evidence says which kind of shutdown a run used, but they power down with whatever the machine is set to. To cover it by hand, turn Fast Startup on and run 08 (or 09), the only halves that shut the machine right down, then read the finding back beside `still-blocked`. Test 04 cannot cover it: a restart always performs a full shutdown and a cold boot, whatever Fast Startup is set to. Until a power cycle has been made with it on, the answer is unknown, not settled. |

When a sitting ends, tick the needs the run actually answered against this table in
`PROMPTING_RESPONSES.md`, so an unasked question is never read as a settled one.

## How these scripts are themselves tested

The scripts run under `Set-StrictMode -Version 2.0`, where a script that parses
cleanly can still throw on its first criterion. PowerShell unrolls whatever a
function writes to the pipeline, so a helper that ends `return $found` hands back
`$null` when nothing matched and a bare string when one thing did, and `.Count` on
either throws. The script's own `catch` turns that into `run: fail` with every
criterion after it lost, and it happens on the success path, because "no matching
line" is what a boot block that held produces.

`tools\live-tests\selftest` runs every script in this folder, and both halves of
every resumable one, against a fake machine: sixteen scripts, twenty-two halves,
three sets of fake inputs holding 0, 1 and 2 matching lines and list items, so
sixty-six runs. Only the device-touching and owner-prompting helpers are replaced;
`Get-EarshotLogLines`, `Get-DiagEvidence`, `Copy-AppEvidence`, `Read-KsEvidence`,
`Read-EarshotJsonFile`, `Add-Criterion` and `Complete-LiveTestRun` all run for
real. Nothing there touches a device, registers a task, elevates or starts
`Earshot.exe`.

`tools\check.ps1` runs it, so a change to a script that would lose a criterion on
the owner's machine fails the gate instead. `tools\live-tests\selftest\README.md`
explains it, and says what to add when a test is added.

## A note on numbers

Never write a figure into the README or anywhere else that a test did not measure.
If a test says `inconclusive`, that is what to record. The battery element is
absent from v1 on purpose, and a placeholder would be worse than nothing.
