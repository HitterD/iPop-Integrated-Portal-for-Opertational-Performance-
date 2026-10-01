# start-ipop.ps1 - start IPop API + Web for access through https://ipop.santos.co.id
# Nginx Proxy Manager (192.168.2.3) forwards:
#   /            -> https://10.10.6.13:7103   (Web; https so the network IPS can't stall .wasm files)
#   /api, /hubs  -> http://10.10.6.13:5083    (API)
#   /rtc         -> http://10.10.6.13:7880    (LiveKit signalling for group calls / meetings)
# LiveKit runs as the native Windows binary (infra\livekit\bin), not in Docker: Docker Desktop's
# per-port UDP forwarding for the media range is what made Docker hang before.
# Usage:  start-ipop.bat              build, then start all three in their own windows
#         start-ipop.bat -NoBuild     skip the build
#         start-ipop.bat -NoLiveKit   skip LiveKit (1:1 calls still work, group calls do not)
#         start-ipop.bat -Stop        stop all
param(
    [switch]$NoBuild,
    [switch]$Stop,
    [switch]$NoLiveKit,
    [int]$ApiPort = 5083,
    [int]$WebPort = 5103,
    [int]$WebHttpsPort = 7103,
    [string]$PublicUrl = "https://ipop.santos.co.id"
)

$ErrorActionPreference = "Stop"
Set-Location -LiteralPath $PSScriptRoot

function Info($m) { Write-Host "  $m" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "  [ok] $m" -ForegroundColor Green }
function Warn($m) { Write-Host "  [!]  $m" -ForegroundColor Yellow }
function Fail($m) { Write-Host "  [x]  $m" -ForegroundColor Red; exit 1 }

# -- 1. Stop anything this checkout started earlier --------------------------
# Matches only processes whose command line points into this folder.
$repoPattern = "*$([System.Management.Automation.WildcardPattern]::Escape($PSScriptRoot.TrimEnd('\')))*"
function Get-IPopProcesses {
    @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ProcessId -ne $PID -and $_.CommandLine -and $_.CommandLine -like $repoPattern -and
        ($_.Name -eq 'IPop.Api.exe' -or $_.Name -eq 'IPop.WebHost.exe' -or $_.Name -eq 'livekit-server.exe' -or $_.CommandLine -like '*blazor-devserver.dll*' -or
         $_.CommandLine -like '*IPop.Web.dll*' -or $_.CommandLine -like '*dotnet*run*IPop.*.csproj*')
    })
}
$old = Get-IPopProcesses
if ($old.Count -gt 0) {
    Info "Stopping $($old.Count) running IPop process(es)..."
    foreach ($p in $old) { try { Stop-Process -Id $p.ProcessId -Force -ErrorAction Stop } catch {} }
    Start-Sleep -Milliseconds 1200
}
if ($Stop) { Ok "IPop API, Web and LiveKit stopped."; exit 0 }

$ports = @($ApiPort, $WebPort, $WebHttpsPort)
if (-not $NoLiveKit) { $ports += 7880, 7881 }
foreach ($port in $ports) {
    $c = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($c) {
        $name = (Get-Process -Id $c.OwningProcess -ErrorAction SilentlyContinue).ProcessName
        Fail "Port $port is still used by $name (PID $($c.OwningProcess)). Stop it: Stop-Process -Id $($c.OwningProcess) -Force"
    }
}

# -- 2. Database -------------------------------------------------------------
if (-not (Test-NetConnection localhost -Port 5433 -InformationLevel Quiet -WarningAction SilentlyContinue)) {
    Fail "PostgreSQL is not listening on localhost:5433. Start the database first."
}
Ok "PostgreSQL is up on localhost:5433."

# -- 3. Certificate for the NPM -> Web hop -----------------------------------
# NPM does not verify the upstream certificate, so the ASP.NET dev cert is enough.
dotnet dev-certs https --check *> $null
if ($LASTEXITCODE -ne 0) {
    Info "Creating ASP.NET development certificate..."
    dotnet dev-certs https *> $null
    if ($LASTEXITCODE -ne 0) { Fail "Could not create the development certificate." }
}
Ok "HTTPS development certificate present."

# -- 4. Build ----------------------------------------------------------------
if (-not $NoBuild) {
    Info "Building API + Web..."
    dotnet build src/IPop.Api/IPop.Api.csproj --nologo -v q
    if ($LASTEXITCODE -ne 0) { Fail "API build failed." }
    dotnet build src/IPop.WebHost/IPop.WebHost.csproj --nologo -v q
    if ($LASTEXITCODE -ne 0) { Fail "Web build failed." }
    Ok "Build succeeded."
}

