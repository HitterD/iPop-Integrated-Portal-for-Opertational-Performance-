#Requires -RunAsAdministrator
param(
    [Parameter(Mandatory)] [string] $NodeIp,
    [Parameter(Mandatory)] [string] $PublicDomain,
    [string] $ApiKey = "ipopkey",
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
    $m = Select-String -Path $envFile -Pattern '^LIVEKIT_KEYS=\s*([^:]+):\s*(.+)$'
    if ($m) { $ApiKey = $m.Matches[0].Groups[1].Value.Trim(); $secret = $m.Matches[0].Groups[2].Value.Trim() }
}
if (-not $secret) {
    $bytes = New-Object byte[] 32
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $secret = -join ($bytes | ForEach-Object { $_.ToString("x2") })
}
[IO.File]::WriteAllText($envFile, "LIVEKIT_KEYS=${ApiKey}: $secret`n", $utf8)

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
"@
[IO.File]::WriteAllText((Join-Path $PSScriptRoot "livekit.yaml"), $yaml, $utf8)

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
Write-Host "Calls__LiveKit__ApiKey=$ApiKey"
Write-Host "Calls__LiveKit__ApiSecret=$secret"
Write-Host ""
Write-Host "In NPM: Custom Location /rtc -> http://${NodeIp}:7880, with Websockets Support enabled." -ForegroundColor Cyan
Write-Host "Network: users must be able to reach $NodeIp on TCP 7881, UDP 3478, and UDP 50000-50050 (not through NPM)." -ForegroundColor Yellow
