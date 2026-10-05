# The whole thing, end to end:   pwsh tests\run.ps1
#
# Builds Draft Keeper and the fake editor into a temp folder, lets a copy watch the fake
# editor run its script, then checks what that copy kept.
# The copy gets its own data folder (DRAFTKEEPER_DATA). Your own Draft Keeper keeps running
# and your drafts aren't touched. It does see any editor you have open, but the checks
# only look at the fake one.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$work = Join-Path ([IO.Path]::GetTempPath()) ("draftkeeper-test-" + [guid]::NewGuid().ToString("n"))
$app = Join-Path $work "app"
$hostDir = Join-Path $work "host"
$data = Join-Path $work "data"
$store = Join-Path $data "drafts.dat"
$log = Join-Path $work "decisions.log"

# --disable-build-servers, or back-to-back builds can hang on a leftover build server.
# Some machines also leave a finished build stuck on exit, unkillable. So go by what it
# printed, not the exit code. Five minutes without a result counts as failed.
function Build($project, $out) {
    New-Item -ItemType Directory -Force $work | Out-Null
    $buildLog = Join-Path $work ("build-" + [IO.Path]::GetFileNameWithoutExtension($project) + ".log")
    # Start-Process just glues the arguments together. Paths with spaces need quotes.
    $args_ = @("build", ('"{0}"' -f (Join-Path $root $project)), "-c", "Release",
               "-o", ('"{0}"' -f $out), "-v", "quiet", "-nologo", "--disable-build-servers")
    $p = Start-Process dotnet -ArgumentList $args_ -NoNewWindow -PassThru -RedirectStandardOutput $buildLog
    $deadline = (Get-Date).AddMinutes(5)
    $result = ""
    while ((Get-Date) -lt $deadline -and $result -notmatch 'Build succeeded|Build FAILED') {
        if ($p.HasExited) { Start-Sleep -Milliseconds 300 }
        # [string], or an empty file reads back as a null that compares like an empty list.
        $result = [string](Get-Content $buildLog -Raw -ErrorAction SilentlyContinue)
        if ($p.HasExited -and $result -notmatch 'Build succeeded|Build FAILED') { break }
        Start-Sleep -Milliseconds 500
    }
    if ($result -notmatch 'Build succeeded') {
        Write-Output $result
        throw "build failed or gave no result within five minutes: $project"
    }
    if (-not (Get-ChildItem $out -Filter *.dll -ErrorAction SilentlyContinue)) {
        throw "build reported success but left nothing in $out"
    }
}

