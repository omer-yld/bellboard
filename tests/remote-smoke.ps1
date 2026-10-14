# End-to-end test of Bellboard Remote: starts an isolated Bellboard with remote access on, pairs a "phone" over
# HTTP, mirrors a terminal over the WebSocket, and checks the security rules (origin, host, levels, revocation).
#   .\tests\remote-smoke.ps1 [-BuildDir dist\Bellboard] [-Port 17990]
param(
    [string]$BuildDir = (Join-Path $PSScriptRoot "..\dist\Bellboard"),
    [int]$Port = 17990,
    [switch]$KeepData
)
$ErrorActionPreference = "Stop"
$BuildDir = (Resolve-Path $BuildDir).Path
$cli = Join-Path $BuildDir "bell.exe"
$dataDir = Join-Path ([IO.Path]::GetTempPath()) ("bellboard-remote-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory $dataDir | Out-Null
$env:BELLBOARD_PIPE = "bellboard-remote-smoke-$PID"
$env:BELLBOARD_HOME = $dataDir
$base = "http://127.0.0.1:$Port"
$script:appPid = $null
$script:passed = 0
Add-Type -AssemblyName System.Net.Http

# Remote access on, reached on localhost ("my own tunnel" mode), before the first start.
@{ remote = @{ enabled = $true; mode = "local"; port = $Port; push = $true } } | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $dataDir "config.json")

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

# HTTP without PowerShell's own cookie and redirect handling, so every header is ours.
$handler = New-Object System.Net.Http.HttpClientHandler
$handler.UseCookies = $false
$http = New-Object System.Net.Http.HttpClient($handler)
function Req([string]$method, [string]$path, $body = $null, [hashtable]$headers = @{}) {
    $msg = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::new($method), "$base$path")
    foreach ($k in $headers.Keys) { [void]$msg.Headers.TryAddWithoutValidation($k, $headers[$k]) }
    if ($null -ne $body) { $msg.Content = New-Object System.Net.Http.StringContent(($body | ConvertTo-Json -Depth 10 -Compress), [Text.Encoding]::UTF8, "application/json") }
    $res = $http.SendAsync($msg).GetAwaiter().GetResult()
    $text = $res.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $json = $null
    try { $json = $text | ConvertFrom-Json } catch { }
    [pscustomobject]@{ Status = [int]$res.StatusCode; Json = $json; Text = $text; Headers = $res.Headers }
}
$origin = @{ Origin = $base }

