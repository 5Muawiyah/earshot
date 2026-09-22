# The live test self-test

The live tests are the only record of what the owner's AirPods actually did. They are
PowerShell, they run under `Set-StrictMode -Version 2.0`, and they can be parsed, reviewed and
still throw on their first criterion.

This runs them.

## The defect it exists for

PowerShell unrolls whatever a function writes to the pipeline. A helper that ends

```powershell
    return $found        # $found = @()
```

hands the caller `$null` when nothing matched and a bare `System.String` when one thing did.
Under strict mode 2.0 both `$null.Count` and `'one line'.Count` throw `PropertyNotFoundStrict`.
`.Length` works and `.Count` does not, and strict mode 3.0 throws too, so raising the version
is not a fix. The throw is caught by the script's own `catch`, which records `run: fail` and
abandons every criterion after it.

It fires on the success path. "No matching line" is exactly what a boot block that held
produces, so the tests broke hardest when the application was working.

The rule the code now follows has two halves:

* a helper that answers with a list returns it as `return ,$found`, so nothing is unrolled, and
* a caller assigns that plainly and writes `@($found).Count`, wrapping the **variable**.

Not `@(Get-EarshotLogLines ...)`. `@()` collects what the pipeline emits, and the leading comma
has already made the whole list one object by then, so wrapping the call wraps it twice and
`.Count` reads 1 for every answer including none. `@($variable)` is a no-op when the comma is
there and repairs the value when somebody takes it away, which is what makes the two halves
belt and braces rather than one contradicting the other.

`LiveTestScriptTests` holds both halves to the source. This folder proves them by running.

## What it runs

`Invoke-SelfTest.ps1` runs every shipped script, and both halves of every resumable one, in its
own `powershell.exe` 5.1 process against its own sandbox under `%TEMP%`. Eighteen scripts,
twenty-five halves, three sets of fake inputs (seventy-five runs), plus the bespoke extra cases
named against each row in `Invoke-SelfTest.ps1`'s `$tests` (twenty more), ninety-five runs in
total.

The three sets differ only in how many items every counted thing holds, because 0, 1 and more
than 1 are the three shapes a returned list takes:

| case   | log lines per pattern | items per evidence list |
| ------ | --------------------- | ----------------------- |
| `none` | 0                     | 0                       |
| `one`  | 1                     | 1                       |
| `two`  | 2                     | 2                       |

The counted things are the thirteen log patterns the scripts search for, the notifications in a
`diag ks` report, the steps in a `diag protect-unelevated` report, the watched values in a
battery sweep, the services `probe services` lists, and the services `protection.json` records
as turned off. One line is also written with a stamp in 2001, so the `-SinceUtc` filter in
`Get-EarshotLogLines` has a line it must drop, and the log holds a line with no stamp at all
and a line no pattern looks for.

## What it asserts

A case passes when all of these hold.

* Nothing was written to the error stream, and the output holds no `PropertyNotFoundStrict`,
  no missing `Count`, no method on a null value and no index into a null array.
* `result.json` exists, records no error, and carries no `run` criterion. `run` is the one the
  scripts add from their own `catch`, so its presence means the sitting was abandoned.
* Every criterion `expectations.psd1` names was recorded, and no criterion it does not name was.
* The overall outcome is the one the fake inputs imply.
* Every criterion whose outcome the fake inputs decide has that outcome.
* No question, option or command fell outside the fakes. Anything that does is written to
  `unanswered.txt` and fails the case, so a new owner question cannot be answered by accident.

## Nothing here touches a device

* `Invoke-Earshot` and `Invoke-EarshotElevated` are replaced, so `Earshot.exe` is never started.
* `Resolve-EarshotExe` is replaced, so no real `Earshot.exe` is even looked for.
* `Start-Process` is replaced inside every child with one that throws, so a helper that grew a
  new way of starting something stops the self-test rather than running it.
* `LOCALAPPDATA`, `APPDATA` and `ProgramData` are redirected into the sandbox. `ProgramFiles`
  cannot be: Windows resets it in every child process whatever the parent set, which is why the
  two criteria that read the program folder are marked `any` in the expectations.
* No scheduled task is registered, nothing is elevated, and the only registry reads are the two
  the scripts make themselves, both read-only.

Everything else runs for real: `Get-EarshotLogLines`, `Get-DiagEvidence`, `Copy-AppEvidence`,
`Read-KsEvidence`, `Get-TargetEndpointStates`, `Read-EarshotJsonFile`, `Show-Preconditions`,
`Add-Criterion` and `Complete-LiveTestRun`.

## The files

| file                 | what it is                                                                     |
| -------------------- | ------------------------------------------------------------------------------ |
| `Invoke-SelfTest.ps1`| the runner: builds the sandboxes, starts each half, checks what it recorded     |
| `Run-OneHalf.ps1`    | the driver inside each child: installs the stubs, then runs the shipped script  |
| `Fakes.psm1`         | the fake machine: its state, its reports, its log, and the owner's answers      |
| `expectations.psd1`  | what each half must record, per set of fake inputs                              |

The fake reports carry the member names the application actually writes. Those names are pinned
by `LiveTestFieldTests`, and a fake that used a different one would read as `$null`, which turns
up here as a criterion coming out the wrong way.

## Running it

```
powershell -NoProfile -ExecutionPolicy Bypass -File .\Invoke-SelfTest.ps1
```

It prints JSON and exits 0 when everything passed. `tools\check.ps1` runs it through
`LiveTestSelfTestTests`, so it runs on every gate.

Useful while working on it:

* `-Test 13` one test only
* `-Case none` one set of fake inputs only
* `-Keep` leave the sandboxes behind
* `-Observed` print what each case recorded instead of checking it, for working on the
  expectations. Never for proving anything.

## Adding a test

1. Add a row to `$tests` in `Invoke-SelfTest.ps1`. A shipped script with no row is reported as
   not covered, so this cannot be forgotten.
2. Add a start state to `StartStates` in `Fakes.psm1`, one line per half.
3. Add any new owner question to `Answers` or `Notes` in `Fakes.psm1`. A question with no answer
   fails the case and says which question it was.
4. Add the entry to `expectations.psd1`, reasoning from the start state, the answers and the
   counts. Use `-Observed` to see what it recorded, then check every outcome against the script
   before writing it down.
5. Raise `ExpectedScripts` and `ExpectedHalves` in `LiveTestSelfTestTests`.