Add-Type -AssemblyName System.Security
function Write-Store($rows) {
    $json = ConvertTo-Json @($rows) -Depth 3
    $bytes = [Security.Cryptography.ProtectedData]::Protect(
        [Text.Encoding]::UTF8.GetBytes($json), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    [IO.File]::WriteAllBytes($store, $bytes)
}

$bad = $false
function Check($label, $cond) {
    Write-Output ("{0}  {1}" -f $(if ($cond) { 'PASS' } else { 'FAIL' }), $label)
    if (-not $cond) { $script:bad = $true }
}

$watcher = $null
$test = $null
try {
    Write-Output "building"
    Build "src\DraftKeeper\DraftKeeper.csproj" $app
    Build "tests\TestHost\TestHost.csproj" $hostDir

    # Copies like older versions left behind. Same draft saved every time the chat opened,
    # plus a shorter start of it. Loading should fold those into one. The same text in
    # another chat is its own draft and stays.
    New-Item -ItemType Directory -Force $data | Out-Null
    $nu = "Nu draft that was saved again every time its chat was opened, and once as a shorter start."
    # The fake editor shows the first one in Pi's conversation, like it got sent.
    $piSent = "Pi message that was sent before this program could confirm it."
    $piKept = "Pi draft that was never sent and has to stay."
    $now = [DateTimeOffset]::Now
    $oldest = $now.AddHours(-3)
    Write-Store @(
        [ordered]@{ Id = "seed1"; Owner = "seed"; Session = "Session Nu"; Text = $nu; SavedAt = $oldest.ToString("o"); Live = $false }
        [ordered]@{ Id = "seed2"; Owner = "seed"; Session = "Session Nu"; Text = $nu; SavedAt = $now.AddHours(-1).ToString("o"); Live = $false }
        [ordered]@{ Id = "seed3"; Owner = "seed"; Session = "Session Nu"; Text = $nu.Substring(0, 30); SavedAt = $now.AddHours(-2).ToString("o"); Live = $false }
        [ordered]@{ Id = "seed4"; Owner = "seed"; Session = "Session Xi"; Text = $nu; SavedAt = $now.AddHours(-1).ToString("o"); Live = $false }
        [ordered]@{ Id = "seed5"; Owner = "seed"; Session = "Session Pi"; Text = $piSent; SavedAt = $now.AddHours(-1).ToString("o"); Live = $false }
        [ordered]@{ Id = "seed6"; Owner = "seed"; Session = "Session Pi"; Text = $piKept; SavedAt = $now.AddHours(-1).ToString("o"); Live = $false }
    )

    $env:DRAFTKEEPER_EXTRA_PROCESSES = "DraftKeeperTestHost"
    $env:DRAFTKEEPER_DATA = $data
    $env:DRAFTKEEPER_LOG = $log

    Write-Output "running the test host (about three minutes)"
    $watcher = Start-Process (Join-Path $app "DraftKeeper.exe") -PassThru
    Start-Sleep -Seconds 3
    $test = Start-Process (Join-Path $hostDir "DraftKeeperTestHost.exe") -PassThru
    if (-not $test.WaitForExit(600000)) { throw "the test host did not finish" }
    Start-Sleep -Seconds 6
    Stop-Process -Id $watcher.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2

    $bytes = [Security.Cryptography.ProtectedData]::Unprotect(
        [IO.File]::ReadAllBytes($store), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    $items = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json

    $session = { param($name) @($items | Where-Object { $_.Session -eq $name }) }
    $alpha  = & $session 'Session Alpha'
    $beta   = & $session 'Session Beta'
    $gamma  = & $session 'Session Gamma'
    $delta  = & $session 'Session Delta'
    $eps    = & $session 'Session Epsilon'
    $kappa  = & $session 'Session Kappa'
    $lambda = & $session 'Session Lambda'
    $piRows = & $session 'Session Pi'
    $nuRows = & $session 'Session Nu'
    $xiRows = & $session 'Session Xi'
    $like   = { param($set, $pattern) [bool]($set | Where-Object { $_.Text -like $pattern }) }
    $decisions = if (Test-Path $log) { Get-Content $log } else { @() }

    Write-Output ""
    Write-Output "one box, labelled from the window title"
    Check "Alpha, Beta and Gamma all recorded"        ($alpha.Count -gt 0 -and $beta.Count -gt 0 -and $gamma.Count -gt 0)
    Check "Gamma survived the input box being rebuilt" ($gamma.Count -eq 1 -and $gamma[0].Text -like 'Gamma unsent*')
    Check "Delta captured after the rebuild"           ($delta.Count -eq 1 -and $delta[0].Text -like 'Delta unsent*')
    Check "Alpha capped at three finished drafts"      ($alpha.Count -eq 3)
    Check "the two oldest Alpha drafts trimmed"        (-not (& $like $alpha 'Alpha message 1.*') -and -not (& $like $alpha 'Alpha message 2.*'))
    Check "Alpha unsent draft kept as Alpha"           (& $like $alpha 'Alpha unsent*')
    Check "Beta draft labelled Beta"                   ($beta.Count -eq 1 -and $beta[0].Text -like 'Beta unsent*')
    Check "no Alpha text under Beta"                   (-not (& $like $beta 'Alpha*'))
    Check "no Beta text under Gamma"                   (-not (& $like $gamma 'Beta*'))
    Check "line breaks preserved"                      ($gamma.Count -eq 1 -and ([regex]::Matches($gamma[0].Text, "`n")).Count -ge 3)
    Check "placeholder never saved"                    (-not (& $like $items '*Queue another message*'))
    Check "a message typed a character at a time is one draft" ((@($alpha | Where-Object { $_.Text -like 'Alpha message 5.*' })).Count -le 1)
    Check "an edit in the middle did not fork the draft" ((@($alpha | Where-Object { $_.Text -like 'Alpha unsent*' })).Count -eq 1)
    Check "the edited text is what was kept"           (& $like $alpha '*EDITED MIDDLE*')
    Check "text already in an opened tab is labelled with that tab" ($eps.Count -eq 1 -and $eps[0].Text -like 'Epsilon draft*')
    Check "keyboard hint never saved"                  (-not (& $like $items '*focus or unfocus*'))
    # Theta not being there proves nothing by itself. It has to have been seen, then
    # confirmed sent.
    Check "the sent message was captured first"        ([bool]($decisions | Where-Object { $_ -like "*-> 'Session Theta'*" }))
    Check "the send was confirmed"                     ([bool]($decisions | Where-Object { $_ -like '*confirmed sent, dropped draft*' }))
    Check "a sent message is dropped"                  (-not (& $like $items 'Theta message*'))
    Check "a message cleared without sending is kept"  (& $like $items 'Iota message*')

    Write-Output ""
    Write-Output "chats in editor tabs, each its own document"
    Check "Kappa kept once, with what was typed after reopening it" ($kappa.Count -eq 1 -and $kappa[0].Text -like 'Kappa draft*Also read the logs from last night.')
    Check "Lambda kept once, after leaving it for a chat that shows the same text" ($lambda.Count -eq 1 -and $lambda[0].Text -like 'Lambda draft*')
    Check "near-identical text in two chats stayed apart" (-not (& $like $kappa 'Lambda*') -and -not (& $like $lambda 'Kappa*'))
    Check "a sent message with a blank line was captured first" ([bool]($decisions | Where-Object { $_ -like "*-> 'Session Omicron'*" }))
    Check "a sent message with a blank line is dropped" (-not (& $like $items 'Omicron message*'))
    Check "opening a chat drops a saved draft its conversation shows" (-not ($piRows | Where-Object { $_.Text -eq $piSent }))
    Check "opening a chat keeps a saved draft its conversation does not show" ([bool]($piRows | Where-Object { $_.Text -eq $piKept }))

    Write-Output ""
    Write-Output "copies saved by the old program"
    Check "copies in one chat folded into one row"     ($nuRows.Count -eq 1 -and $nuRows[0].Text -eq $nu)
    Check "the folded row kept the oldest time"        ($nuRows.Count -eq 1 -and [Math]::Abs((([DateTimeOffset]$nuRows[0].SavedAt) - $oldest).TotalSeconds) -lt 1)
    Check "the same text in another chat stayed"       ($xiRows.Count -eq 1)

    if ($bad) {
        Write-Output ""
        Write-Output "what the test sessions kept:"
        $items | Where-Object { $_.Session -like 'Session *' } | Sort-Object { [DateTimeOffset]$_.SavedAt } | ForEach-Object {
            $t = ($_.Text -replace "`r?`n", ' / ')
            if ($t.Length -gt 70) { $t = $t.Substring(0, 70) + '...' }
            Write-Output ("  {0:HH:mm:ss.fff}  {1,-16} live={2,-5} {3}" -f ([DateTimeOffset]$_.SavedAt).LocalDateTime, $_.Session, $_.Live, $t)
        }
        Write-Output ""
        Write-Output "what the watcher decided:"
        $decisions | ForEach-Object { "  $_" }
    }
}
finally {
    Remove-Item Env:\DRAFTKEEPER_EXTRA_PROCESSES, Env:\DRAFTKEEPER_DATA, Env:\DRAFTKEEPER_LOG -ErrorAction SilentlyContinue
    foreach ($p in @($watcher, $test)) {
        if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
    }
    # This holds whatever the test copy saw in your open editors. It goes, even if a file
    # is still locked for a second.
    for ($i = 0; $i -lt 10 -and (Test-Path $work); $i++) {
        Start-Sleep -Seconds 1
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path $work) { Write-Output "could not remove $work" }
}

Write-Output ""
if ($bad) { Write-Output "FAILURES ABOVE"; exit 1 }
Write-Output "all checks passed"
