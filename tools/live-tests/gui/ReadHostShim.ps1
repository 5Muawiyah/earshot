<#
    The shared core of the line protocol between a live test script and the test window
    (test-gui.md section 5.1). This text is load-bearing: keep its behaviour exactly. The
    production driver, Invoke-GuiHalf.ps1, dot-sources this file and then defines the two-line
    global:Read-Host wrapper itself; the self-test's own driver (slice S4) dot-sources this same
    file and defines a different wrapper round the same core.

    Wire format, child to window, one message per stdout line: the prefix, then base64 of UTF-8
    JSON. Window to child: "R <seq> <base64>" for a reply or "A <seq>" to abort, ASCII, newline
    terminated. The shim never strips or repairs a malformed reply; it throws, and the calling
    script's own catch and finally run.
#>

$global:EarshotTwSeq = 0
$global:EarshotTwPrefix = '@@EARSHOT-TW@@ '

function global:Send-EarshotTwMessage
{
    param([Parameter(Mandatory = $true)]$Message)

    $json = ($Message | ConvertTo-Json -Compress -Depth 6)
    $wire = [System.Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($json))
    [System.Console]::Out.WriteLine($global:EarshotTwPrefix + $wire)
    [System.Console]::Out.Flush()
}

function global:Read-EarshotTwReply
{
    param([string]$Prompt = '')

    $global:EarshotTwSeq = $global:EarshotTwSeq + 1
    $seq = $global:EarshotTwSeq
    $frames = Get-PSCallStack
    $stack = @()
    $caller = ''
    $bound = @{}
    foreach ($frame in @($frames))
    {
        $name = [string]$frame.Command
        if ($name -eq 'Read-EarshotTwReply' -or $name -eq 'Read-Host') { continue }
        $stack = $stack + @($name)
        if ($caller -eq '')
        {
            $caller = $name
            $info = $frame.InvocationInfo
            if ($null -ne $info)
            {
                foreach ($key in $info.BoundParameters.Keys)
                {
                    if ($key -ne 'Run') { $bound[$key] = $info.BoundParameters[$key] }
                }
            }
        }
    }

    Send-EarshotTwMessage -Message ([ordered]@{
        type = 'prompt'; seq = $seq; caller = $caller; stack = $stack; prompt = $Prompt; bound = $bound })

    $line = [System.Console]::In.ReadLine()
    if ($null -eq $line)
    {
        throw 'The test window closed before this was answered, so nothing was answered for you.'
    }

    if ($line -match '^A (\d+)$' -and [int]$Matches[1] -eq $seq)
    {
        throw 'Stopped from the test window at your request.'
    }

    if ($line -notmatch '^R (\d+) ([A-Za-z0-9+/=]*)$' -or [int]$Matches[1] -ne $seq)
    {
        throw ('The test window sent a reply this script could not read, so nothing was answered: ' + $line)
    }

    return [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($Matches[2]))
}
