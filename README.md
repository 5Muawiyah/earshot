# Earshot

A Windows 11 tray utility for AirPods that stops the PC stealing them from
your phone at boot, connects and disconnects them with one click, and keeps
audio in stereo by turning off the Handsfree profile.

Earshot is being built. The shared foundation is in place (settings, logging,
Windows interop, the read-only probe and tests). The tray, connect and
disconnect, the boot block and audio protection are not in this build yet.
Until they are, the PowerShell prototype further down is the working tool.

## What it will do

- **Block at boot** (on by default). Windows has no per-device auto-connect
  setting, so Earshot disables the AirPods' Bluetooth device nodes while they
  are not in use on the PC. A disabled node stays disabled across a restart,
  so Windows never pages them. Nothing is unpaired.
- **Connect and disconnect.** Left click the tray icon. This uses the Core
  Audio API and needs no administrator rights.
- **Protect audio quality** (on by default). Windows drops from stereo A2DP to
  mono Handsfree whenever an app opens a microphone. Earshot turns off the
  Handsfree service for the AirPods. This also turns off the AirPods
  microphone.
- **Open on startup** (on by default), and a configurable device name match
  for renamed AirPods.

There is no equaliser, codec switch or quality booster. Windows picks the
codec itself and offers no way to override it.

## Run modes

One program, `Earshot.exe`, chosen by its first argument:

| Command | What it does |
|---|---|
| `Earshot.exe` | Tray app. |
| `Earshot.exe probe [audio\|topology\|nodes\|services\|task\|battery\|all] [--json] [--out <path>]` | Read-only diagnostics. Changes nothing. |
| `Earshot.exe install` / `uninstall` | One-time setup and removal, with one administrator prompt. |
| `Earshot.exe gate <verb> <nonce> [address]` | Run by Earshot's own scheduled task, not by hand. |
| `Earshot.exe diag <target>` | Single live actions for hardware testing. |

A mode that is not in the current build reports "Not available in this
build." and exits with code 69.

For testing, `EARSHOT_DATA_ROOT=<absolute folder>` moves every data folder
under that folder, and `EARSHOT_SAFE_MODE=1` turns off every device action.
Safe mode is on for any value of `EARSHOT_SAFE_MODE` except empty, `0` or
`false`. The data root counts as set when `EARSHOT_DATA_ROOT` is not blank.
`install`, `uninstall` and `gate` refuse to run in safe mode or while the
data root is set, and in safe mode every `diag` target is refused.

## Battery

**No battery level in v1.** No battery value could be found that Windows
exposes for these AirPods, so the tray shows no battery element rather than a
guess.

Evidence, gathered on 15 September 2026 with the AirPods connected to this PC:

- `Get-PnpDeviceProperty`, filtered to battery keys, returned nothing for the
  AirPods device nodes matched by name.
- A full property dump of the device nodes in the AirPods container had no
  battery key.
- WinRT returned the battery key `{104EA319-6EE2-4701-BD47-8DDBF425BBE5},2`
  empty for the AirPods endpoint and its device nodes.

The same check with the AirPods disconnected has not been run yet.

Real battery levels need Apple's own protocol over a Bluetooth channel that
Windows does not open to apps without a kernel driver. Earshot does not use a
driver.

## Caveats

- **Connect the AirPods to the PC once** before blocking. Device nodes that
  are not present cannot be disabled, and Earshot says so instead of doing
  nothing.
- Blocking needs a one-time setup with an administrator prompt. After that it
  runs through a scheduled task with no prompt.
- Protect audio quality turns off the AirPods microphone while it is on.
- More caveats, and the results of testing on real hardware, will be added
  here before release.

## Building

Needs the .NET 10 SDK on Windows (x64).

    dotnet build Earshot.slnx
    dotnet test Earshot.slnx

`tools\check.ps1` rebuilds from scratch, runs the tests and checks that no
compiler or code analysis warning is suppressed anywhere.

## The PowerShell prototype

`AirPodsGate.ps1`, `Install-AirPodsGate.ps1` and `Uninstall-AirPodsGate.ps1`
are the prototype of the boot block. Earshot replaces them.

Setup, once:

1. Connect the AirPods to the PC once, so Windows creates their device nodes.
2. Open PowerShell as administrator, in this folder.
3. Check what will be touched before changing anything:

       .\AirPodsGate.ps1 -Action Status

   If nothing matches, the script lists every Bluetooth device it can see.
   Re-run with the name you see, for example `-Name "Owner's AirPods"`.

4. Install the shortcut:

       .\Install-AirPodsGate.ps1

   Pass `-Name` here too if yours are renamed.

Daily use: double click **Toggle AirPods** on the desktop, or pin it to the
taskbar. It flips between blocked and allowed, elevated, with no prompt.

    .\AirPodsGate.ps1 -Action Status     # show state, change nothing
    .\AirPodsGate.ps1 -Action Block
    .\AirPodsGate.ps1 -Action Allow
    .\AirPodsGate.ps1                    # toggle
    .\AirPodsGate.ps1 -Action Block -WhatIf

Remove it with `.\Uninstall-AirPodsGate.ps1`, which re-enables the nodes,
drops the scheduled task and deletes the shortcut.

Prototype caveats:

- The no-prompt trick is a scheduled task registered with highest privileges,
  fired by `schtasks /run`. Registering it needs admin once.
- Only tested for correctness of logic, not on a live Windows machine. Run
  `-Action Status` first and confirm the matched list looks right before you
  disable anything.
- If Windows re-enumerates the AirPods as fresh nodes after a driver update,
  re-run Block.

## Licence

MIT. See `LICENSE`.
