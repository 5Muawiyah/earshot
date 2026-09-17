<#
.SYNOPSIS
    Which thread and apartment the audio notifications arrive on, and in what order.

.DESCRIPTION
    Earshot keeps every Core Audio object on one worker thread in a multi-threaded
    apartment, because those objects are not safe to touch from another apartment.
    The notification callbacks come from Windows, not from Earshot, and nothing
    documents which thread it uses or in what order the events arrive.

    This test runs a full connect, a full disconnect and a protection change, and
    reads the thread id, the apartment and the event order that the diag evidence
    recorded for each. It also records how many notifications one change produces,
    which is what the coalescing window is sized for.

.PARAMETER ExePath
    Earshot.exe: the installed copy or an unzipped release.

.PARAMETER RunRoot
    The evidence folder to write into. Leave it out for a new one.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\12-CallbackThread.ps1 -ExePath "C:\Program Files\Earshot\Earshot.exe"
#>

#Requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [string]$RunRoot = ''
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'LiveTest.psm1') -Force

$run = New-LiveTestRun -TestId '12-callback-thread' -Title 'Notification thread, apartment and event order' `
    -Settles 'Which thread and apartment Core Audio calls back on during a connect, a disconnect and a protection change, and how many notifications one change produces.' `
    -ExePath $ExePath -RunRoot $RunRoot

# Prints the notification list from a diag evidence file and returns what it found.
function Read-Notifications
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        $Evidence,
        [Parameter(Mandatory = $true)][string]$What
    )

    $threads = @()
    $apartments = @()
    $count = 0
    foreach ($notification in (Get-Field -Object $Evidence -Name 'notifications'))
    {
        $count = $count + 1
        $thread = Get-Field -Object $notification -Name 'threadId'
        $apartment = Get-Field -Object $notification -Name 'apartment'
        if (-not ($threads -contains $thread)) { $threads = $threads + @($thread) }
        if (-not ($apartments -contains $apartment)) { $apartments = $apartments + @($apartment) }
        Write-Line -Run $Run -Text ('  ' + (Get-Field -Object $notification -Name 'sequence') + ' ' +
            (Get-Field -Object $notification -Name 'utc') + ' ' + (Get-Field -Object $notification -Name 'kind') +
            ' state ' + (Get-Field -Object $notification -Name 'newState') +
            ' after ' + (Get-Field -Object $notification -Name 'millisecondsAfterRequest') + ' ms' +
            ' on thread ' + $thread + ' (' + $apartment + ')')
    }

    Write-Line -Run $Run -Text ('  ' + $What + ': ' + $count + ' notification(s), thread(s) ' + ($threads -join ', ') +
        ', apartment(s) ' + ($apartments -join ', '))
    return [ordered]@{ Count = $count; Threads = $threads; Apartments = $apartments }
}