# -- 5. Start both in their own console windows ------------------------------
# Development mode: DevSeed runs on start (resets ipopadmin to its dev password).
function Start-Server([string]$title, [string]$project, [string]$urls) {
    $cmd = "title $title && dotnet run --project $project --no-build --launch-profile http --urls `"$urls`""
    Start-Process cmd.exe -ArgumentList "/k $cmd" -WorkingDirectory $PSScriptRoot
}
Start-Server "IPop API" "src/IPop.Api/IPop.Api.csproj" "http://0.0.0.0:$ApiPort"
# The Web is served by IPop.WebHost in Production (security headers, no debug symbols, no
# Development config) rather than the Blazor dev server.
Start-Server "IPop Web" "src/IPop.WebHost/IPop.WebHost.csproj" "http://0.0.0.0:${WebPort};https://0.0.0.0:$WebHttpsPort"

# LiveKit reads its API key/secret from LIVEKIT_KEYS in the git-ignored .env.
$liveKitUp = $false
if (-not $NoLiveKit) {
    $lkExe = Join-Path $PSScriptRoot "infra\livekit\bin\livekit-server.exe"
    $envFile = Join-Path $PSScriptRoot ".env"
    $keys = if (Test-Path $envFile) { (Get-Content $envFile | Where-Object { $_ -match '^LIVEKIT_KEYS=' } | Select-Object -First 1) -replace '^LIVEKIT_KEYS=', '' } else { $null }
    if (-not (Test-Path $lkExe)) { Warn "LiveKit binary missing at $lkExe - group calls disabled." }
    elseif (-not $keys) { Warn "LIVEKIT_KEYS not found in .env - group calls disabled." }
    else {
        $env:LIVEKIT_KEYS = $keys.Trim('"')
        Start-Process cmd.exe -ArgumentList "/k title IPop LiveKit && `"$lkExe`" --config infra\livekit\livekit.yaml" -WorkingDirectory $PSScriptRoot
        Remove-Item Env:LIVEKIT_KEYS
    }
}

# -- 6. Wait until both answer -----------------------------------------------
Info "Waiting for API and Web to respond..."
$deadline = (Get-Date).AddSeconds(120)
$apiUp = $false; $webUp = $false
while ((Get-Date) -lt $deadline -and -not ($apiUp -and $webUp -and ($liveKitUp -or $NoLiveKit))) {
    Start-Sleep -Seconds 2
    if (-not $apiUp) {
        # Any HTTP answer (401 included) means the API is listening.
        try { Invoke-WebRequest -UseBasicParsing "http://127.0.0.1:$ApiPort/api/calls/config" -TimeoutSec 2 | Out-Null; $apiUp = $true }
        catch { if ($_.Exception.Response) { $apiUp = $true } }
    }
    if (-not $webUp) {
        try { Invoke-WebRequest -UseBasicParsing "http://127.0.0.1:$WebPort/" -TimeoutSec 2 | Out-Null; $webUp = $true } catch {}
    }
    if (-not $liveKitUp -and -not $NoLiveKit) {
        try { Invoke-WebRequest -UseBasicParsing "http://127.0.0.1:7880/" -TimeoutSec 2 | Out-Null; $liveKitUp = $true } catch {}
    }
}
if (-not $apiUp) { Warn "API did not answer on port $ApiPort - check the 'IPop API' window." }
if (-not $webUp) { Warn "Web did not answer on port $WebPort - check the 'IPop Web' window." }
if (-not $NoLiveKit -and -not $liveKitUp) { Warn "LiveKit did not answer on port 7880 - check the 'IPop LiveKit' window." }
if ($apiUp -and $webUp) { Ok "API and Web are running." }
if ($liveKitUp) { Ok "LiveKit is running (group calls)." }

Write-Host ""
Write-Host "  Public:   $PublicUrl" -ForegroundColor White
Write-Host "  LAN:      http://10.10.6.13:$WebPort (redirects to $PublicUrl - Windows notifications need https)" -ForegroundColor White
Write-Host "  Upstream: Web http :$WebPort / https :$WebHttpsPort, API http :$ApiPort, LiveKit :7880 (media UDP 50000-50100)" -ForegroundColor DarkGray
Write-Host "  Close the 'IPop API', 'IPop Web' and 'IPop LiveKit' windows, or run start-ipop.bat -Stop, to stop." -ForegroundColor DarkGray
Write-Host ""
