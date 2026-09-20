# Architecture

The technical guide: how Earshot holds the AirPods off the PC at rest, how it
protects audio quality, the permission model behind both, and the command
line the tray and the live tests both drive. For a plain-English tour of the
tray instead, see [overview.md](overview.md).

## How it works

The rule Earshot holds to is simple: with Block at boot on, the AirPods'
device nodes are enabled exactly while you are using them on this PC, and
disabled the rest of the time. The steady state is disabled, which is what
stops the paging at boot. Nothing has to happen at shutdown for that to hold.

- **Blocking** disables each of the AirPods' own Bluetooth nodes with the
  persistent flag, so the disable survives a restart. Without that flag
  Windows would put them back at the next boot and the whole thing would
  quietly fail. Nodes are chosen by container and Bluetooth address, not by
  name alone, so a paired phone, the Bluetooth radio itself and any other
  device are never touched.
- **Allowing** enables the same nodes again, then Earshot waits for the audio
  endpoints to come back and asks the audio driver to reconnect.
- **The tray cannot do either by itself.** It starts the SYSTEM task
  `\Earshot\Gate`, which takes the device identity from `device.json` only,
  never from whoever started it, and accepts a fixed short list of commands.
  The tray then reads the real device state back itself rather than trusting
  the task's result.
- **While you are listening**, the nodes stay enabled. When the AirPods stop
  being used, and nothing else is in flight, Earshot blocks them again after
  about 30 seconds of quiet, a deliberately cautious figure rather than a
  measured one, so a brief drop does not cut off someone still listening. If
  a block does not take, it is tried again on a widening interval.
- **At startup**, `\Earshot\BootBlock` runs as SYSTEM before anyone signs in.
  If Block at boot is on and a target node is present and enabled, it blocks
  it, as a safety net for the case where the nodes were left enabled, after a
  crash for instance. Windows does not document where this task falls
  against its own reconnect, so it can lose the race. The design leans on
  the nodes already being disabled, not on this task winning.
