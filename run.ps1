# run.ps1 - one-command dev runner for IPop
# - Checks Docker + postgres health
# - Surfaces the LAN IP so other devices on the network can connect (not just localhost)
# - Builds Api + Web, then runs Api on 5083 and Web devserver on 5103 bound to 0.0.0.0
# Usage:  pwsh -ExecutionPolicy Bypass -File .\run.ps1
#         pwsh -ExecutionPolicy Bypass -File .\run.ps1 -NoBuild
#         pwsh -ExecutionPolicy Bypass -File .\run.ps1 -Https   (adds https 7083/7103 - needed for call microphones on the LAN)
param(
    [switch]$NoBuild,
    [switch]$Https,
    [int]$ApiPort = 5083,
    [int]$WebPort = 5103,
    [int]$ApiHttpsPort = 7083,
    [int]$WebHttpsPort = 7103,
    [switch]$NoPortFallback,
    [switch]$KeepOrphans
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
Set-Location -LiteralPath $PSScriptRoot
$shown = $false
$runLogDir = Join-Path $PSScriptRoot ".scratch\run-logs"
New-Item -ItemType Directory -Force -Path $runLogDir | Out-Null

function Info($m)  { Write-Host "  $m" -ForegroundColor Cyan }
function Ok($m)    { Write-Host "  [ok] $m" -ForegroundColor Green }
function Warn($m)  { Write-Host "  [!]  $m" -ForegroundColor Yellow }
function Fail($m)  { Write-Host "  [x]  $m" -ForegroundColor Red; exit 1 }

# -- 1. Docker ----------------------------------------------------------------
Info "Checking Docker..."
try { docker info 2>&1 | Out-Null } catch { Fail "Docker is not running. Start Docker Desktop and try again." }
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { Fail "docker CLI not found. Install Docker Desktop." }
if (-not (Get-Command docker-compose -ErrorAction SilentlyContinue) -and -not (docker compose version 2>&1 | Out-String).Contains("Docker Compose")) {
    Fail "docker compose not found. Update Docker Desktop."
}
Ok "Docker is running."

# -- 2. Postgres container --------------------------------------------------
Info "Checking postgres (ipop-postgres)..."
$composeFile = Join-Path $PSScriptRoot "docker-compose.yml"
if (-not (Test-Path $composeFile)) { Fail "docker-compose.yml not found at $composeFile" }

# Try `docker compose` (v2) then `docker-compose` (v1) - whichever is available
function Invoke-Compose { param([string[]]$Args)
    try { & docker compose @Args 2>&1 } catch { & docker-compose @Args 2>&1 }
}
$null = Invoke-Compose @("-f", $composeFile, "up", "-d") 2>&1

# Wait for healthcheck
$deadline = (Get-Date).AddSeconds(30)
$healthy = $false
while ((Get-Date) -lt $deadline) {
    $status = (docker inspect --format "{{.State.Health.Status}}" ipop-postgres 2>$null)
    if ($status -eq "healthy") { $healthy = $true; break }
    # Fallback: container exists but has no healthcheck yet (older compose)
    if (-not $status) {
        $running = (docker inspect --format "{{.State.Running}}" ipop-postgres 2>$null)
        if ($running -eq "true") { $healthy = $true; break }
    }
    Start-Sleep -Milliseconds 800
}
if (-not $healthy) { Warn "postgres did not become healthy within 30s - continuing anyway (check docker logs ipop-postgres)." }
else { Ok "postgres is healthy on host port 5433 -> container 5432." }

# Quick connectivity probe
try {
    $probe = docker exec ipop-postgres pg_isready -U ipop -d ipop 2>&1
    if ($probe -match "accepting connections") { Ok "pg_isready: accepting connections." }
    else { Warn "pg_isready: $probe" }
} catch { Warn "pg_isready probe failed: $_" }

# -- 3. Host LAN IP ---------------------------------------------------------
# Prefer the interface that owns the default route and whose address looks like a LAN
# (exclude ZeroTier 172.22.*, WSL 172.27.*, and APIPA). 10.10.6.13 wins over 172.22.*.
$lanIp = $null
try {
    $routes = Get-NetRoute -AddressFamily IPv4 -DestinationPrefix "0.0.0.0/0" -ErrorAction SilentlyContinue |
        Sort-Object { $_.RouteMetric }  # lowest metric = preferred default route
    foreach ($r in $routes) {
        $addr = Get-NetIPAddress -InterfaceIndex $r.InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue |
            Where-Object { $_.IPAddress -notlike "127.*" -and $_.IPAddress -notlike "169.254.*" -and $_.IPAddress -notlike "172.22.*" -and $_.IPAddress -notlike "172.27.*" } |
            Select-Object -First 1
        if ($addr) { $lanIp = $addr.IPAddress; break }
    }
    if (-not $lanIp) {
        $lanIp = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -notlike "127.*" -and $_.IPAddress -notlike "169.254.*" -and $_.IPAddress -notlike "172.22.*" -and $_.IPAddress -notlike "172.27.*" } | Select-Object -First 1).IPAddress
    }
} catch {}
if (-not $lanIp) { $lanIp = "localhost"; Warn "Could not detect LAN IP, falling back to localhost." }
else { Ok "LAN IP: $lanIp  (other devices on the same network use this, not 127.0.0.1)" }

