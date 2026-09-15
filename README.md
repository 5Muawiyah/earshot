# AirPods Gate

Stops your PC paging the AirPods at boot and stealing them off your phone.

## What it does

Windows has no per-device auto-connect setting, so this works at the device
level instead: it disables the AirPods' Bluetooth device nodes. A disabled
node persists across reboots, so the block survives a power cycle. Nothing is
unpaired, so letting them back in is instant.

## Setup, once

1. **Connect the AirPods to the PC once**, so Windows enumerates their device
   nodes. Nodes that are not present cannot be disabled, so this step matters.
2. Open PowerShell as administrator, in this folder.
3. Check what will be touched before changing anything:

       .\AirPodsGate.ps1 -Action Status

   If nothing matches, the script lists every Bluetooth device it can see.
   Re-run with the name you see, for example `-Name "Owner's AirPods"`.

4. Install the shortcut:

       .\Install-AirPodsGate.ps1

   Pass `-Name` here too if yours are renamed.

## Daily use

Double click **Toggle AirPods** on the desktop, or pin it to the taskbar.
It flips between blocked and allowed, elevated, with no UAC prompt. A balloon
tells you which state you landed in.

Blocked is the state you want most of the time. Flip to allowed when you
actually want them on the PC, and back afterwards.

## Command line

    .\AirPodsGate.ps1 -Action Status     # show state, change nothing
    .\AirPodsGate.ps1 -Action Block
    .\AirPodsGate.ps1 -Action Allow
    .\AirPodsGate.ps1                    # toggle
    .\AirPodsGate.ps1 -Action Block -WhatIf

## Removing it

    .\Uninstall-AirPodsGate.ps1

Re-enables the nodes, drops the scheduled task, deletes the shortcut.

## Caveats

- The no-prompt trick is a scheduled task registered with highest privileges,
  fired by `schtasks /run`. Registering it needs admin once.
- Only tested for correctness of logic, not on a live Windows machine. Run
  `-Action Status` first and confirm the matched list looks right before you
  disable anything.
- If Windows re-enumerates the AirPods as fresh nodes after a driver update,
  re-run Block.
