# Nightly backup of the iPop Postgres database to the Synology (run by the "iPop DB backup" scheduled task).
#
#   1. pg_dump (custom format) inside the ipop-postgres container
#   2. copy to \\192.168.2.16\sja\SJA SPJ\ICT\backup\ipop-db as ipop-YYYYMMDD-HHmm.dump
#   3. verify the copy: same SHA-256 as the dump, and pg_restore can list it
#   4. keep 14 daily + 8 weekly (Sunday) backups; "-manual" dumps are never pruned
#   5. nothing stays on C:; the result is written to backup-status.json next to the dumps,
#      which the API's BackupMonitorWorker reads to alert admins when backups stop.
#
# Uses the UNC path (a scheduled task does not see mapped drives like Z:).
param(
    [string]$Target = '\\192.168.2.16\sja\SJA SPJ\ICT\backup\ipop-db',
    [string]$Container = 'ipop-postgres',
    [string]$Database = 'ipop',
    [int]$KeepDaily = 14,
    [int]$KeepWeekly = 8
)
$ErrorActionPreference = 'Stop'
$stamp = Get-Date -Format 'yyyyMMdd-HHmm'
$name = "ipop-$stamp.dump"
$tmp = Join-Path $env:TEMP $name
$status = @{ startedAt = (Get-Date).ToUniversalTime().ToString('o'); file = $name; ok = $false; error = $null; bytes = 0 }

function Write-Status {
    $status.finishedAt = (Get-Date).ToUniversalTime().ToString('o')
    try { $status | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Target 'backup-status.json') -Encoding UTF8 } catch { }
}

try {
    if (-not (Test-Path -LiteralPath $Target)) { New-Item -ItemType Directory -Force -Path $Target | Out-Null }

    # 1. dump inside the container, then copy it out
    $inside = "/tmp/$name"
    & docker exec $Container pg_dump -U ipop -d $Database -Fc -f $inside
    if ($LASTEXITCODE -ne 0) { throw "pg_dump failed ($LASTEXITCODE)" }
    # 3a. the dump must be readable before it is trusted
    $tables = (& docker exec $Container pg_restore -l $inside | Select-String 'TABLE DATA').Count
    if ($LASTEXITCODE -ne 0 -or $tables -lt 50) { throw "pg_restore could not read the dump ($tables tables)" }
    $status.tables = $tables
    & docker cp "${Container}:$inside" $tmp
    if ($LASTEXITCODE -ne 0) { throw "docker cp failed ($LASTEXITCODE)" }
    & docker exec $Container rm -f $inside | Out-Null

    # 2 + 3b. copy to the NAS and compare hashes
    $dest = Join-Path $Target $name
    Copy-Item -LiteralPath $tmp -Destination $dest -Force
    if ((Get-FileHash -LiteralPath $tmp).Hash -ne (Get-FileHash -LiteralPath $dest).Hash) { throw 'copy on the NAS does not match the dump' }
    $status.bytes = (Get-Item -LiteralPath $dest).Length

    # 4. retention: newest $KeepDaily, plus the newest Sunday backup of each of the last $KeepWeekly weeks
    $auto = Get-ChildItem -LiteralPath $Target -Filter 'ipop-*.dump' |
        Where-Object { $_.Name -match '^ipop-(\d{8})-(\d{4})\.dump$' } |
        ForEach-Object { $_ | Add-Member -NotePropertyName Taken -NotePropertyValue ([datetime]::ParseExact($Matches[1] + $Matches[2], 'yyyyMMddHHmm', $null)) -PassThru } |
        Sort-Object Taken -Descending
    $keep = @{}
    $auto | Select-Object -First $KeepDaily | ForEach-Object { $keep[$_.Name] = $true }
    $auto | Where-Object { $_.Taken.DayOfWeek -eq 'Sunday' } |
        Group-Object { $_.Taken.Date.ToString('yyyy-MM-dd') } | Select-Object -First $KeepWeekly |
        ForEach-Object { $keep[$_.Group[0].Name] = $true }
    $auto | Where-Object { -not $keep[$_.Name] } | Remove-Item -Force

    $status.ok = $true
}
catch {
    $status.error = $_.Exception.Message
}
finally {
    if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force }
    Write-Status
}
if (-not $status.ok) { exit 1 }