# Prefer the documented ports, but automatically choose free localhost ports when an
# older iPop process is still running. This avoids a misleading cascade of API/Web errors.
function Test-PortAvailable([int]$port) {
    try {
        $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Any, $port)
        $listener.Start()
        $listener.Stop()
        return $true
    } catch { return $false }
}

function Find-FreePort([int]$preferred) {
    for ($candidate = $preferred; $candidate -lt ($preferred + 20); $candidate++) {
        if (Test-PortAvailable $candidate) { return $candidate }
    }
    return 0
}

# Name the process squatting on a port so the user can stop it deliberately rather
# than guessing. A previous run that was killed without its cleanup block leaves
# orphaned IPop.Api / blazor-devserver processes behind.
function Get-PortOwner([int]$port) {
    try {
        $conn = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $conn) { return $null }
        $proc = Get-Process -Id $conn.OwningProcess -ErrorAction SilentlyContinue
        if (-not $proc) { return "PID $($conn.OwningProcess)" }
        return "$($proc.ProcessName) (PID $($proc.Id))"
    } catch { return $null }
}

function Resolve-Port([int]$requested, [string]$label) {
    if (Test-PortAvailable $requested) { return $requested }

    $owner = Get-PortOwner $requested
    $who = if ($owner) { " Held by $owner." } else { "" }
    if ($NoPortFallback) {
        Fail "$label port $requested is already in use.$who Stop it (Stop-Process -Id <pid> -Force) or pass -${label}Port <n>."
    }
    $free = Find-FreePort ($requested + 1)
    if ($free -eq 0) { Fail "$label port $requested is in use and no free port was found in $($requested + 1)..$($requested + 20).$who" }
    Warn "$label port $requested is busy; using $free for this run.$who"
    Warn "  Leftover from an earlier run? Stop it with: Stop-Process -Id <pid> -Force"
    return $free
}

