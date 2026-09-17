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

## Running a test

```
powershell -NoProfile -ExecutionPolicy Bypass -File .\Run-LiveTests.ps1 -List
powershell -NoProfile -ExecutionPolicy Bypass -File .\Run-LiveTests.ps1 -Test 01 -ExePath "C:\Program Files\Earshot\Earshot.exe"
```

Four tests take an option of their own. The launcher passes each one on, and refuses
it for a test that does not take it rather than dropping it quietly:

| Option | Test | What it is for |
|---|---|---|
| `-Variant 1` to `-Variant 5` | 10 | Which kind of restart to raise. Run the test once per variant. |
| `-AllowPlanB` | 07 | Also try the `--principal user` fallback, which raises an administrator prompt. |
| `-Note "fast startup on"` | 04 | Free text kept with the run, to record the setting it was run under. |
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
`Earshot.exe` in an unzipped release folder. Install and uninstall refuse anything
else: they copy only the files the publish manifest lists and check each copy
against its recorded hash, and a build output folder has no manifest.

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
connect fallback is needed.

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
| 001, 003, 010 | 08 (`icon-dpi`, `icon-theme`). A contrast theme is not asked. |
| 004, 005, 006, 012, 016 | 08 (`click-once`, `menu`, `clean-exit`, `startup-value`, `startup-agrees`, `single-instance`) |
| 007, 013 | 10, all five variants (`query-arrived`, `end-arrived`) |
| 009, 015 | 14 (`picker-lists-devices`). The greyed entries, the rename and the re-pin are not asked. |
| 017, 064, 080 | 08, both halves of the click: `left-click-connects`, `click-agrees-with-endpoints`, `no-admin-prompt`, then `left-click-disconnects`, `disconnect-agrees-with-endpoints`, `no-admin-prompt-disconnect`, `blocked-again-after-click`, plus `card-no-focus`. 01 and 02 settle the driver requests underneath them. |
| 018, 021, 027, 060, 071, 079, 122 | 12 |
| 019 | 06, 12 (`protection-churn`) |
| 020, 025, 026, 037, 049, 078 | 04 |
| 023, 056, 057, 065, 072, 073, 076, 098, 112, 128 | 01 |
| 024 | 01, 02, 03 |
| 030, 031, 043, 045, 054, 099, 134 | 15 |
| 032, 048, 125 | 07 (`ace-present`), 15 (`install-again`) |
| 033, 034, 035, 047, 051, 110, 116, 124 | 07 |
| 036 | 04, 06, 07: any gate run that completes |
| 038, 111, 126, 127, 138 | 08 (`ACCEPTANCE`, `still-blocked`). Need 126 also asks for Fast Startup, and **that half is not asked**: see below. |
| 039, 114 | 05 |
| 040, 133 | 09 (`not-paged-at-boot`, `nodes-after-boot`) |
| 041 | 07 (`setboot-round-trip`), 14 (the set-device half) |
| 044, 055, 136 | 07 with `-AllowPlanB` (`planb`) |
| 050, 117 | 05 (`bit-cleared`), 04 (`persisted`), 15 (`nodes-restored`) |
| 052, 108 | 06 (`services-readable-while-blocked`), 08 (`no-fresh-handsfree-node`) |
| 053, 119 | 09. A battery-saver boot is not asked. |
| 058, 068, 077 | 02 |
| 061, 066, 074 | 01 (`K1-budget`), 02 (`disconnect-budget`) |
| 084, 086, 087 | 08 (`card-no-focus`) |
| 085 | 08 (`icon-theme`) for light and dark. High contrast is not asked. |
| 093 to 097, 100, 102 to 106, 115, 130 | 06 |
| 113, 129 | 03 (`no-auto-page`), 02 (`block-recorded`) |
| 118, 132 | 10 (`block-queued`), 09 (`end-session-logged`) |
| 120, 131 | 13 |
| 137 | 11 |
| 002, 011 | **Not asked.** An Explorer restart and a primary-display DPI change. |
| 008 | **Not asked.** No script watches a first sighting pin the container. |
| 014 | **Not asked.** Keyboard access to the icon (Win+B, Shift+F10). |
| 022, 028 | **Not asked.** They need a tray session of hours. |
| 029 | **Not asked.** A code question, not a device one. |
| 042 | **Not asked.** It needs the AirPods unpaired from this PC. |
| 046 | **Not asked.** It needs a second account and a squatted task folder. |
| 059, 067 | **Not asked.** Wrong-state and unsupported requests, and what they return. |
| 062, 063 | **Not asked.** The controller's own return while blocked, and whether `staleSnapshotsIgnored` is ever non-zero. |
| 069, 070, 075 | **Not asked.** Whether the render endpoint or its connector changes shape mid-connect. |
| 081, 082, 083, 088 to 092 | **Not asked.** A second monitor, an auto-hidden taskbar, a full-screen app, and the other card placement cases. |
| 101, 121 | **Not asked.** Two SYSTEM tasks started together, and the locks under the real task limits. |
| 107 | **Not asked.** A protection intent kept while blocked and applied after the next allow. |
| 109 | **Not asked.** The DACL on `device-change.lock`. |
| 123, 135 | **Not asked.** Removing a paired device while it is blocked. |
| 126, Fast Startup half | **Not asked.** No script turns Fast Startup on, reads it, or has a criterion for it. Test 08 powers down with whatever the machine is set to, and does not record which. To cover it by hand, turn Fast Startup on and run `04-BlockAndReboot.ps1 -Note "fast startup on"`, then read the note back beside `persisted`. Until then the answer is unknown, not settled. |

When a sitting ends, tick the needs the run actually answered against this table in
`PROMPTING_RESPONSES.md`, so an unasked question is never read as a settled one.

## A note on numbers

Never write a figure into the README or anywhere else that a test did not measure.
If a test says `inconclusive`, that is what to record. The battery element is
absent from v1 on purpose, and a placeholder would be worse than nothing.
