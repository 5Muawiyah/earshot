param([string]$Action, [switch]$DryRun, [switch]$RemoveSettings, [hashtable]$Roots, [string]$Feed, [hashtable]$TestHooks)
# Earshot installer: installs, updates, repairs or uninstalls Earshot from a GitHub release.
#
# For a person:  irm https://github.com/5Muawiyah/earshot/releases/latest/download/earshot.ps1 | iex
# For a tool:    & ([scriptblock]::Create((irm https://github.com/5Muawiyah/earshot/releases/latest/download/earshot.ps1))) -Action Install
#
# What it does, in order: refuses an administrator shell; resolves the release once; downloads the zip and its
# .sha256 into a new folder under %TEMP%; checks the SHA-256 before anything is unzipped; runs the unpacked copy's
# read-only "probe setup-values" to learn the values setup needs; then asks Windows for administrator approval ONCE and
# hands the checked zip to Earshot's own elevated verbs, which copy it into an administrators-only folder, check it
# again against the hash on their command line and install only what the release's file list names.
#
# What the checksum proves: the script, the zip and the checksum come from the same release, so the checksum catches a
# damaged or cut-short download. It cannot catch a compromised release, because whoever could change the zip could
# change the checksum and this script with it.
#
# What it never does: change or keep an execution policy, turn off or weaken Defender, SmartScreen or UAC, add or strip
# Zone.Identifier, send a header of its own, fetch or run any other script, run or unzip anything before its SHA-256
# matched, ask for administrator approval more than once, start Earshot as administrator, stop a process, or exit.
# It ends with one line: "Earshot: done." or "Earshot: stopped. <why>".
& {
    $ErrorActionPreference = 'Stop'

    $UsageLine = 'Usage: & ([scriptblock]::Create((irm https://github.com/5Muawiyah/earshot/releases/latest/download/earshot.ps1))) -Action Install|Update|Repair|Uninstall [-DryRun] [-RemoveSettings]'
    $StopMarker = 'EARSHOT-STOP: '
    $MaxZipBytes = 512MB
    $MaxChecksumBytes = 4096
    $MaxZipPathLength = 300
    # Waiting budgets, chosen here and not measured. The tray's own Exit is given 45 s by the update (UpdateActions.TrayExitWait),
    # so this script waits 60 s, a little longer, before it says the tray did not close. The record of an update is waited for
    # 300 s, as long as the first install's own wait for the install it starts (UpdateActions.InstallWait).
    $TrayWaitSecondsDefault = 60
    $OutcomeWaitSecondsDefault = 300

    # State shared by the functions below. Nothing here outlives the script block.
    $st = @{
        Folder = $null; Verified = $false; Redirected = [Console]::IsOutputRedirected
        Paths = $null           # the folders of this PC's install, set once they are known, so a stop can look for a tray still running
        ClosedTrays = @()       # the trays this script asked to exit, so a stop can start them again
        Succeeded = $false      # an install, update, repair or uninstall reported success: nothing is started again
        NoRestart = $false      # the elevated program may still be working in the install folder: nothing is started
        OutcomeProblem = ''     # why the last read of the update's record failed, if it did
    }
    $hk = $TestHooks
    if ($null -eq $hk) { $hk = @{} }
    if ($null -ne $hk.Redirected) { $st.Redirected = [bool]$hk.Redirected }

    # ---------------------------------------------------------------- output

    function Write-Line([string]$Text) { Write-Host $Text }

    function Stop-Run([string]$Reason) { throw ($StopMarker + $Reason) }

    function Format-Megabytes([long]$Bytes) {
        ($Bytes / 1MB).ToString('0.0', [Globalization.CultureInfo]::InvariantCulture) + ' MB'
    }

    # ---------------------------------------------------------------- small pure helpers (tested through Expose)

    # One command line for CreateProcess: each argument as CommandLineToArgvW reads it back. Windows PowerShell 5.1 does not
    # quote the elements of an array given to Start-Process, so the line is built here.
    # https://learn.microsoft.com/en-us/cpp/c-language/parsing-c-command-line-arguments
    function ConvertTo-ArgumentLine([string[]]$Arguments) {
        $parts = New-Object System.Collections.Generic.List[string]
        foreach ($a in $Arguments) {
            if ($a.Length -gt 0 -and $a.IndexOfAny([char[]]@(' ', "`t", "`n", "`v", '"')) -lt 0) {
                $parts.Add($a)
                continue
            }
            $sb = New-Object System.Text.StringBuilder
            [void]$sb.Append([char]34)
            $backslashes = 0
            foreach ($c in $a.ToCharArray()) {
                if ($c -eq [char]92) { $backslashes++ }
                elseif ($c -eq [char]34) {
                    [void]$sb.Append([char]92, ($backslashes * 2) + 1).Append([char]34)
                    $backslashes = 0
                }
                else {
                    [void]$sb.Append([char]92, $backslashes).Append($c)
                    $backslashes = 0
                }
            }
            [void]$sb.Append([char]92, $backslashes * 2).Append([char]34)
            $parts.Add($sb.ToString())
        }
        return ($parts -join ' ')
    }

    # The checksum file: one line, "<64 hex>  <zip name>" or "<64 hex> *<zip name>", optional byte order mark and trailing
    # line break. The name must be the zip's own, so a checksum published for another file never vouches for this one.
    # The same rule as the application's own updater. Returns the hex, or $null.
    function Read-Checksum([byte[]]$Bytes, [string]$ZipName) {
        $encoding = New-Object System.Text.UTF8Encoding($false, $true)
        $text = $null
        try { $text = $encoding.GetString($Bytes) }
        catch { return $null }
        if ($text.Length -gt 0 -and $text[0] -eq [char]0xFEFF) { $text = $text.Substring(1) }
        $line = $text.TrimEnd([char]13, [char]10)
        if ($line.IndexOf([char]10) -ge 0 -or $line.IndexOf([char]13) -ge 0) { return $null }
        if ($line.Length -ne (64 + 2 + $ZipName.Length)) { return $null }
        if ($line.Substring(0, 64) -notmatch '^[0-9A-Fa-f]{64}$') { return $null }
        $separator = $line.Substring(64, 2)
        if ($separator -ne '  ' -and $separator -ne ' *') { return $null }
        if (-not $line.Substring(66).Equals($ZipName, [StringComparison]::Ordinal)) { return $null }
        return $line.Substring(0, 64)
    }

    function Get-Sha256([string]$Path) {
        $sha = [System.Security.Cryptography.SHA256]::Create()
        $stream = [IO.File]::OpenRead($Path)
        try { $hash = $sha.ComputeHash($stream) }
        finally { $stream.Dispose(); $sha.Dispose() }
        return ([BitConverter]::ToString($hash)).Replace('-', '')
    }

    # major.minor.patch of a program file's release version, or '' when it has none.
    function Get-ProgramVersion([string]$Path) {
        if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return '' }
        $text = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path).ProductVersion
        if (-not $text) { return '' }
        $plus = $text.IndexOf('+')
        if ($plus -ge 0) { $text = $text.Substring(0, $plus) }
        if ($text -match '^(\d{1,9})\.(\d{1,9})\.(\d{1,9})(\.\d{1,9})?$') { return ($Matches[1] + '.' + $Matches[2] + '.' + $Matches[3]) }
        return ''
    }

    function Test-Declined($Failure) {
        $e = $Failure
        while ($null -ne $e) {
            if ($e -is [System.ComponentModel.Win32Exception] -and $e.NativeErrorCode -eq 1223) { return $true }
            if ($e.Message -match 'canceled by the user') { return $true }
            $e = $e.InnerException
        }
        return $false
    }

    # Whether anyone can answer a question, from what the host says about itself: a console host, a user session, input that
    # is not redirected, and no -NonInteractive among the host's own arguments. Windows PowerShell 5.1 reads -noni as that
    # switch and nothing shorter (probed: -non is taken as a command), so four letters is the least that counts. The arguments
    # are the host's, so a script argument spelled like it only ever makes this say no, which asks nothing.
    function Test-CanAnswer([string[]]$CommandLineArgs, [string]$HostName, [bool]$InputRedirected, [bool]$UserInteractive) {
        if (-not $UserInteractive) { return $false }
        if ($HostName -ne 'ConsoleHost') { return $false }
        if ($InputRedirected) { return $false }
        foreach ($a in @($CommandLineArgs)) {
            if (-not $a) { continue }
            if (-not ($a.StartsWith('-') -or $a.StartsWith('/'))) { continue }
            $name = $a.TrimStart([char]45, [char]47).ToLowerInvariant()
            if ($name.Length -ge 4 -and 'noninteractive'.StartsWith($name)) { return $false }
        }
        return $true
    }

    # Whether a Run value starts exactly one of these programs in the form Earshot writes it: the quoted path, a space and
    # --startup. Another program that merely names the path, or another file beside it, is not ours.
    function Test-OursRunValue([string]$Text, [string[]]$Programs) {
        foreach ($program in @($Programs)) {
            if ($Text.Equals(('"' + $program + '" --startup'), [StringComparison]::OrdinalIgnoreCase)) { return $true }
        }
        return $false
    }

    function Test-Under([string]$Path, [string]$Folder) {
        if (-not $Path -or -not $Folder) { return $false }
        $full = [IO.Path]::GetFullPath($Folder).TrimEnd([char]92) + [char]92
        return [IO.Path]::GetFullPath($Path).StartsWith($full, [StringComparison]::OrdinalIgnoreCase)
    }

    if ($hk.Expose) {
        & $hk.Expose @{
            ConvertToArgumentLine = ${function:ConvertTo-ArgumentLine}
            ReadChecksum = ${function:Read-Checksum}
            GetSha256 = ${function:Get-Sha256}
            GetProgramVersion = ${function:Get-ProgramVersion}
            TestDeclined = ${function:Test-Declined}
            TestCanAnswer = ${function:Test-CanAnswer}
            TestOursRunValue = ${function:Test-OursRunValue}
        }
        return
    }

    # ---------------------------------------------------------------- the machine and the person

    function Test-Elevated {
        if ($hk.IsElevated) { return [bool](& $hk.IsElevated) }
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        try { return (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
        finally { $identity.Dispose() }
    }

    # Whether anyone can answer a question, asked of the real host (Test-CanAnswer decides).
    function Test-CanAsk {
        if ($null -ne $hk.CanAsk) { return [bool]$hk.CanAsk }
        return (Test-CanAnswer ([Environment]::GetCommandLineArgs()) $Host.Name ([Console]::IsInputRedirected) ([Environment]::UserInteractive))
    }

    function Read-Answer {
        if ($hk.ReadLine) { return [string](& $hk.ReadLine) }
        return [string](Read-Host)
    }

    # The action named on the command line, or chosen from the menu. The names are checked here, not by a ValidateSet on the
    # parameter: a validated parameter cannot be given its empty default when the script is run through iex, which turns the
    # one-line install into an error.
    function Resolve-Action {
        if ($Action) {
            $named = @('Install', 'Update', 'Repair', 'Uninstall') | Where-Object { $_ -eq $Action } | Select-Object -First 1
            if (-not $named) { Stop-Run ('The action must be Install, Update, Repair or Uninstall, not ' + $Action + '.') }
            return $named
        }
        if (-not (Test-CanAsk)) {
            Write-Line $UsageLine
            Stop-Run 'No action was given and no one is here to ask.'
        }
        Write-Line 'Earshot'
        Write-Line '  1  Install'
        Write-Line '  2  Update'
        Write-Line '  3  Repair'
        Write-Line '  4  Uninstall'
        Write-Line 'Choose 1 to 4, or press Enter to cancel'
        $answer = (Read-Answer).Trim()
        switch ($answer) {
            '1' { return 'Install' }
            '2' { return 'Update' }
            '3' { return 'Repair' }
            '4' { return 'Uninstall' }
            '' { Stop-Run 'Cancelled.' }
            default { Stop-Run ('"' + $answer + '" is not one of the choices.') }
        }
    }

    function Get-RootSet {
        $programFiles = $env:ProgramW6432
        if (-not $programFiles) { $programFiles = [Environment]::GetFolderPath('ProgramFiles') }
        $local = [Environment]::GetFolderPath('LocalApplicationData')
        $set = @{
            Install = Join-Path $programFiles 'Earshot'
            Machine = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'Earshot'
            UserPrograms = Join-Path (Join-Path $local 'Programs') 'Earshot'
            Roaming = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Earshot'
            Local = Join-Path $local 'Earshot'
            Temp = [IO.Path]::GetTempPath()
            RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
        }
        if ($null -ne $Roots) { foreach ($key in @($Roots.Keys)) { $set[$key] = $Roots[$key] } }
        return $set
    }

    # ---------------------------------------------------------------- the release feed

    function Get-FeedAddresses {
        if (-not $Feed) {
            return @{
                Api = 'https://api.github.com/repos/5Muawiyah/earshot/releases/latest'
                Downloads = 'https://github.com/5Muawiyah/earshot/releases/download/'
                Loopback = $false
            }
        }
        $uri = $null
        if (-not [Uri]::TryCreate($Feed, [UriKind]::Absolute, [ref]$uri)) { Stop-Run 'The feed address is not a web address.' }
        $loopback = ($uri.Scheme -eq 'http' -and $uri.Host -eq '127.0.0.1')
        if ($uri.Scheme -ne 'https' -and -not $loopback) { Stop-Run 'The feed address must be https.' }
        $base = $Feed.TrimEnd([char]47) + '/'
        return @{
            Api = $base + 'repos/5Muawiyah/earshot/releases/latest'
            Downloads = $base + '5Muawiyah/earshot/releases/download/'
            Loopback = $loopback
        }
    }

    function Enable-Tls12 {
        $current = [Net.ServicePointManager]::SecurityProtocol
        if ($current -ne [Net.SecurityProtocolType]::SystemDefault -and -not ($current -band [Net.SecurityProtocolType]::Tls12)) {
            [Net.ServicePointManager]::SecurityProtocol = $current -bor [Net.SecurityProtocolType]::Tls12
        }
    }

    function Get-FailureStatus($Failure) {
        if ($Failure.Exception -and $Failure.Exception.Response) { return [int]$Failure.Exception.Response.StatusCode }
        return 0
    }

    function Stop-ForStatus([int]$Status, [string]$What, [string]$MissingMessage = '', [string]$Detail = '') {
        if ($Status -eq 404 -and $MissingMessage) { Stop-Run $MissingMessage }
        if ($Status -eq 404) { Stop-Run ('GitHub does not have ' + $What + ' (404).') }
        if ($Status -eq 403 -or $Status -eq 429) { Stop-Run ('GitHub refused the request (' + $Status + '). Try again later.') }
        if ($Status -gt 0) { Stop-Run ('GitHub answered ' + $Status + ' for ' + $What + '.') }
        if ($Detail) { Stop-Run ('Could not reach GitHub for ' + $What + ': ' + $Detail) }
        Stop-Run ('Could not reach GitHub for ' + $What + '.')
    }

    # The latest release, once, with PowerShell's own default headers. Drafts and pre-releases are not offered by the
    # latest-release address, and are refused here as well.
    function Get-LatestRelease($Addresses) {
        Write-Line 'Looking for the latest release...'
        $release = $null
        try { $release = Invoke-RestMethod -Uri $Addresses.Api -Method Get }
        catch { Stop-ForStatus (Get-FailureStatus $_) 'the latest release' '' ([string]$_.Exception.GetBaseException().Message) }
        $tag = [string]$release.tag_name
        if ($tag -notmatch '^v(\d{1,9})\.(\d{1,9})\.(\d{1,9})$') { Stop-Run ('The latest release has a tag this script does not read: ' + $tag) }
        if ($release.draft -or $release.prerelease) { Stop-Run 'The latest release is not a published release.' }
        $version = $Matches[1] + '.' + $Matches[2] + '.' + $Matches[3]
        $zip = 'Earshot-' + $version + '-win-x64.zip'
        $names = @($release.assets | ForEach-Object { [string]$_.name })
        if ($names -notcontains $zip -or $names -notcontains ($zip + '.sha256')) {
            Stop-Run ('The release ' + $tag + ' does not list ' + $zip + ' and its checksum.')
        }
        return @{ Tag = $tag; Version = $version; ZipName = $zip }
    }

    # A repair fetches the release of the version already installed, so it never changes the version.
    function Get-ReleaseOfVersion([string]$Version) {
        return @{
            Tag = ('v' + $Version)
            Version = $Version
            ZipName = ('Earshot-' + $Version + '-win-x64.zip')
            MissingMessage = 'That release is no longer on GitHub. Choose Update instead.'
        }
    }

    # ---------------------------------------------------------------- downloading and checking

    function Write-DownloadProgress([string]$Name, [long]$Done, [long]$Total, [hashtable]$Seen) {
        if ($Total -le 0) { return }
        $percent = [int][Math]::Floor(100.0 * $Done / $Total)
        if ($st.Redirected) {
            while ($Seen.Next -le 100 -and $Seen.Next -le $percent) {
                Write-Line ('Downloading ' + $Name + ': ' + $Seen.Next + '%')
                $Seen.Next += 25
            }
            return
        }
        Write-Progress -Activity 'Earshot' -Status ($Name + ': ' + (Format-Megabytes $Done) + ' of ' + (Format-Megabytes $Total)) -PercentComplete $percent
    }

    # Downloads one file to Destination with the framework's HttpClient, which sends no header the script chose, streamed
    # in 81920-byte reads. Stops on any failure with one plain line. The final address must be https, or loopback http
    # when a test feed was given.
    function Save-Download([string]$Uri, [string]$Destination, [long]$Cap, [string]$Name, [bool]$Loopback, [string]$MissingMessage = '') {
        Add-Type -AssemblyName System.Net.Http
        $client = New-Object System.Net.Http.HttpClient
        $client.Timeout = [TimeSpan]::FromMinutes(30)
        $response = $null
        $stream = $null
        $file = $null
        try {
            try { $response = $client.GetAsync($Uri, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult() }
            catch { Stop-Run ('Could not reach GitHub for ' + $Name + ': ' + $_.Exception.GetBaseException().Message) }
            $status = [int]$response.StatusCode
            if (-not $response.IsSuccessStatusCode) { Stop-ForStatus $status $Name $MissingMessage }
            $final = $response.RequestMessage.RequestUri
            if ($final.Scheme -ne 'https' -and -not ($Loopback -and $final.Scheme -eq 'http' -and $final.Host -eq '127.0.0.1')) {
                Stop-Run ('The download of ' + $Name + ' was sent to an address that is not https, so nothing was installed.')
            }
            $declared = $response.Content.Headers.ContentLength
            if ($null -ne $declared -and $declared -gt $Cap) { Stop-Run ($Name + ' is bigger than the limit, so nothing was installed.') }
            $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            $file = [IO.File]::Open($Destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            $buffer = New-Object byte[] 81920
            $received = [long]0
            $total = 0
            if ($null -ne $declared) { $total = [long]$declared }
            $seen = @{ Next = 25 }
            while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $received += $read
                if ($received -gt $Cap) { Stop-Run ($Name + ' went past the limit, so nothing was installed.') }
                $file.Write($buffer, 0, $read)
                if ($Cap -gt 4096) { Write-DownloadProgress $Name $received $total $seen }
            }
            $file.Flush($true)
            if ($null -ne $declared -and $received -ne $declared) { Stop-Run ('The download of ' + $Name + ' was cut short, so nothing was installed.') }
        }
        finally {
            if ($file) { $file.Dispose() }
            if ($stream) { $stream.Dispose() }
            if ($response) { $response.Dispose() }
            $client.Dispose()
            if (-not $st.Redirected) { Write-Progress -Activity 'Earshot' -Completed }
        }
        return $received
    }

    function New-WorkFolder($Pth) {
        $name = 'earshot-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
        $folder = Join-Path $Pth.Temp $name
        if (Test-Path -LiteralPath $folder) { Stop-Run ('The folder ' + $folder + ' already exists.') }
        # The installed program asks for a zip named update.zip, written with a drive letter, normalised, and short.
        $zipPath = Join-Path $folder 'update.zip'
        if ($zipPath.Length -gt $MaxZipPathLength -or $zipPath -notmatch '^[A-Za-z]:\\' -or [IO.Path]::GetFullPath($zipPath) -ne $zipPath) {
            Stop-Run 'The temporary folder is not somewhere Earshot can install from. Set TEMP to a short local folder, such as C:\Temp.'
        }
        [void](New-Item -ItemType Directory -Path $folder)
        $st.Folder = $folder
        return $folder
    }

    # Downloads the release's zip and its checksum, compares, and only then unpacks. Returns the paths and the hash.
    function Get-VerifiedCopy($Release, $Addresses, $Pth) {
        $folder = New-WorkFolder $Pth
        $zip = Join-Path $folder 'update.zip'
        $sumFile = Join-Path $folder 'update.zip.sha256'
        $base = $Addresses.Downloads + $Release.Tag + '/'
        Write-Line ('Release ' + $Release.Tag + '.')
        $missing = ''
        if ($Release.MissingMessage) { $missing = [string]$Release.MissingMessage }
        [void](Save-Download ($base + $Release.ZipName + '.sha256') $sumFile $MaxChecksumBytes ($Release.ZipName + '.sha256') $Addresses.Loopback $missing)
        $declared = Read-Checksum ([IO.File]::ReadAllBytes($sumFile)) $Release.ZipName
        if ($null -eq $declared) { Stop-Run 'The release checksum file is not in the form this script reads, so nothing was installed.' }
        [void](Save-Download ($base + $Release.ZipName) $zip $MaxZipBytes $Release.ZipName $Addresses.Loopback $missing)
        Write-Line 'Checking the download...'
        $actual = Get-Sha256 $zip
        if (-not $actual.Equals($declared, [StringComparison]::OrdinalIgnoreCase)) {
            Stop-Run 'The download did not match its checksum, so nothing was installed.'
        }
        Write-Line 'The download matches its checksum.'
        $st.Verified = $true
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $app = Join-Path $folder 'app'
        Write-Line 'Unpacking...'
        [IO.Compression.ZipFile]::ExtractToDirectory($zip, $app)
        $exe = Join-Path (Join-Path $app 'Earshot') 'Earshot.exe'
        if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { Stop-Run 'The download holds no Earshot.exe, so nothing was installed.' }
        return @{ Folder = $folder; Zip = $zip; Sha256 = $actual.ToUpperInvariant(); App = (Join-Path $app 'Earshot'); Exe = $exe }
    }

    # What setup needs, from the verified copy's own read-only probe. It changes nothing and shows no window.
    function Get-SetupValues($Copy) {
        Write-Line 'Checking this PC...'
        $out = Join-Path $Copy.Folder 'setup.json'
        if ($hk.SetupValues) {
            $values = & $hk.SetupValues $Copy.Exe $out
            if ($values -is [string]) { $values = $values | ConvertFrom-Json }
            return [pscustomobject]$values
        }
        $process = Start-Process -FilePath $Copy.Exe -ArgumentList (ConvertTo-ArgumentLine @('probe', 'setup-values', '--out', $out)) -PassThru -WindowStyle Hidden
        $process.WaitForExit()
        $code = $process.ExitCode
        if ($code -ne 0 -or -not (Test-Path -LiteralPath $out)) { Stop-Run ('This PC could not be checked (Earshot answered ' + $code + '), so nothing was changed.') }
        $values = [IO.File]::ReadAllText($out) | ConvertFrom-Json
        if ($values.schema -ne 1) { Stop-Run 'The check of this PC answered in a form this script does not read, so nothing was changed.' }
        return $values
    }

    # ---------------------------------------------------------------- the running tray

    function Find-Tray($Pth) {
        if ($hk.FindTray) { return @(& $hk.FindTray | Where-Object { $_ }) }
        $session = [Diagnostics.Process]::GetCurrentProcess().SessionId
        $wanted = @((Join-Path $Pth.Install 'Earshot.exe'), (Join-Path $Pth.UserPrograms 'Earshot.exe'))
        $found = @()
        foreach ($p in @(Get-Process -Name Earshot -ErrorAction SilentlyContinue)) {
            if ($p.SessionId -ne $session) { continue }
            $path = $p.Path
            if (-not $path) { continue }
            foreach ($w in $wanted) {
                if ($path.Equals($w, [StringComparison]::OrdinalIgnoreCase)) {
                    $found += @{ Id = $p.Id; Path = $path; Version = (Get-ProgramVersion $path) }
                }
            }
        }
        return $found
    }

    function Request-TrayExit($Tray) {
        if ($hk.ExitTray) { [void](& $hk.ExitTray $Tray); return }
        $asker = Start-Process -FilePath $Tray.Path -ArgumentList '--exit' -PassThru -WindowStyle Hidden
        $asker.WaitForExit()
    }

    function Wait-TrayGone($Pth, $Tray) {
        $seconds = $TrayWaitSecondsDefault
        if ($hk.TrayWaitSeconds) { $seconds = [double]$hk.TrayWaitSeconds }
        $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
        while ($true) {
            $still = @(Find-Tray $Pth | Where-Object { $_.Id -eq $Tray.Id })
            if ($still.Count -eq 0) { return $true }
            if ([DateTime]::UtcNow -ge $deadline) { return $false }
            Start-Sleep -Milliseconds 250
        }
    }

    # Closes a running Earshot through its own Exit (when Hand back is on, AirPods in use are let go and blocked) and returns its process id,
    # or this script's own id when none was running. A dry run only says what it would do.
    function Close-Tray($Pth, [bool]$Dry) {
        $trays = @(Find-Tray $Pth)
        if ($trays.Count -eq 0) { return $PID }
        $first = $null
        foreach ($tray in $trays) {
            if ($null -eq $first) { $first = $tray.Id }
            if ($Dry) { Write-Line ('Would close Earshot first (process ' + $tray.Id + ').'); continue }
            $supportsExit = $false
            if ($tray.Version -and ([version]$tray.Version) -ge ([version]'1.3.0')) { $supportsExit = $true }
            if ($supportsExit) {
                Write-Line 'Closing Earshot...'
                Request-TrayExit $tray
            }
            else {
                if (-not (Test-CanAsk)) { Stop-Run 'Earshot is running. Choose Exit in its menu, then run this again.' }
                Write-Line 'Earshot is running. Choose Exit in its menu, then press Enter here.'
                [void](Read-Answer)
            }
            # Noted before the wait: a tray that was asked to exit and is slow may have gone by the time the stop below is
            # reached, and a stop starts every tray that was asked and is no longer running.
            $st.ClosedTrays = @($st.ClosedTrays) + @($tray)
            if (-not (Wait-TrayGone $Pth $tray)) { Stop-Run 'Earshot did not close. Choose Exit in its menu and run this again.' }
        }
        return $first
    }

    function Start-Tray([string]$Exe) {
        if ($hk.StartTray) { [void](& $hk.StartTray $Exe); return }
        [void](Start-Process -FilePath $Exe -WorkingDirectory (Split-Path -Parent $Exe))
    }

    # A run that stops after it closed the tray starts that tray again, so Earshot is not left closed with the AirPods
    # unprotected until the next sign-in. It does not when the run reported success (the run's own ending starts the program),
    # when the elevated program may still be working in the install folder (a program started now could be half replaced), or
    # when the tray's program is no longer there, or when that very tray is still running (one that was asked to exit and
    # never did). Each program is started once.
    function Restart-ClosedTrays {
        if ($st.Succeeded -or $st.NoRestart) { return }
        $running = @()
        if ($null -ne $st.Paths) { $running = @(Find-Tray $st.Paths) }
        $seen = @{}
        foreach ($tray in @($st.ClosedTrays)) {
            $exe = [string]$tray.Path
            if (-not $exe -or $seen.ContainsKey($exe.ToLowerInvariant())) { continue }
            if (@($running | Where-Object { $_.Id -eq $tray.Id }).Count -gt 0) { continue }
            $seen[$exe.ToLowerInvariant()] = $true
            if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { continue }
            try { Start-Tray $exe; Write-Line 'Earshot was started again.' }
            catch { Write-Line ('Earshot could not be started again: ' + $_.Exception.Message) }
        }
    }

    # ---------------------------------------------------------------- the one administrator prompt

    function Invoke-Elevated([string]$Exe, [string]$Line) {
        $result = $null
        if ($hk.Elevate) { $result = & $hk.Elevate $Exe $Line }
        else {
            foreach ($name in @('EARSHOT_SAFE_MODE', 'EARSHOT_DATA_ROOT')) {
                if ([Environment]::GetEnvironmentVariable($name)) {
                    Stop-Run ("Earshot's setup does not run while " + $name + ' is set.')
                }
            }
            $result = Start-Process -FilePath $Exe -ArgumentList $Line -Verb RunAs -PassThru -WorkingDirectory (Join-Path $env:SystemRoot 'System32')
        }
        if ($result -is [Diagnostics.Process]) {
            $null = $result.Handle
            Wait-WithSpinner $result
            return [int]$result.ExitCode
        }
        Write-Line 'Installing...'
        return [int]$result.ExitCode
    }

    function Wait-WithSpinner([Diagnostics.Process]$Process) {
        if ($st.Redirected) {
            Write-Line 'Installing...'
            $Process.WaitForExit()
            return
        }
        $frames = @('|', '/', '-', '\')
        $i = 0
        while (-not $Process.WaitForExit(100)) {
            [Console]::Write("`r" + $frames[$i % 4] + ' Installing...')
            $i++
        }
        [Console]::Write("`r" + (' ' * 20) + "`r")
    }

    # Runs the one elevated command, taking care of a declined prompt. Returns the exit code of the elevated program.
    function Invoke-ElevatedStep([string]$Exe, [string]$Line) {
        Write-Line 'Windows will ask for administrator approval once.'
        try { return (Invoke-Elevated $Exe $Line) }
        catch {
            if (Test-Declined $_.Exception) { Stop-Run 'Administrator approval was declined, so nothing was changed.' }
            throw
        }
    }

    function Get-ExitCodeMeaning([int]$Code) {
        switch ($Code) {
            3 { return 'a step of setup failed' }
            2 { return 'setup finished only in part' }
            10 { return "a folder's permissions were not as Earshot needs" }
            14 { return "the release's file list is missing" }
            15 { return 'the paired AirPods did not match' }
            16 { return 'the environment sets a variable Earshot will not set up with' }
            20 { return 'setup was asked for in a form Earshot did not accept' }
            21 { return 'Windows did not give setup administrator rights' }
            25 { return 'another setup, update or repair is running' }
            27 { return 'setup was still running when the time to wait for it ran out' }
            77 { return 'setup does not run while a test setting is on' }
            default { return 'setup did not finish' }
        }
    }

    # ---------------------------------------------------------------- the update's own record

    function Read-OutcomeFile($Pth) {
        $file = Join-Path $Pth.Machine 'update-outcome.json'
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { return $null }
        # A record being written when it is read looks damaged for a moment; the next read sees it whole. The last problem is
        # kept, so a record that never reads is said so at the end of the wait instead of looking like no record.
        try {
            $record = ([IO.File]::ReadAllText($file) | ConvertFrom-Json)
            $st.OutcomeProblem = ''
            return $record
        }
        catch {
            $st.OutcomeProblem = [string]$_.Exception.Message
            return $null
        }
    }

    # After the elevated update ended: waits for a record different from the one before the prompt, and says how it went.
    function Wait-Outcome($Pth, [string]$BeforeId, [int]$ExitCode) {
        $seconds = $OutcomeWaitSecondsDefault
        if ($hk.OutcomeWaitSeconds) { $seconds = [double]$hk.OutcomeWaitSeconds }
        if ($ExitCode -ne 0) { $seconds = [Math]::Min($seconds, 5) }
        $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
        $frames = @('|', '/', '-', '\')
        $i = 0
        while ($true) {
            $now = Read-OutcomeFile $Pth
            if ($now -and $now.Id -ne $BeforeId -and @('Installed', 'Failed', 'Refused', 'Repaired', 'RepairFailed') -contains [string]$now.Kind) { return $now }
            if ([DateTime]::UtcNow -ge $deadline) { return $null }
            if (-not $st.Redirected) { [Console]::Write("`r" + $frames[$i % 4] + ' Installing...'); $i++ }
            Start-Sleep -Milliseconds 500
        }
    }

    function Complete-Update($Pth, $Before, [int]$ExitCode) {
        $beforeId = ''
        if ($Before) { $beforeId = [string]$Before.Id }
        $outcome = Wait-Outcome $Pth $beforeId $ExitCode
        if (-not $st.Redirected) { [Console]::Write("`r" + (' ' * 20) + "`r") }
        if ($null -eq $outcome) {
            if ($ExitCode -ne 0) { Stop-Run ('The update did not start (' + (Get-ExitCodeMeaning $ExitCode) + ', code ' + $ExitCode + ').') }
            # The update may still be replacing files, so the closed tray is not started over it.
            $st.NoRestart = $true
            $more = ''
            if ($st.OutcomeProblem) { $more = ' The record of the update could not be read: ' + $st.OutcomeProblem }
            Stop-Run ('The update was still running when this script stopped waiting for it. Earshot will say how it went when it starts.' + $more)
        }
        $kind = [string]$outcome.Kind
        if ($kind -eq 'Installed' -or $kind -eq 'Repaired') { $st.Succeeded = $true; return }
        $reason = [string]$outcome.Reason
        if (-not $reason) { $reason = 'the update did not finish' }
        Stop-Run ($reason + '. (' + [string]$outcome.Code + ')')
    }

    # ---------------------------------------------------------------- the per-user copy (no AirPods paired yet)

    function Test-OursFolder([string]$Folder) {
        return (Test-Path -LiteralPath (Join-Path $Folder 'Earshot.files.json') -PathType Leaf)
    }

    function Install-UserCopy($Copy, $Pth, $Values) {
        $target = $Pth.UserPrograms
        if (Test-Path -LiteralPath $target) {
            if (-not (Test-OursFolder $target)) { Stop-Run ('The folder ' + $target + ' is there and is not an Earshot copy, so it was left alone.') }
            Remove-Item -LiteralPath $target -Recurse -Force
        }
        [void](New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force)
        Copy-Item -LiteralPath $Copy.App -Destination $target -Recurse
        $st.Succeeded = $true
        Write-Line ('Earshot is set up for you only, in ' + $target + '.')
        Start-Tray (Join-Path $target 'Earshot.exe')
        Write-Line 'Pair your AirPods with this PC in Bluetooth settings, then run this line again to finish installing. Until then Earshot cannot stop this PC paging them.'
        Write-Line "Already paired under a name without 'AirPods' in it? Choose them in Earshot's menu (Choose device), then run this line again."
    }

    function Remove-UserCopy($Pth) {
        $target = $Pth.UserPrograms
        if (-not (Test-Path -LiteralPath $target) -or -not (Test-OursFolder $target)) { return }
        $running = @(Find-Tray $Pth | Where-Object { Test-Under $_.Path $target })
        if ($running.Count -gt 0) { Write-Line ('The copy in ' + $target + ' is still running, so it was left.'); return }
        Remove-Item -LiteralPath $target -Recurse -Force
    }

    # ---------------------------------------------------------------- the actions

    function Write-WouldRun([string]$Exe, [string]$Line) {
        Write-Line ('Would run as administrator: "' + $Exe + '" ' + $Line)
    }

    function Test-Older([string]$Installed, [string]$Latest) {
        if (-not $Installed) { return $true }
        return ([version]$Installed) -lt ([version]$Latest)
    }

    function Invoke-Install([string]$Act, $Pth, $Addresses, $Release) {
        $copy = Get-VerifiedCopy $Release $Addresses $Pth
        $values = Get-SetupValues $copy
        $usable = ([string]$values.install.state -eq 'usable')
        $route = 'install-zip'
        if ($usable) {
            if ($Act -eq 'Repair') { $route = 'repair' }
            elseif (Test-Older ([string]$values.install.version) $Release.Version) { $route = 'update' }
            elseif ($Act -eq 'Install') { Write-Line ('Earshot ' + $values.install.version + ' is already installed.'); return }
            else { Write-Line ('Earshot ' + $values.install.version + ' is up to date.'); return }
        }

        if (-not [bool]$values.ready) {
            if ($route -ne 'install-zip') {
                if ([string]$values.reason -eq 'several') { Stop-Run "Earshot is installed, but several AirPods are paired. Choose yours in Earshot's menu (Choose device), then run this again." }
                if (@('unreadable', 'incomplete') -contains [string]$values.reason) { Stop-Run 'Earshot is installed, but Windows did not list all of your paired Bluetooth devices, so no AirPods could be found to set it up for. Check that Bluetooth is on, then run this again.' }
                Stop-Run 'Earshot is installed, but no paired AirPods were found to set it up for. Pair them in Bluetooth settings, then run this again.'
            }
            # The per-user copy has no part that runs before sign-in, so it cannot keep the PC from paging AirPods that are
            # already paired. It is only for a PC with none paired yet; with several, or a list that could not be read in
            # full, an install for the machine has no device to be set up for, and a copy that does not protect would look
            # like one that does. "Not paired" is only said of a list that was read without a problem.
            $why = [string]$values.reason
            if ($why -eq 'several') { Stop-Run 'Several AirPods are paired with this PC, so Earshot cannot tell which are yours. Remove the ones you do not use in Bluetooth settings, then run this again.' }
            if ($why -eq 'unreadable') { Stop-Run 'Windows did not list your paired Bluetooth devices. Check that Bluetooth is on, then run this again.' }
            if ($why -eq 'incomplete') { Stop-Run 'Windows listed only some of your paired Bluetooth devices, so Earshot cannot tell whether your AirPods are paired. Check that Bluetooth is on, then run this again.' }
            if ($why -ne 'not-paired') { Stop-Run 'Earshot could not tell which AirPods to set up for, so nothing was installed.' }
            if ($DryRun) { Write-Line ('Would copy Earshot to ' + $Pth.UserPrograms + ' and start it. No administrator approval is needed.'); return }
            [void](Close-Tray $Pth $false)
            Install-UserCopy $copy $Pth $values
            return
        }

        $sid = [string]$values.userSid
        $address = [string]$values.address
        $container = [string]$values.containerId
        $installedExe = Join-Path $Pth.Install 'Earshot.exe'

        if ($route -eq 'install-zip') {
            Write-Line 'Route: first install.'
            $command = @('install-zip', $copy.Zip, $copy.Sha256, $sid, $address, $container)
            $line = ConvertTo-ArgumentLine $command
            if ($DryRun) {
                Write-WouldRun $copy.Exe $line
                [void](Close-Tray $Pth $true)
                Write-Line 'Would start Earshot afterwards.'
                return
            }
            [void](Close-Tray $Pth $false)
            $code = Invoke-ElevatedStep $copy.Exe $line
            if ($code -eq 26) { Stop-Run 'Earshot is already installed. Use Update or Repair.' }
            if ($code -eq 27) {
                # The install it started was left running; a program started now could be half replaced.
                $st.NoRestart = $true
                Stop-Run 'Setup was still running when this script stopped waiting for it. It was left to finish. Run this line again in a few minutes to see where it got to.'
            }
            if ($code -ne 0) { Stop-Run ('Setup did not finish (' + (Get-ExitCodeMeaning $code) + ', code ' + $code + ').') }
            $st.Succeeded = $true
        }
        else {
            Write-Line ('Route: ' + $route + ' through the installed Earshot.')
            $trayPid = $PID
            if (-not $DryRun) { $trayPid = Close-Tray $Pth $false }
            $command = @('update', $copy.Zip, $copy.Sha256, [string]$trayPid, $sid, $address, $container)
            $line = ConvertTo-ArgumentLine $command
            if ($DryRun) {
                Write-WouldRun $installedExe $line
                [void](Close-Tray $Pth $true)
                Write-Line 'Would start Earshot afterwards.'
                return
            }
            $before = Read-OutcomeFile $Pth
            $code = Invoke-ElevatedStep $installedExe $line
            Complete-Update $Pth $before $code
        }

        Remove-UserCopy $Pth
        Write-Line 'Starting Earshot...'
        Start-Tray $installedExe
        Write-Line 'Earshot is installed.'
    }

    function Invoke-Uninstall($Pth, $Addresses) {
        $installedExe = Join-Path $Pth.Install 'Earshot.exe'
        $exe = $null
        if ((Test-Path -LiteralPath $installedExe -PathType Leaf) -or (Test-Path -LiteralPath $Pth.Install) -or (Test-Path -LiteralPath (Join-Path $Pth.Machine 'config.json'))) {
            # The uninstall is run as administrator, so the program it runs must be one only administrators could have put
            # there. The installed program cannot vouch for its own folder; the checked download's can. When that check does
            # not pass, or the program is gone, the latest release's own program removes what is left.
            $copy = Get-VerifiedCopy (Get-LatestRelease $Addresses) $Addresses $Pth
            $exe = $copy.Exe
            if (Test-Path -LiteralPath $installedExe -PathType Leaf) {
                $values = Get-SetupValues $copy
                if ([string]$values.install.state -eq 'usable') { $exe = $installedExe }
                else { Write-Line "The installed copy's folder is not one only administrators can change, so the downloaded copy removes it." }
            }
        }

        if ($null -eq $exe) {
            if (Test-Path -LiteralPath $Pth.UserPrograms) {
                if ($DryRun) { Write-Line ('Would close Earshot and remove ' + $Pth.UserPrograms + '. No administrator approval is needed.'); [void](Close-Tray $Pth $true) }
                else {
                    [void](Close-Tray $Pth $false)
                    Remove-UserCopy $Pth
                }
            }
            else { Write-Line 'Earshot is not installed.' }
            # A dry run changes nothing, whatever is left over: it only says what a real run would remove.
            if ($DryRun) { Write-WouldRemoveLeftovers $Pth; return }
            Remove-Leftovers $Pth
            return
        }

        if ($DryRun) {
            Write-WouldRun $exe 'uninstall'
            [void](Close-Tray $Pth $true)
            Write-WouldRemoveLeftovers $Pth
            return
        }
        [void](Close-Tray $Pth $false)
        $code = Invoke-ElevatedStep $exe 'uninstall'
        if ($code -ne 0) { Stop-Run ('Uninstall did not finish (' + (Get-ExitCodeMeaning $code) + ', code ' + $code + ').') }
        $st.Succeeded = $true
        Write-Line 'Earshot was uninstalled.'
        Remove-UserCopy $Pth
        Remove-Leftovers $Pth
    }

    # The sign-in entry of an Earshot of ours: the Run value that starts exactly the installed program or the per-user copy.
    function Get-OursRunValue($Pth) {
        if (-not (Test-Path -LiteralPath $Pth.RunKey)) { return $null }
        $value = Get-ItemProperty -LiteralPath $Pth.RunKey -Name 'Earshot' -ErrorAction SilentlyContinue
        if (-not $value) { return $null }
        $text = [string]$value.Earshot
        if (Test-OursRunValue $text @((Join-Path $Pth.Install 'Earshot.exe'), (Join-Path $Pth.UserPrograms 'Earshot.exe'))) { return $text }
        return $null
    }

    # What Remove-Leftovers would remove, said and not done.
    function Write-WouldRemoveLeftovers($Pth) {
        if ($null -ne (Get-OursRunValue $Pth)) { Write-Line 'Would remove the Open on startup entry.' }
        if ($st.RemoveSettings) { Write-Line ('Would remove your Earshot settings (' + $Pth.Roaming + ' and ' + $Pth.Local + ').') }
        else { Write-Line 'Would keep your settings.' }
    }

    # After an uninstall: the sign-in entry when it names an Earshot of ours, and the settings when they were asked for.
    function Remove-Leftovers($Pth) {
        if ($null -ne (Get-OursRunValue $Pth)) {
            Remove-ItemProperty -LiteralPath $Pth.RunKey -Name 'Earshot'
            Write-Line 'Open on startup was removed.'
        }
        if ($st.RemoveSettings) {
            foreach ($folder in @($Pth.Roaming, $Pth.Local)) {
                if (Test-Path -LiteralPath $folder) { Remove-Item -LiteralPath $folder -Recurse -Force }
            }
            Write-Line 'Your Earshot settings were removed.'
        }
        else { Write-Line 'Your settings were kept.' }
    }

    function Remove-WorkFolder([bool]$Keep) {
        if (-not $st.Folder) { return }
        if (-not (Test-Path -LiteralPath $st.Folder)) { return }
        if ($Keep) { Write-Line ('The downloaded files are in ' + $st.Folder + '.'); return }
        Remove-Item -LiteralPath $st.Folder -Recurse -Force
    }

    # ---------------------------------------------------------------- the run

    function Invoke-Main {
        if ($env:OS -ne 'Windows_NT') { Stop-Run 'Earshot is for Windows.' }
        if (Test-Elevated) { Stop-Run 'Run this in a normal PowerShell window, not as administrator.' }
        $act = Resolve-Action
        $st.RemoveSettings = [bool]$RemoveSettings
        if ($act -eq 'Uninstall' -and -not $Action) {
            Write-Line 'Remove your Earshot settings too? (y/N)'
            $st.RemoveSettings = ((Read-Answer).Trim() -match '^(?i:y|yes)$')
        }
        if (-not [Environment]::Is64BitOperatingSystem) { Stop-Run 'Earshot needs 64-bit Windows.' }
        Enable-Tls12
        $pth = Get-RootSet
        $st.Paths = $pth
        $addresses = Get-FeedAddresses
        $installedExe = Join-Path $pth.Install 'Earshot.exe'
        $local = Get-ProgramVersion $installedExe

        if ($act -eq 'Uninstall') { Invoke-Uninstall $pth $addresses; return }

        if ($act -eq 'Repair' -and $local) { $release = Get-ReleaseOfVersion $local }
        else { $release = Get-LatestRelease $addresses }

        # An install that reports a version at least the release's needs nothing downloaded. One that reports none, or an
        # unusable one, is decided after the download by the verified copy's own check.
        if ($act -ne 'Repair' -and $local -and -not (Test-Older $local $release.Version)) {
            if ($act -eq 'Install') { Write-Line ('Earshot ' + $local + ' is already installed.') }
            else { Write-Line ('Earshot ' + $local + ' is up to date.') }
            return
        }

        Invoke-Install $act $pth $addresses $release
    }

    $final = 'Earshot: done.'
    $keepFolder = $false
    $reachedTheEnd = $false
    try {
        try {
            Invoke-Main
            $reachedTheEnd = $true
        }
        catch {
            $message = [string]$_.Exception.Message
            if ($message.StartsWith($StopMarker)) { $final = 'Earshot: stopped. ' + $message.Substring($StopMarker.Length) }
            else { $final = 'Earshot: stopped. Something went wrong: ' + $message }
            $keepFolder = $st.Verified
        }
    }
    finally {
        # A stop of any kind starts again the tray this script closed, and so does Ctrl+C, which ends the run without
        # reaching the catch above (a pipeline stop is not caught) but does run this. A run that reached its own end has
        # started whatever it meant to start.
        if (-not $reachedTheEnd) {
            try { Restart-ClosedTrays }
            catch { Write-Line ('Earshot could not be started again: ' + $_.Exception.Message) }
        }
    }
    try { Remove-WorkFolder $keepFolder }
    catch { Write-Line ('The downloaded files in ' + $st.Folder + ' could not be removed: ' + $_.Exception.Message) }
    Write-Line $final
}