# -- 3b. Reclaim orphans from an earlier run --------------------------------
# When the launcher is killed hard (closing the terminal, Ctrl+C during build) its
# finally block never runs, so IPop.Api.exe and blazor-devserver keep holding both
# the ports AND the bin\Debug DLLs. The next run then fails twice over: the port bind
# throws AddressInUseException and MSBuild reports MSB3027 file-lock errors. Matching
# strictly on this repo's own paths, so nothing outside this checkout is touched.
# Windows reports the drive letter in whatever case the process was launched with
# ("F:\..." vs "f:\..."), so match case-insensitively via -like rather than .Contains().
$repoRoot = $PSScriptRoot.TrimEnd('\')
$repoPattern = "*$([System.Management.Automation.WildcardPattern]::Escape($repoRoot))*"
function Get-IPopOrphans {
    try {
        Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
            Where-Object {
                $_.ProcessId -ne $PID -and
                $_.CommandLine -and
                $_.CommandLine -like $repoPattern -and
                ($_.Name -eq 'IPop.Api.exe' -or $_.CommandLine -like '*blazor-devserver.dll*' -or $_.CommandLine -like '*IPop.Web.dll*')
            }
    } catch { @() }
}

$orphans = @(Get-IPopOrphans)
if ($orphans.Count -gt 0) {
    Warn "Found $($orphans.Count) process(es) still running from an earlier run of this checkout:"
    foreach ($o in $orphans) { Write-Host "       $($o.Name) (PID $($o.ProcessId))" -ForegroundColor DarkGray }
    if ($KeepOrphans) {
        Warn "Leaving them alone (-KeepOrphans). Expect port fallback and possible build file-lock errors."
    } else {
        Info "Stopping them so ports and bin\Debug DLLs are released (pass -KeepOrphans to skip)..."
        foreach ($o in $orphans) {
            try { Stop-Process -Id $o.ProcessId -Force -ErrorAction Stop } catch { Warn "Could not stop PID $($o.ProcessId): $($_.Exception.Message)" }
        }
        Start-Sleep -Milliseconds 900
        $left = @(Get-IPopOrphans)
        if ($left.Count -gt 0) { Warn "$($left.Count) process(es) survived; the build may still hit file locks." }
        else { Ok "Reclaimed ports and build outputs from the previous run." }
    }
}

$requestedApiPort = $ApiPort
$requestedWebPort = $WebPort
$ApiPort = Resolve-Port $ApiPort "Api"
$WebPort = Resolve-Port $WebPort "Web"
if ($Https) {
    $ApiHttpsPort = Resolve-Port $ApiHttpsPort "ApiHttps"
    $WebHttpsPort = Resolve-Port $WebHttpsPort "WebHttps"
}

# -- 3c. Self-signed certificate (-Https) -----------------------------------
# Browsers only expose the microphone on https (or localhost), so LAN calls need TLS.
# One cert for localhost + 127.0.0.1 + the LAN IP, regenerated when the IP changes.
# Lives in .scratch\certs (never committed); Kestrel picks it up via env vars.
if ($Https) {
    $certDir = Join-Path $PSScriptRoot ".scratch\certs"
    New-Item -ItemType Directory -Force -Path $certDir | Out-Null
    Set-Content (Join-Path $certDir ".gitignore") "*" -Encoding ascii
    $pfx = Join-Path $certDir "ipop-dev.pfx"
    $stamp = Join-Path $certDir "ipop-dev.san"
    $certPassword = "ipop-dev-only"
    $san = "localhost,127.0.0.1,$lanIp"
    if (-not (Test-Path $pfx) -or -not (Test-Path $stamp) -or (Get-Content $stamp -Raw).Trim() -ne $san) {
        Info "Creating self-signed certificate for $san ..."
        $ext = "2.5.29.17={text}DNS=localhost&IPAddress=127.0.0.1"
        if ($lanIp -ne "localhost") { $ext += "&IPAddress=$lanIp" }
        $cert = New-SelfSignedCertificate -Subject "CN=iPop dev" -FriendlyName "iPop dev (self-signed)" `
            -TextExtension @($ext, "2.5.29.37={text}1.3.6.1.5.5.7.3.1") `
            -CertStoreLocation "Cert:\CurrentUser\My" -KeyExportPolicy Exportable -NotAfter (Get-Date).AddYears(2)
        Export-PfxCertificate -Cert $cert -FilePath $pfx -Password (ConvertTo-SecureString $certPassword -AsPlainText -Force) | Out-Null
        Export-Certificate -Cert $cert -FilePath (Join-Path $certDir "ipop-dev.cer") | Out-Null
        Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)" -ErrorAction SilentlyContinue
        Set-Content $stamp $san -Encoding ascii
        Ok "Certificate written to $pfx"
    } else { Ok "Reusing certificate $pfx ($san)" }
    $env:ASPNETCORE_Kestrel__Certificates__Default__Path = $pfx
    $env:ASPNETCORE_Kestrel__Certificates__Default__Password = $certPassword
}