- **Shutting down while connected** is the one case the at-rest rule does not
  cover; see [requirements.md](requirements.md#what-it-does-not-do) for what
  Earshot does about it and why it cannot always win.

With Block at boot off, Earshot still connects and disconnects, and
Disconnect only asks the AirPods to disconnect.

### Audio quality protection

Windows switches a Bluetooth headset from stereo A2DP to the Hands-Free
profile whenever an application opens a microphone or plays through the
communications category. Hands-Free is a narrow mono voice channel, and that
switch, not the codec, is what makes the AirPods suddenly sound like a phone
call:
https://learn.microsoft.com/en-us/windows-hardware/drivers/bluetooth/bluetooth-classic-audio

Earshot turns off the Hands-Free and Headset services for the AirPods and
leaves the A2DP sink alone, so there is nothing for Windows to switch to.
Earshot records exactly which services it turned off, and turns those back on
when you turn the setting off or uninstall. The change installs and removes
profile drivers, so it can be slow, and the audio endpoints come and go while
it runs; it runs through a SYSTEM task rather than in the tray. The change
can also lapse: reconnecting or restarting can bring the Hands-Free service
back, so Earshot re-reads the installed services after a connect and after
boot and re-applies it when it has reverted, so it can take effect a moment
after a connect rather than instantly.

Connect can take the protection off for a moment: the one-shot reconnect used
to bring the AirPods back is a Hands-Free property, so turning Hands-Free off
also removes the filter that carries the request. If the A2DP filter refuses
it, the card says "Trying another way": Earshot turns the protection off,
connects, and turns it back on. The microphone works on this PC while that
runs, and it is not instant, because the change installs and removes profile
drivers. If the protection cannot be put back, the card says "Connected, but
audio quality protection did not apply." and the microphone stays available
until a later connect puts it back.

## The safety model

- **Setup asks for one administrator prompt and does the rest itself.** It
  copies the folder to `C:\Program Files\Earshot` and checks every copied
  file against the SHA-256 recorded in `Earshot.files.json`, the list the
  release build writes. Only administrators can write to Program Files, so
  the program the scheduled tasks run later cannot be swapped for another
  one; a file that does not match stops the install.
- **`C:\ProgramData\Earshot`** has its inherited permissions removed: SYSTEM
  and administrators can write, you can read. It holds `device.json` (which
  device to block, as a Bluetooth address and container id), `config.json`
  (Block at boot), `protection.json` and `protection-intent.json` (which
  Bluetooth services Earshot turned off, so they can be turned back on), and
  one status file per run of the elevated worker.
- **Three scheduled tasks, all running as SYSTEM**, in their own Task
  Scheduler folder: `Gate` (blocks and allows the device nodes on demand),
  `Protect` (changes the Bluetooth services on demand, with a longer time
  limit because that installs and removes drivers) and `BootBlock` (at
  startup only). `Gate` and `Protect` grant your account read and execute,
  which is what lets the tray start them with no prompt, and that permission
  cannot be used to change what they run. `BootBlock` grants read only:
  nothing but its own startup trigger ever starts it, so you cannot start it
  either.
- **The device identity is read from `device.json`, never from whoever
  started the task.** Each task accepts a fixed short list of commands, and
  the tray reads the real device state back itself afterwards rather than
  trusting the task's own result.
- **Setup never changes your pairing**, and never touches the registry `Run`
  key. Your own settings stay in `%APPDATA%\Earshot\settings.json` and the
  log in `%LOCALAPPDATA%\Earshot\logs`, separate from the machine-wide state
  in `ProgramData`, because the elevated worker runs with nobody signed in
  and cannot read your profile.
- **A device that cannot play audio from this PC is refused**, a phone for
  instance, with the reason stated: "That device cannot play audio from this
  PC. Choose headphones or speakers."
- **Two environment variables keep testing off the real device.**
  `EARSHOT_DATA_ROOT` moves every data folder elsewhere, and
  `EARSHOT_SAFE_MODE` turns every device action off so only reads happen.
  `install`, `uninstall`, `gate` and `gate-protect` refuse to run while
  either is set. A `diag` target is refused in safe mode only, so
  `EARSHOT_DATA_ROOT` on its own does not stop a device action; the live
  test scripts stop the run themselves when it is set, rather than let a
  real device action write its evidence somewhere else.

## Setup and removal

Setup is the one-time flow started from **Set up Earshot...** in the tray
menu: one administrator prompt, then Earshot copies itself into Program
Files, verifies every copied file, and registers the three scheduled tasks
described above. Connect, disconnect and audio protection all work without
setup; only the boot block needs it, because disabling a device node needs
administrator rights.

To remove Earshot, turn **Open on startup** off in the tray menu first, then
close Earshot. The startup registry value belongs to the tray, and uninstall
does not touch it; Earshot removes a value left behind the next time it
starts with the setting off.

Then run, from an administrator PowerShell or Command Prompt:

    "C:\Program Files\Earshot\Earshot.exe" uninstall

Windows shows its administrator prompt when you open that window. Uninstall
refuses to run without it.

It enables every device node it disabled, turns back on the Bluetooth
services it recorded, deletes the three scheduled tasks and the `\Earshot`
task folder, removes `C:\ProgramData\Earshot`, and removes
`C:\Program Files\Earshot`, scheduling that last one for the next restart if
it is in use. Each step is reported. If a node or a service could not be
restored, it keeps the two files that record what to restore and says so, so
you can run it again.

Your pairing is never touched, and `%APPDATA%\Earshot` is left alone, so your
settings survive a reinstall.

Test 15, which exercises setup and this reversal live, has not yet run; the
elevated launch site it covers has never been exercised. See
[verification.md](verification.md).

## Command line

One program, `Earshot.exe`, chosen by its first argument.

| Command | What it does |
|---|---|
| `Earshot.exe` | The tray application. `--startup` is the same thing, and is what the startup value passes. |
| `Earshot.exe probe [audio\|topology\|nodes\|services\|task\|battery\|all] [--json] [--out <path>]` | Read-only diagnostics. Reads endpoints, walks the audio topology, reads the device nodes, lists the installed Bluetooth services, reads the scheduled tasks, and reports the battery answer described in [requirements.md](requirements.md). It changes nothing. On a machine where Earshot is not set up and no device is pinned it still writes a full report, and exits 78 to say so: the nodes and services targets had no device to read. So read the report rather than the exit code. |
| `Earshot.exe probe icon --out <folder>` | Writes the tray icon to files, in each of its four states, at three screen scalings and in both inks, for checking how it looks. |
| `Earshot.exe install <userSid> <address> <containerGuid> [--principal user]` / `Earshot.exe uninstall` | The one-time setup and its removal. Both need an elevated administrator and refuse to run as SYSTEM. The menu runs `install` with those three arguments filled in; a bare `install` is refused, so it is not a command to type by hand. |
| `Earshot.exe gate <verb> <nonce> [address]` / `Earshot.exe gate-protect <verb> <nonce>` | The elevated workers: one for the device nodes, one for the Bluetooth services. Started by Earshot's own scheduled tasks, not by hand. |
| `Earshot.exe diag <target>` | Single live actions for testing on real hardware: connect, disconnect, a raw driver request, a gate run, the unelevated service call, and the battery sweep. **All but the battery sweep and `gate status` change the state of the device**; those two only read. They exist for the live tests in `tools\live-tests` and are not part of normal use. |