# A minimal WebSocket client: send JSON, wait for a message that matches.
function Connect-Live([string]$cookie, [string]$originValue = $base) {
    $ws = New-Object System.Net.WebSockets.ClientWebSocket
    if ($cookie) { $ws.Options.SetRequestHeader("Cookie", $cookie) }
    if ($originValue) { $ws.Options.SetRequestHeader("Origin", $originValue) }
    # Windows PowerShell returns a VoidTaskResult from GetResult(); keep it out of the function's output.
    [void]$ws.ConnectAsync([Uri]"ws://127.0.0.1:$Port/api/live", [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $script:inbox = New-Object System.Collections.Generic.List[object]
    return $ws
}
function Send-Live($ws, $obj) {
    $bytes = [Text.Encoding]::UTF8.GetBytes(($obj | ConvertTo-Json -Depth 10 -Compress))
    [void]$ws.SendAsync([ArraySegment[byte]]::new($bytes), [System.Net.WebSockets.WebSocketMessageType]::Text, $true, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
}
function Receive-Live($ws, [int]$ms) {
    $buf = New-Object byte[] 65536
    $ms2 = New-Object IO.MemoryStream
    $cts = New-Object Threading.CancellationTokenSource($ms)
    try {
        do {
            $r = $ws.ReceiveAsync([ArraySegment[byte]]::new($buf), $cts.Token).GetAwaiter().GetResult()
            if ($r.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) { return [pscustomobject]@{ t = "closed" } }
            $ms2.Write($buf, 0, $r.Count)
        } while (-not $r.EndOfMessage)
    } catch { return $null }
    return ([Text.Encoding]::UTF8.GetString($ms2.ToArray()) | ConvertFrom-Json)
}
function WaitLive($ws, [scriptblock]$match, [int]$ms, [string]$msg) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $ms) {
        $m = Receive-Live $ws ([Math]::Max(100, $ms - $sw.ElapsedMilliseconds))
        if ($null -eq $m) { continue }
        $script:inbox.Add($m)
        if (& $match $m) { Ok "$msg ($($sw.ElapsedMilliseconds) ms)"; return $m }
        if ($m.t -eq "closed") { break }
    }
    $seen = ($script:inbox | Select-Object -Last 8 | ForEach-Object { $_.t }) -join ", "
    throw "FAIL (timed out after $ms ms): $msg (last messages: $seen; socket $($ws.State))"
}

try {
    Write-Host "Bellboard Remote smoke test ($BuildDir, port $Port)"
    Start-Process (Join-Path $BuildDir "Bellboard.exe") -ArgumentList "--fresh" | Out-Null
    WaitFor { & $cli ping *> $null; $LASTEXITCODE -eq 0 } 20000 "app answers on the pipe"
    $script:appPid = (W ping --json | ConvertFrom-Json).pid
    WaitFor { (W read) -match "PS .*>" } 20000 "first prompt rendered"

    WaitFor { (W remote status --json | ConvertFrom-Json).running } 10000 "bell remote status: the sidecar runs"
    WaitFor { (Req GET "/api/health").Json.ok } 15000 "GET /api/health answers"
    Check (-not (Req GET "/api/health").Json.paired) "not paired without a cookie"
    $index = Req GET "/"
    Check ($index.Status -eq 200 -and $index.Text -match "<html") "the phone app is served at /"
    Check (($index.Headers.GetValues("Content-Security-Policy") -join "") -match "default-src 'self'") "strict Content-Security-Policy"
    Check ((Req GET "/vendor/xterm.js").Status -eq 200) "xterm.js is served from the exe"
    Check ((Req GET "/api/state").Status -eq 401) "/api/state refuses an unpaired device"

    # Host and origin rules.
    Check ((Req GET "/api/health" -headers @{ Host = "evil.example" }).Status -eq 421) "unknown Host refused (DNS rebinding)"
    Check ((Req POST "/api/pair" @{ code = "x"; name = "x" }).Status -eq 403) "POST without Origin refused"
    Check ((Req POST "/api/pair" @{ code = "x"; name = "x" } @{ Origin = "https://evil.example" }).Status -eq 403) "POST from another site refused"
    Check ((Req POST "/api/pair" @{ code = "x"; name = "x" } @{ Origin = "http://127.0.0.1:3000" }).Status -eq 403) "POST from another port of this host refused"

    # Pairing: wrong code, then the right one.
    $pair = W remote pair --json | ConvertFrom-Json
    Check ($pair.code -match "^[A-Z2-9]{4}-[A-Z2-9]{4}$") "bell remote pair makes a code ($($pair.code))"
    Check ($pair.qr.Length -gt 100) "bell remote pair draws a QR code"
    $bad = Req POST "/api/pair" @{ code = "AAAA-AAAA"; name = "Wrong" } $origin
    Check ($bad.Status -eq 400) "a wrong code is refused"
    $ok = Req POST "/api/pair" @{ code = $pair.code.ToLower().Replace("-", " "); name = "Smoke phone" } $origin
    Check ($ok.Status -eq 200 -and $ok.Json.device.level -eq "respond") "pairing works (level respond, code typed loosely)"
    $setCookie = ($ok.Headers.GetValues("Set-Cookie") -join ";")
    Check ($setCookie -match "httponly" -and $setCookie -match "samesite=strict" -and $setCookie -match "path=/api") "cookie is HttpOnly, SameSite=Strict, Path=/api"
    $cookie = ($setCookie -split ";")[0]
    $device = $ok.Json.device.id
    Check ((Req POST "/api/pair" @{ code = $pair.code; name = "Again" } $origin).Status -eq 400) "a code works only once"
    Check ((W remote devices --json | ConvertFrom-Json).Count -eq 1) "bell remote devices lists the phone"
    Check (-not ((Get-Content (Join-Path $dataDir "remote.dat") -Raw -Encoding Byte) -join "" -match "Smoke")) "remote.dat is encrypted"

    $state = Req GET "/api/state" -headers @{ Cookie = $cookie }
    Check ($state.Status -eq 200 -and @($state.Json.workspaces).Count -eq 1 -and $state.Json.device.name -eq "Smoke phone") "GET /api/state with the cookie"
    $pane = @($state.Json.workspaces)[0].panes[0].id

    # Levels: a respond device can't type into a pane without an agent.
    Check ((Req POST "/api/call" @{ method = "agent.inbox"; params = @{} } @{ Cookie = $cookie; Origin = "http://localhost:3000" }).Status -eq 403) "the cookie is useless from a dev server on another port"
    $send = Req POST "/api/call" @{ method = "terminal.send"; params = @{ pane = $pane; text = "echo nope" } } (@{ Cookie = $cookie } + $origin)
    Check ($send.Status -eq 400 -and $send.Json.error -match "full access") "respond can't type into a plain shell"
    $quit = Req POST "/api/call" @{ method = "app.quit"; params = @{} } (@{ Cookie = $cookie } + $origin)
    Check ($quit.Status -eq 403) "app.quit is never remote"
    $read = Req POST "/api/call" @{ method = "terminal.read"; params = @{ pane = $pane; lines = 5 } } (@{ Cookie = $cookie } + $origin)
    Check ($read.Status -eq 200 -and $read.Json.text -match "PS") "terminal.read over /api/call"
    $prompt = Req POST "/api/call" @{ method = "agent.prompt"; params = @{ pane = $pane } } (@{ Cookie = $cookie } + $origin)
    Check ($prompt.Status -eq 200 -and $prompt.Json.hash.Length -eq 16) "agent.prompt returns the screen and its hash"

    # Live channel: state, then mirror a terminal.
    Check ($null -eq (& { try { Connect-Live $cookie "https://evil.example" } catch { $null } })) "WebSocket from another site refused"
    $ws = Connect-Live $cookie
    Send-Live $ws @{ t = "hello"; v = 1 }
    WaitLive $ws { param($m) $m.t -eq "state" -and $m.device.id -eq $device } 5000 "live: state after hello" | Out-Null
    Send-Live $ws @{ t = "attach"; pane = $pane; scrollback = 200 }
    $snap = WaitLive $ws { param($m) $m.t -eq "snapshot" -and $m.pane -eq $pane } 8000 "live: terminal snapshot"
    Check ($snap.data -match "PS" -and $snap.cols -gt 20) "snapshot has the screen ($($snap.cols)x$($snap.rows))"
    W send --pane $pane --enter "Write-Output ('remote-' + 6*7)" | Out-Null
    WaitLive $ws { param($m) $m.t -eq "out" -and $m.d -match "remote-42" } 8000 "live: output streams after the snapshot" | Out-Null
    $outs = @($script:inbox | Where-Object { $_.t -eq "out" -and $_.pane -eq $pane })
    Check (($outs | ForEach-Object { $_.seq }) -join "," -eq ((1..$outs.Count) -join ",")) "output seq numbers are contiguous from 1"
    Send-Live $ws @{ t = "call"; id = 7; method = "notification.dnd"; params = @{ on = $true } }
    WaitLive $ws { param($m) $m.t -eq "result" -and $m.id -eq 7 -and $m.ok } 5000 "live: call (respond level)" | Out-Null
    W dnd off | Out-Null

    # Full level: may type anywhere.
    W remote level $device full | Out-Null
    WaitLive $ws { param($m) $m.t -eq "revoked" -or $m.t -eq "closed" } 5000 "changing the level reconnects the device" | Out-Null
    $send2 = Req POST "/api/call" @{ method = "terminal.send"; params = @{ pane = $pane; text = "Write-Output ('full-' + 6*7)`r" } } (@{ Cookie = $cookie } + $origin)
    Check ($send2.Status -eq 200) "full can type into a plain shell"
    WaitFor { (W read --pane $pane --lines 10) -match "full-42" } 8000 "the shell got it"
    Check ((Get-Content (Join-Path $dataDir "remote.log") -Raw) -match "Smoke phone") "remote.log records remote actions"

    # Revocation.
    $ws2 = Connect-Live $cookie
    Send-Live $ws2 @{ t = "hello"; v = 1 }
    WaitLive $ws2 { param($m) $m.t -eq "state" } 5000 "reconnected" | Out-Null
    W remote revoke $device | Out-Null
    WaitLive $ws2 { param($m) $m.t -eq "revoked" -or $m.t -eq "closed" } 5000 "revoking closes the device's socket" | Out-Null
    WaitFor { (Req GET "/api/state" -headers @{ Cookie = $cookie }).Status -eq 401 } 35000 "a revoked device gets 401"

    # Brute force: five wrong codes lock pairing.
    W remote pair --json | Out-Null
    1..5 | ForEach-Object { Req POST "/api/pair" @{ code = "BBBB-BBB$_"; name = "x" } $origin | Out-Null }
    $locked = Req POST "/api/pair" @{ code = "CCCC-CCCC"; name = "x" } $origin
    Check ($locked.Status -eq 429) "repeated wrong codes lock pairing"

    # The sidecar comes back if it dies.
    $sidecar = Get-Process bellboard-remote | Where-Object { $_.Path -like "$BuildDir*" } | Select-Object -First 1
    Stop-Process -Id $sidecar.Id -Force
    WaitFor { (Req GET "/api/health").Json.ok } 20000 "the sidecar restarts after a crash"

    W remote disable | Out-Null
    WaitFor { -not (Get-Process bellboard-remote -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$BuildDir*" }) } 5000 "bell remote disable stops the sidecar"
    Check (-not ((Get-Content (Join-Path $dataDir "bellboard.log") -Raw) -match [regex]::Escape(($cookie -split "=")[1]))) "the token never appears in the log"

    Write-Host "PASS: $script:passed checks" -ForegroundColor Green
    exit 0
}
catch {
    Write-Host $_ -ForegroundColor Red
    Write-Host $_.ScriptStackTrace
    if (Test-Path (Join-Path $dataDir "bellboard.log")) { Write-Host "--- bellboard.log"; Get-Content (Join-Path $dataDir "bellboard.log") -Tail 40 }
    exit 1
}
finally {
    try { & $cli quit *> $null } catch { }
    if ($script:appPid) {
        $p = Get-Process -Id $script:appPid -ErrorAction SilentlyContinue
        if ($p -and -not $p.WaitForExit(15000)) { Stop-Process -Id $script:appPid -Force }
    }
    if (-not $KeepData) { Start-Sleep -Milliseconds 500; Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue }
}
