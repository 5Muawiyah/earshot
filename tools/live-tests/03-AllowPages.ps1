<#
.SYNOPSIS
    Does enabling the nodes on its own make Windows page the AirPods?

.DESCRIPTION
    Starting from blocked nodes, this allows them and then watches for a while
    without sending any connect request. If Windows pages the AirPods by itself,
    the render endpoint goes ACTIVE and the AirPods leave the phone.

    The answer matters twice. Earshot's connect path allows first and then sends
    the reconnect, so paging on its own would be a shortcut. Turning Block at boot
    off also allows the nodes straight away, and if that alone steals the AirPods
    from the phone, the menu item needs to say so.

    Nothing in the build depends on either answer, so a no here confirms the design
    rather than breaking it.

.PARAMETER ExePath
    Earshot.exe: the installed copy or an unzipped release.

.PARAMETER RunRoot
    The evidence folder to write into. Leave it out for a new one.

.PARAMETER WatchSeconds
    How long to watch after the allow before deciding. 120 by default.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\03-AllowPages.ps1 -ExePath "C:\Program Files\Earshot\Earshot.exe"
#>

#Requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [string]$RunRoot = '',
    [int]$WatchSeconds = 120
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'LiveTest.psm1') -Force

$run = New-LiveTestRun -TestId '03-allow-pages' -Title 'Whether an allow alone pages the AirPods' `
    -Settles 'Whether enabling the nodes can take the AirPods off the phone with no connect request, which decides if Block at boot off needs a warning and whether the connect path can be shortened.' `
    -ExePath $ExePath -RunRoot $RunRoot

try
{
    $ready = Show-Preconditions -Run $run -Preconditions @(
        'Earshot is installed and set up.',
        'The AirPods Bluetooth nodes are blocked. If they are not, run 04-BlockAndReboot.ps1 first, or block them from the tray.',
        'The AirPods are connected to your phone and playing.',
        'The Earshot tray is closed, so it cannot connect or re-block during the watch.'
    ) -PhysicalActions @(
        'Wear the AirPods and keep audio playing from your phone for the whole watch.',
        'Tell this test at the end whether the audio ever left your phone.'
    )

    if ($ready)
    {
        Write-Section -Run $run -Title 'Baseline'
        $nodes = Get-NodeState -Run $run -Label 'nodes-before'
        $nodeState = Get-Field -Object $nodes -Name 'nodeState'
        $audio = Get-AudioState -Run $run -Label 'audio-before'
        $states = Get-TargetEndpointStates -AudioJson $audio
        Write-Line -Run $run -Text ('Nodes: ' + $nodeState + '. Render: ' + $states.Render + '. Capture: ' + $(if ([string]::IsNullOrEmpty($states.Capture)) { 'none' } else { $states.Capture }) + '.')

        if ($nodeState -ne 'Blocked')
        {
            Add-Criterion -Run $run -Id 'preconditions' -Criterion 'The nodes start blocked.' -Outcome 'inconclusive' `
                -Detail ('They read ' + $nodeState + '. Block them first, then run this test again.')
        }
        else
        {
            Add-Criterion -Run $run -Id 'preconditions' -Criterion 'The nodes start blocked and the AirPods are on the phone.' `
                -Outcome 'pass' -Detail ('nodes ' + $nodeState + ', render ' + $states.Render + '.')

            Write-Section -Run $run -Title 'Allow, then watch and send nothing'
            $allow = Invoke-Earshot -Run $run -Label 'gate-allow' -Command @('diag', 'gate', 'allow') -Live `
                -Consequence 'Enables the AirPods Bluetooth nodes through the elevated task. No connect request is sent. Windows may still page them by itself, which is what this test is measuring.'

            if ($null -eq $allow)
            {
                Add-Criterion -Run $run -Id 'allow' -Criterion 'The allow ran.' -Outcome 'inconclusive' -Detail 'It was skipped.'
            }
            else
            {
                $nodes = Get-NodeState -Run $run -Label 'nodes-after-allow'
                Add-Criterion -Run $run -Id 'allow' -Criterion 'The allow leaves the nodes Allowed.' `
                    -Outcome $(if ((Get-Field -Object $nodes -Name 'nodeState') -eq 'Allowed') { 'pass' } else { 'fail' }) `
                    -Detail ('They read ' + (Get-Field -Object $nodes -Name 'nodeState') + '.')

                $wentActive = $false
                $firstActiveAfter = $null
                $elapsed = 0
                $interval = 10
                while ($elapsed -lt $WatchSeconds -and -not $wentActive)
                {
                    Wait-Seconds -Run $run -Seconds $interval -Reason 'watching whether Windows pages the AirPods on its own'
                    $elapsed = $elapsed + $interval
                    $audio = Get-AudioState -Run $run -Label ('audio-watch-' + $elapsed)
                    $states = Get-TargetEndpointStates -AudioJson $audio
                    Write-Line -Run $run -Text ('  after ' + $elapsed + ' s: render ' + $states.Render + ', capture ' + $(if ([string]::IsNullOrEmpty($states.Capture)) { 'none' } else { $states.Capture }))
                    if ($states.Render -eq 'Active')
                    {
                        $wentActive = $true
                        $firstActiveAfter = $elapsed
                    }
                }

                $heard = Read-Answer -Run $run -Question 'Did the audio ever leave your phone during the watch?'
                Add-Criterion -Run $run -Id 'no-auto-page' -Criterion ('The AirPods stay off this PC for ' + $WatchSeconds + ' s after the allow, with no connect request sent.') `
                    -Outcome $(if ($wentActive -or $heard -eq 'yes') { 'fail' } else { 'pass' }) `
                    -Detail $(if ($wentActive) { 'The render endpoint reached ACTIVE after about ' + $firstActiveAfter + ' s.' } else { 'The render endpoint never reached ACTIVE; you answered ' + $heard + '.' })

                Add-Finding -Run $run -Name 'allowAlonePagesTheAirPods' -Value $(if ($wentActive -or $heard -eq 'yes') { 'yes' } else { 'no' }) `
                    -Detail 'yes means turning Block at boot off can take the AirPods off the phone at that moment, and the connect path could skip its own reconnect.'
                if ($null -ne $firstActiveAfter)
                {
                    Add-Finding -Run $run -Name 'secondsUntilWindowsPaged' -Value $firstActiveAfter
                }
            }

            Write-Section -Run $run -Title 'Leaving the machine blocked again'
            $block = Invoke-Earshot -Run $run -Label 'gate-block' -Command @('diag', 'gate', 'block') -Live `
                -Consequence 'Disables the AirPods Bluetooth nodes again, which is how the machine should be left between tests.'
            if ($null -ne $block)
            {
                $nodes = Get-NodeState -Run $run -Label 'nodes-final'
                Add-Criterion -Run $run -Id 'left-blocked' -Criterion 'The nodes are left blocked.' `
                    -Outcome $(if ((Get-Field -Object $nodes -Name 'nodeState') -eq 'Blocked') { 'pass' } else { 'fail' }) `
                    -Detail ('They read ' + (Get-Field -Object $nodes -Name 'nodeState') + '.')
            }
        }

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
    Write-Host ('Test 03 finished: ' + $overall)
}

# 0 pass, 1 fail, 2 inconclusive. Anything that starts a test can read the outcome without
# parsing result.json, and a test that recorded a failure is never read as a clean run.
exit (Get-LiveTestExitCode -Overall $overall)