# -- 4. Firewall reminder (non-fatal) --------------------------------------
try {
    $fw = Get-NetFirewallRule -DisplayName "IPop Api $ApiPort" -ErrorAction SilentlyContinue
    if (-not $fw) {
        Warn "No firewall allow rule for $ApiPort/$WebPort. If other devices cannot connect, run once as Administrator:"
        Write-Host "       netsh advfirewall firewall add rule name=`"IPop Api $ApiPort`" dir=in action=allow protocol=TCP localport=$ApiPort" -ForegroundColor DarkGray
        Write-Host "       netsh advfirewall firewall add rule name=`"IPop Web $WebPort`" dir=in action=allow protocol=TCP localport=$WebPort" -ForegroundColor DarkGray
        Write-Host "       (or allow via Windows Defender Firewall > Allow an app)" -ForegroundColor DarkGray
    } else { Ok "Firewall rule present for IPop." }
} catch {}

# -- 5. Build ---------------------------------------------------------------
if (-not $NoBuild) {
    Info "Building (Api + Web)..."
    dotnet build --nologo -v q
    if ($LASTEXITCODE -ne 0) { Fail "Build failed." }
    Ok "Build succeeded."
} else { Info "Skipping build (-NoBuild)." }

# -- 6. ApiBase for Web - must point to the LAN IP so other devices reach the API -
# wwwroot/appsettings.json is the Web's ApiBase. Patch it to the LAN IP for this run.
$webSettings = Join-Path $PSScriptRoot "src\IPop.Web\wwwroot\appsettings.json"
$origApiBase = $null
$patched = $false
if (Test-Path $webSettings) {
    try {
        $j = Get-Content $webSettings -Raw | ConvertFrom-Json
        $origApiBase = $j.ApiBase
        $desired = "http://${lanIp}:$ApiPort"
        if ($j.ApiBase -ne $desired) {
            $j.ApiBase = $desired
            $j | ConvertTo-Json | Set-Content $webSettings -Encoding utf8
            $patched = $true
            Info "Patched ApiBase: $origApiBase -> $desired  (reverted on exit)"
        }
        # Pages served over https must call the API over https too (mixed content).
        $j = Get-Content $webSettings -Raw | ConvertFrom-Json
        $desiredHttps = if ($Https) { "https://${lanIp}:$ApiHttpsPort" } else { $null }
        $currentHttps = if ($j.PSObject.Properties.Name -contains "ApiBaseHttps") { $j.ApiBaseHttps } else { $null }
        if ($currentHttps -ne $desiredHttps) {
            if ($j.PSObject.Properties.Name -contains "ApiBaseHttps") { $j.PSObject.Properties.Remove("ApiBaseHttps") }
            if ($desiredHttps) { $j | Add-Member -NotePropertyName ApiBaseHttps -NotePropertyValue $desiredHttps }
            $j | ConvertTo-Json | Set-Content $webSettings -Encoding utf8
            $patched = $true
            if ($desiredHttps) { Info "Patched ApiBaseHttps -> $desiredHttps  (removed on exit)" }
        }
    } catch { Warn "Could not patch $webSettings : $_" }
}

# Also export for child processes that read from environment
$env:ApiBase = "http://${lanIp}:$ApiPort"

# Force Development so DevSeed runs on startup (this resets ipopadmin to Admin123! etc.)
$env:ASPNETCORE_ENVIRONMENT = "Development"

