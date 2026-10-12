# End-to-end smoke test: starts an isolated Bellboard (own pipe + data dir), drives it through the CLI,
# restarts it to check session restore, and enforces a startup-time budget.
#   .\tests\smoke.ps1 [-BuildDir dist\Bellboard] [-PromptBudgetMs 4000]
param(
    [string]$BuildDir = (Join-Path $PSScriptRoot "..\dist\Bellboard"),
    [int]$PromptBudgetMs = 4000,
    [switch]$KeepData
)
$ErrorActionPreference = "Stop"
$BuildDir = (Resolve-Path $BuildDir).Path
$cli = Join-Path $BuildDir "bell.exe"
$dataDir = Join-Path ([IO.Path]::GetTempPath()) ("bellboard-smoke-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory $dataDir | Out-Null
$env:BELLBOARD_PIPE = "bellboard-smoke-$PID"
$env:BELLBOARD_HOME = $dataDir
$script:appPid = $null
$script:passed = 0

function W {
    $out = & $cli @args 2>&1
    if ($LASTEXITCODE) { throw "bell $($args -join ' ') failed ($LASTEXITCODE): $out" }
    return ($out -join "`n")
}
function Ok([string]$msg) { $script:passed++; Write-Host "  ok  $msg" -ForegroundColor Green }
function Check([bool]$cond, [string]$msg) { if (-not $cond) { throw "FAIL: $msg" }; Ok $msg }
function WaitFor([scriptblock]$cond, [int]$ms, [string]$msg) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $ms) {
        try { if (& $cond) { Ok "$msg ($($sw.ElapsedMilliseconds) ms)"; return } } catch { }
        Start-Sleep -Milliseconds 150
    }
    throw "FAIL (timed out after $ms ms): $msg"
}
function Start-Bellboard([string[]]$extra) {
    $exe = Join-Path $BuildDir "Bellboard.exe"
    if ($extra.Count -gt 0) { Start-Process $exe -ArgumentList $extra | Out-Null } else { Start-Process $exe | Out-Null }
    WaitFor { & $cli ping *> $null; $LASTEXITCODE -eq 0 } 20000 "app answers on \\.\pipe\$env:BELLBOARD_PIPE"
    $script:appPid = (W ping --json | ConvertFrom-Json).pid
}
function Stop-Bellboard {
    # Windows PowerShell turns native stderr into errors under "Stop"; quitting an exited app is fine.
    try { & $cli quit *> $null } catch { }
    if ($script:appPid) {
        $p = Get-Process -Id $script:appPid -ErrorAction SilentlyContinue
        if ($p -and -not $p.WaitForExit(15000)) { Stop-Process -Id $script:appPid -Force }
    }
}
function Workspaces { (W list --json | ConvertFrom-Json).workspaces }

try {
    Write-Host "Bellboard smoke test ($BuildDir)"
    Start-Bellboard @("--fresh")
    WaitFor { (W read) -match "PS .*>" } 20000 "first prompt rendered"
    Check (@(Workspaces).Count -eq 1) "one workspace at start"

    W send --enter "Write-Output ('smoke-' + 6*7)" | Out-Null
    WaitFor { (W read --lines 10) -match "smoke-42" } 8000 "send/read round trip"

    $tmp = (Get-Item ([IO.Path]::GetTempPath())).FullName.TrimEnd('\')
    W send --enter "Set-Location '$tmp'" | Out-Null
    WaitFor { (@(Workspaces)[0].cwd).TrimEnd('\') -eq $tmp } 8000 "cwd follows the shell (OSC 9;9)"

    # Windows PowerShell 5.1 mangles embedded double quotes in native arguments, so pipe via --stdin.
    'Write-Host -NoNewline ("$([char]27)]9;osc-ping$([char]7)")' | & $cli send --stdin --enter
    if ($LASTEXITCODE) { throw "send --stdin failed" }
    WaitFor { @(W notifications --json | ConvertFrom-Json).body -contains "osc-ping" } 8000 "OSC 9 raises a notification"
    W notify --title Smoke "cli-ping" | Out-Null
    Check (@(W notifications --json | ConvertFrom-Json).body -contains "cli-ping") "Bellboard notify raises a notification"

    $split = (W split right --json | ConvertFrom-Json).pane
    WaitFor { (W read --pane $split) -match "PS .*>" } 15000 "split pane gets a prompt"
    W send --pane $split "Write-Output keys-ok" | Out-Null
    W send-key --pane $split enter | Out-Null
    WaitFor { (W read --pane $split --lines 5) -match "keys-ok" } 8000 "send-key enter"

    $tab = (W tab new --pane $split --json | ConvertFrom-Json).pane
    WaitFor { (W read --pane $tab) -match "PS .*>" } 15000 "new tab gets a prompt"
    Check ((@(Workspaces)[0].panes | Where-Object id -eq $tab).tab -eq "2/2") "tab is the second of two in its pane"

    $page = 'data:text/html,<title>smoke page</title><input placeholder=Name><button onclick=document.title=''clicked''>Go</button>'
    W browser open $page | Out-Null
    $snap = W browser snapshot
    Check ($snap -match 'button "Go"') "browser snapshot lists the button"
    $ref = [regex]::Match($snap, 'button "Go" \[ref=(e\d+)\]').Groups[1].Value
    W browser click $ref | Out-Null
    WaitFor { (W browser eval "document.title") -eq "clicked" } 5000 "browser click via DevTools input"
    Check ((W browser eval "6*7") -eq "42") "browser eval"

    $before = Workspaces
    Stop-Bellboard
    Ok "quit saves the session"
    Start-Bellboard ([string[]]@())
    WaitFor { @(Workspaces)[0].panes.Count -eq @($before)[0].panes.Count } 15000 "layout restored ($(@($before)[0].panes.Count) panes)"
    WaitFor { @(@(Workspaces)[0].panes | Where-Object kind -eq "browser").Count -eq 1 } 5000 "browser pane restored"
    Check (@(@(Workspaces)[0].panes | Where-Object { $_.tab -eq "2/2" }).Count -eq 1) "tabs restored"

    # Startup budget (cold start -> first prompt), from the per-pane timelines in bellboard.log.
    $log = Get-Content (Join-Path $dataDir "bellboard.log")
    $first = $log | Where-Object { $_ -match "pane p1 startup: .* prompt (\d+)ms" } | Select-Object -First 1
    if ($first -match "prompt (\d+)ms$") {
        $ms = [int]$Matches[1]
        Check ($ms -le $PromptBudgetMs) "cold start -> prompt ${ms} ms (budget $PromptBudgetMs ms)"
    } else { throw "FAIL: no startup timeline in bellboard.log" }

    Write-Host "PASS: $script:passed checks" -ForegroundColor Green
    exit 0
}
catch {
    Write-Host $_ -ForegroundColor Red
    if (Test-Path (Join-Path $dataDir "bellboard.log")) { Write-Host "--- bellboard.log"; Get-Content (Join-Path $dataDir "bellboard.log") -Tail 30 }
    exit 1
}
finally {
    Stop-Bellboard
    if (-not $KeepData) { Start-Sleep -Milliseconds 500; Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue }
}
