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
or off, and leaves a record of the state it finished in. Add `-OfferUninstall` to
be offered a full uninstall at the end, which also restores everything.

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

The tests are numbered riskiest first. Test 08 is the acceptance test the whole
application exists for: after a full power cycle, the AirPods are still connected
to the phone. Everything else is secondary to it.

Use a normal window, not an administrator one. Tests 07 and 15 raise an
administrator prompt of their own, which only you can approve, and test 07 is
specifically about what a normal window can do without one.

`EARSHOT_SAFE_MODE` and `EARSHOT_DATA_ROOT` must not be set. Earshot refuses its
live modes while either is, and the scripts stop with that message rather than
report a device failure that never happened.

## Which Earshot.exe

Pass the installed copy at `%ProgramFiles%\Earshot\Earshot.exe`, or the
`Earshot.exe` in an unzipped release folder. Install and uninstall refuse anything
else: they copy only the files the publish manifest lists and check each copy
against its recorded hash, and a build output folder has no manifest.

## Tests that need a restart

Four tests are in two halves, because a script cannot survive a restart. The first
half stops, tells you how to restart, and prints the exact command to run after you
log back in. That command is also saved as `resume.txt` in the evidence folder, so
it is never lost.

| Test | Restart |
|---|---|
| 04 | a normal restart |
| 05 | optional, to show the enable persists too |
| 08 | a full power down, not a restart |
| 09 | shut down while the AirPods are connected to this PC |
| 10 | one restart per variant, four variants |
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

## A note on numbers

Never write a figure into the README or anywhere else that a test did not measure.
If a test says `inconclusive`, that is what to record. The battery element is
absent from v1 on purpose, and a placeholder would be worse than nothing.