# -- 7. CORS - ensure Api allows the LAN origin for this run --
$apiSettings = Join-Path $PSScriptRoot "src\IPop.Api\appsettings.Development.json"
$origCorsOrigins = $null
$patchedCors = $false
if (Test-Path $apiSettings) {
    try {
        $aj = Get-Content $apiSettings -Raw | ConvertFrom-Json
        $origCorsOrigins = $aj.Cors.WebOrigins
        $wantedOrigins = @("http://localhost:$WebPort", "https://localhost:$WebPort", "http://${lanIp}:$WebPort", "https://${lanIp}:$WebPort", "https://ipop.santos.co.id")
        if ($Https) { $wantedOrigins += @("https://localhost:$WebHttpsPort", "https://127.0.0.1:$WebHttpsPort", "https://${lanIp}:$WebHttpsPort") }
        $needPatch = -not $aj.Cors -or -not $aj.Cors.WebOrigins -or @($wantedOrigins | Where-Object { $aj.Cors.WebOrigins -notcontains $_ }).Count -gt 0
        if ($needPatch -and $aj.Cors -and $aj.Cors.WebOrigins) { $wantedOrigins = @(@($aj.Cors.WebOrigins) + $wantedOrigins | Select-Object -Unique) }
        if ($needPatch) {
            if (-not $aj.Cors) { $aj | Add-Member -NotePropertyName Cors -NotePropertyValue ([pscustomobject]@{ WebOrigins = $wantedOrigins }) }
            else { $aj.Cors.WebOrigins = $wantedOrigins }
            $aj | ConvertTo-Json -Depth 6 | Set-Content $apiSettings -Encoding utf8
            $patchedCors = $true
            Info "Patched Cors WebOrigins to include http://${lanIp}:$WebPort (reverted on exit)"
        }
    } catch { Warn "Could not patch Cors origins: $_" }
}

# -- 8. Run -----------------------------------------------------------------
Info "Starting..."
Write-Host ""
Write-Host "  Api  http://${lanIp}:$ApiPort  (also http://localhost:$ApiPort)" -ForegroundColor White
Write-Host "  Web  http://${lanIp}:$WebPort  (also http://localhost:$WebPort)" -ForegroundColor White
Write-Host "  DB   ${lanIp}:5433 -> postgres:5432  (also localhost:5433)  container: ipop-postgres" -ForegroundColor White
Write-Host ""
Write-Host "  On this machine:  http://localhost:$WebPort" -ForegroundColor DarkGray
Write-Host "  From another device on the same LAN:  http://${lanIp}:$WebPort" -ForegroundColor DarkGray
Write-Host "  If that fails: check Windows Firewall allows $ApiPort and $WebPort, and both devices are on the same subnet." -ForegroundColor DarkGray
if ($Https) {
    Write-Host ""
    Write-Host "  HTTPS (calls/microphone):  https://${lanIp}:$WebHttpsPort   API https://${lanIp}:$ApiHttpsPort" -ForegroundColor White
    Write-Host "  Self-signed: on each device open https://${lanIp}:$ApiHttpsPort/ once and accept the warning," -ForegroundColor DarkGray
    Write-Host "  then open the Web URL. Or trust .scratch\certs\ipop-dev.cer (Trusted Root) to skip warnings." -ForegroundColor DarkGray
}
Write-Host ""
Write-Host "  Press Ctrl+C to stop both." -ForegroundColor Yellow
Write-Host ""

