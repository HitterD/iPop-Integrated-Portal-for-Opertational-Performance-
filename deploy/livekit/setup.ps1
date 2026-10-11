#Requires -RunAsAdministrator
param(
    [Parameter(Mandatory)] [string] $NodeIp,
    [Parameter(Mandatory)] [string] $PublicDomain,
    [string] $ApiKey = "ipopkey",
    [string] $SpoolDir = "F:\ipop-data\recordings-spool",
    [switch] $SkipFirewall
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$utf8 = New-Object System.Text.UTF8Encoding $false

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw "Docker is not installed or not on PATH." }

# Reuse an existing secret so re-running doesn't invalidate the API's copy.
$envFile = Join-Path $PSScriptRoot ".env"
$secret = $null
if (Test-Path $envFile) {
    $line = Get-Content $envFile | Where-Object { $_ -match '^LIVEKIT_KEYS=' } | Select-Object -First 1
    if ($line) {
        $keys = ($line -replace '^LIVEKIT_KEYS=\s*', '').Trim().Trim('"').Trim("'")
        if ($keys -match '^([^:]+):\s*(.+)$') {
            $ApiKey = $Matches[1].Trim()
            $secret = $Matches[2].Trim()
        } else { throw "Existing LIVEKIT_KEYS is invalid; refusing to replace its secret." }
    }
}
if (-not $secret) {
    $bytes = New-Object byte[] 32
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $secret = -join ($bytes | ForEach-Object { $_.ToString("x2") })
}
[IO.Directory]::CreateDirectory($SpoolDir) | Out-Null
$spoolMount = $SpoolDir.Replace('\', '/')
[IO.File]::WriteAllText($envFile, "LIVEKIT_KEYS=`"${ApiKey}: $secret`"`nRECORDINGS_SPOOL=$spoolMount`n", $utf8)

$yaml = @"
port: 7880
bind_addresses:
  - ""
rtc:
  tcp_port: 7881
  port_range_start: 50000
  port_range_end: 50050
  node_ip: $NodeIp
turn:
  enabled: true
  udp_port: 3478
redis:
  address: redis:6379
"@
[IO.File]::WriteAllText((Join-Path $PSScriptRoot "livekit.yaml"), $yaml, $utf8)

$egress = [IO.File]::ReadAllText((Join-Path $PSScriptRoot "egress.yaml.template"))
$egress = $egress.Replace('__API_KEY__', $ApiKey).Replace('__API_SECRET__', $secret)
[IO.File]::WriteAllText((Join-Path $PSScriptRoot "egress.yaml"), $egress, $utf8)

if (-not $SkipFirewall) {
    foreach ($r in @(
        @{ Name = "iPop LiveKit TCP"; Proto = "TCP"; Ports = @("7880", "7881") },
        @{ Name = "iPop LiveKit UDP"; Proto = "UDP"; Ports = @("3478", "50000-50050") })) {
        Get-NetFirewallRule -DisplayName $r.Name -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        New-NetFirewallRule -DisplayName $r.Name -Direction Inbound -Protocol $r.Proto -LocalPort $r.Ports -Action Allow | Out-Null
    }
    Write-Host "Firewall rules added." -ForegroundColor Green
}

docker compose up -d
if ($LASTEXITCODE -ne 0) { throw "docker compose up failed." }
Start-Sleep -Seconds 3
try {
    $res = Invoke-WebRequest -Uri "http://localhost:7880" -UseBasicParsing -TimeoutSec 5
    Write-Host "LiveKit is responding on 7880 (HTTP $($res.StatusCode))." -ForegroundColor Green
} catch {
    Write-Warning "LiveKit is not responding yet. Check: docker logs ipop-livekit"
}

Write-Host ""
Write-Host "=== Put these in the API config (appsettings.Local.json or environment variables) ===" -ForegroundColor Cyan
Write-Host "Calls__LiveKit__Url=wss://$PublicDomain"
Write-Host "Calls__LiveKit__ServerUrl=http://127.0.0.1:7880"
Write-Host "MeetingRooms__SpoolDir=$SpoolDir"
Write-Host "Calls__LiveKit__ApiKey=$ApiKey"
Write-Host "Calls__LiveKit__ApiSecret=$secret"
Write-Host ""
Write-Host "In NPM: Custom Location /rtc -> http://${NodeIp}:7880, with Websockets Support enabled." -ForegroundColor Cyan
Write-Host "Network: users must be able to reach $NodeIp on TCP 7881, UDP 3478, and UDP 50000-50050 (not through NPM)." -ForegroundColor Yellow