try
{
    $ready = Show-Preconditions -Run $run -Preconditions @(
        'Earshot is installed and set up.',
        'The AirPods are paired with this PC and currently disconnected from it.',
        'The AirPods Bluetooth nodes are allowed, not blocked.',
        'The Earshot tray is closed, so only this test drives the device.'
    ) -PhysicalActions @(
        'Wear the AirPods so you can tell when each step actually happened.'
    )

    if ($ready)
    {
        Write-Section -Run $run -Title 'Connect through the full controller'
        $connect = Invoke-Earshot -Run $run -Label 'diag-connect' -Command @('diag', 'connect') -Live `
            -Consequence 'Runs the whole connect path once: walk, send, then wait for the notifications to confirm. The AirPods join this PC.' `
            -TimeoutSeconds 180
        $connectFound = $null
        if ($null -ne $connect)
        {
            $evidence = Get-DiagEvidence -Run $run
            $summary = Read-KsEvidence -Run $run -Evidence $evidence
            $connectFound = Read-Notifications -Run $run -Evidence $evidence -What 'connect'
            Add-Finding -Run $run -Name 'connectNotifications' -Value $connectFound.Count
            Add-Finding -Run $run -Name 'connectConfirmationSource' -Value ('' + $summary.Source)
        }

        Write-Section -Run $run -Title 'Disconnect through the full controller'
        $disconnect = Invoke-Earshot -Run $run -Label 'diag-disconnect' -Command @('diag', 'disconnect') -Live `
            -Consequence 'Runs the whole disconnect path once. The AirPods leave this PC and can return to your phone.' `
            -TimeoutSeconds 180
        $disconnectFound = $null
        if ($null -ne $disconnect)
        {
            $evidence = Get-DiagEvidence -Run $run
            [void](Read-KsEvidence -Run $run -Evidence $evidence)
            $disconnectFound = Read-Notifications -Run $run -Evidence $evidence -What 'disconnect'
            Add-Finding -Run $run -Name 'disconnectNotifications' -Value $disconnectFound.Count
        }

        $threads = @()
        $apartments = @()
        foreach ($found in @($connectFound, $disconnectFound))
        {
            if ($null -eq $found) { continue }
            foreach ($thread in $found.Threads) { if (-not ($threads -contains $thread)) { $threads = $threads + @($thread) } }
            foreach ($apartment in $found.Apartments) { if (-not ($apartments -contains $apartment)) { $apartments = $apartments + @($apartment) } }
        }

        Add-Criterion -Run $run -Id 'notifications-arrive' -Criterion 'Notifications arrive for both a connect and a disconnect.' `
            -Outcome $(if ($threads.Count -gt 0) { 'pass' } else { 'fail' }) `
            -Detail ('threads seen: ' + ($threads -join ', ') + '.')

        Add-Criterion -Run $run -Id 'apartment' -Criterion 'Every callback arrives in a multi-threaded apartment, which is what the worker is built for.' `
            -Outcome $(if ($apartments.Count -gt 0 -and -not ($apartments -contains 'STA')) { 'pass' } elseif ($apartments.Count -eq 0) { 'inconclusive' } else { 'fail' }) `
            -Detail ('apartments seen: ' + ($apartments -join ', ') + '.')

        Add-Finding -Run $run -Name 'notificationThreads' -Value ($threads -join ', ')
        Add-Finding -Run $run -Name 'notificationApartments' -Value ($apartments -join ', ')

        Write-Section -Run $run -Title 'A protection change, which is the noisiest event'
        Write-Line -Run $run -Text 'Turning Handsfree off removes a capture endpoint and its filter, and turning it on puts them back.'
        Write-Line -Run $run -Text 'That is the burst the coalescing window has to absorb without the connection state flickering.'
        $services = Get-ServiceState -Run $run -Label 'services-before'
        $wasProtected = (Get-Field -Object $services -Name 'protection') -eq 'Protected'
        $toggleTo = 'protect-on'
        if ($wasProtected) { $toggleTo = 'protect-off' }

        if ($toggleTo -eq 'protect-off')
        {
            $toggled = Invoke-Earshot -Run $run -Label 'gate-protect-off' -Command @('diag', 'gate', 'protect-off') -Live `
                -Consequence 'Turns the Handsfree and Headset services back on, which reinstalls their profile drivers and adds a capture endpoint. It can take minutes.' `
                -TimeoutSeconds 420
        }
        else
        {
            $toggled = Invoke-Earshot -Run $run -Label 'gate-protect-on' -Command @('diag', 'gate', 'protect-on') -Live `
                -Consequence 'Turns the Handsfree and Headset services off, which removes their profile drivers and a capture endpoint. It can take minutes.' `
                -TimeoutSeconds 420
        }

        if ($null -ne $toggled)
        {
            $audio = Get-AudioState -Run $run -Label 'audio-after-protection-change'
            $states = Get-TargetEndpointStates -AudioJson $audio
            Write-Line -Run $run -Text ('Render ' + $states.Render + ', capture ' + $states.Capture + ', derived ' + $states.Connection)
            Add-Criterion -Run $run -Id 'protection-churn' -Criterion 'A protection change does not leave the connection state wrong.' `
                -Outcome $(if ($null -ne $states.Connection) { 'pass' } else { 'inconclusive' }) `
                -Detail ('The derived state reads ' + $states.Connection + '. The render endpoint, not the capture one, is what drives it.')

            Write-Line -Run $run -Text 'Put protection back the way it was before the test.'
            if ($wasProtected)
            {
                [void](Invoke-Earshot -Run $run -Label 'gate-protect-on-restore' -Command @('diag', 'gate', 'protect-on') -Live `
                    -Consequence 'Returns protection to on, the state the machine was in before this test.' -TimeoutSeconds 420)
            }
            else
            {
                [void](Invoke-Earshot -Run $run -Label 'gate-protect-off-restore' -Command @('diag', 'gate', 'protect-off') -Live `
                    -Consequence 'Returns protection to off, the state the machine was in before this test.' -TimeoutSeconds 420)
            }

            [void](Get-ServiceState -Run $run -Label 'services-final')
        }

        Write-Section -Run $run -Title 'Long session, and the unregister at exit'
        Write-Line -Run $run -Text 'The other half of this question is whether notifications keep arriving over hours and whether the'
        Write-Line -Run $run -Text 'client unregisters cleanly at exit. That needs a long tray session rather than this test.'
        Wait-Owner -Run $run -Text 'Leave Earshot running for a few hours of normal use today, then exit it from the menu and come back to this.'
        $unregister = Get-EarshotLogLines -Run $run -Pattern 'unregister'
        foreach ($line in ($unregister | Select-Object -Last 5)) { Write-Line -Run $run -Text ('  ' + $line) }
        $stopped = Get-EarshotLogLines -Run $run -Pattern 'Tray stopped.'
        Add-Criterion -Run $run -Id 'clean-exit' -Criterion 'The tray exits cleanly, with the notification client unregistered and no late callback.' `
            -Outcome $(if ($stopped.Count -gt 0) { 'pass' } else { 'inconclusive' }) `
            -Detail ([string]$stopped.Count + ' clean stop line(s) in the log, ' + $unregister.Count + ' unregister line(s).')

        Save-EarshotLog -Run $run
    }
}
catch
{
    Write-Failure -Run $run -Message ('The test stopped with an error: ' + ($_ | Out-String).Trim())
    Add-Criterion -Run $run -Id 'run' -Criterion 'The test ran to the end.' -Outcome 'fail' -Detail 'See the error above. Run 00-Restore.ps1 before the next test.'
}
finally
{
    $overall = Complete-LiveTestRun -Run $run
    Write-Host ('Test 12 finished: ' + $overall)
}

# 0 pass, 1 fail, 2 inconclusive. Anything that starts a test can read the outcome without
# parsing result.json, and a test that recorded a failure is never read as a clean run.
exit (Get-LiveTestExitCode -Overall $overall)