$apiJob = $null
$webJob = $null
try {
    function Start-VisibleProcess([string]$title, [string]$projectPath, [int]$port, [int]$httpsPort) {
        $urls = if ($Https) { "http://0.0.0.0:$port;https://0.0.0.0:$httpsPort" } else { "http://0.0.0.0:$port" }
        $runCmd = "dotnet run --project $projectPath --no-build --urls `"$urls`""
        $hasWt = [bool](Get-Command wt.exe -ErrorAction SilentlyContinue)
        $started = $false
        if ($hasWt) {
            try {
                & wt.exe -w 0 new-tab -d "$PSScriptRoot" --title "$title" cmd.exe /k "title $title && $runCmd"
                if ($? -and $LASTEXITCODE -eq 0) { $started = $true }
            } catch {
                $started = $false
            }
        }
        if (-not $started) {
            Start-Process cmd.exe -ArgumentList "/k title $title && $runCmd" -WorkingDirectory $PSScriptRoot
        }
    }

    Info "Opening visible console tabs for Backend and Frontend..."
    Start-VisibleProcess "IPop API" "src/IPop.Api/IPop.Api.csproj" $ApiPort $ApiHttpsPort
    Start-Sleep -Milliseconds 800

    Start-VisibleProcess "IPop Web" "src/IPop.Web/IPop.Web.csproj" $WebPort $WebHttpsPort
    Start-Sleep -Milliseconds 800

    # Wait until either exits (Ctrl+C kills this script, finally block cleans up)
    $startTime = [DateTime]::UtcNow
    while ($true) {
        Start-Sleep -Milliseconds 1000
        # Light liveness: show once when both are listening
        if (-not $shown) {
            $apiUp = $false; $webUp = $false
            try { $null = Invoke-WebRequest -UseBasicParsing "http://localhost:$ApiPort/health" -TimeoutSec 1; $apiUp = $true } catch {
                try { $null = Invoke-WebRequest -UseBasicParsing "http://localhost:$ApiPort/api/auth/login" -Method Post -ContentType "application/json" -Body '{"nik":"x","password":"x"}' -TimeoutSec 1; $apiUp = $true } catch {
                    try { if ($_.Exception.Response.StatusCode.value__ -ge 400) { $apiUp = $true } } catch {}
                    try { if ($_.ErrorDetails) { $apiUp = $true } } catch {}
                }
            }
            try { $null = Invoke-WebRequest -UseBasicParsing "http://localhost:$WebPort/" -TimeoutSec 1; $webUp = $true } catch {}
            if ($apiUp -and $webUp) {
                Ok "Both Api and Web are responding."
                Ok "Live logs are streaming in 'IPop API' and 'IPop Web' tabs."
                $shown = $true
            } elseif (([DateTime]::UtcNow - $startTime).TotalSeconds -gt 45) {
                if (-not $apiUp) { Warn "Api is taking longer than expected on port $ApiPort (check 'IPop API' tab)." }
                if (-not $webUp) { Warn "Web is taking longer than expected on port $WebPort (check 'IPop Web' tab)." }
                $startTime = [DateTime]::UtcNow
            }
        }
    }
} finally {
    Info "Shutting down..."
    Start-Sleep -Milliseconds 400
    foreach ($o in @(Get-IPopOrphans)) {
        try { Stop-Process -Id $o.ProcessId -Force -ErrorAction SilentlyContinue } catch {}
    }
    if ($patched -and $origApiBase -and (Test-Path $webSettings)) {
        try {
            $j = Get-Content $webSettings -Raw | ConvertFrom-Json
            $j.ApiBase = $origApiBase
            if ($j.PSObject.Properties.Name -contains "ApiBaseHttps") { $j.PSObject.Properties.Remove("ApiBaseHttps") }
            $j | ConvertTo-Json | Set-Content $webSettings -Encoding utf8
            Info "Restored ApiBase to $origApiBase"
        } catch {}
    }
    if ($patchedCors -and $origCorsOrigins -and (Test-Path $apiSettings)) {
        try {
            $aj = Get-Content $apiSettings -Raw | ConvertFrom-Json
            $aj.Cors.WebOrigins = $origCorsOrigins
            $aj | ConvertTo-Json -Depth 6 | Set-Content $apiSettings -Encoding utf8
            Info "Restored Cors WebOrigins"
        } catch {}
    }
    Ok "Done."
}
