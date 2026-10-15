# Keeps C: from filling up with deploy leftovers (ops alert "Drive C: is almost full", 2026-10-15).
# Run after every prod deploy:
#   powershell -File deploy\prune-deploy-artifacts.ps1            # dry run, lists what would go
#   powershell -File deploy\prune-deploy-artifacts.ps1 -Apply     # really prunes
#
# Rules:
#   - .scratch\published-web: keep the newest $Keep packages AND whatever package the running prod Web (port 5103) uses.
#   - .scratch\*-deploy-*: keep the newest $Keep folders whole. Older ones lose their heavy contents
#     (api-before, api-new, apibuild, api-stage, art, web-new, *.dump, *.log, *.png, node_modules) but keep DEPLOY.md
#     and small scripts, so the deploy history stays readable.
#   - A new deploy's api-before is zipped (api-before.zip) to save space; the folder is removed only after the zip
#     is verified.
#   - DB dumps of the newest $Keep deploys are moved to $DumpDir (F:) instead of staying on C:.
# Never touches src\, the running prod API (src\IPop.Api\bin) or anything outside .scratch.
param(
    [string]$Scratch = "C:\Users\IT12\orca\workspaces\ipop-rescue\.scratch",
    [int]$Keep = 2,
    [string]$DumpDir = "F:\ipop-data\deploy-dumps",
    [switch]$Apply
)
$ErrorActionPreference = "Stop"

# Newest first by the yyyyMMdd date in the name (folder write times change when old folders are pruned).
function Get-NameDate([string]$name) {
    if ($name -match '(20\d{6})') { return $Matches[1] } else { return "00000000" }
}

function Remove-Path([string]$path) {
    if ($Apply) { Remove-Item -LiteralPath $path -Recurse -Force } else { Write-Output "would remove $path" }
}

# Package the running prod Web uses (never removed even if it is older than the newest $Keep).
$webPkg = $null
$listener = Get-NetTCPConnection -LocalPort 5103 -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
if ($listener) {
    $exe = (Get-CimInstance Win32_Process -Filter "ProcessId=$($listener.OwningProcess)").ExecutablePath
    if ($exe) { $webPkg = Split-Path (Split-Path $exe -Parent) -Leaf }
}

$published = Join-Path $Scratch "published-web"
if (Test-Path $published) {
    $pkgs = Get-ChildItem $published -Directory | Sort-Object @{e={Get-NameDate $_.Name}}, LastWriteTime -Descending
    $pkgs | Select-Object -Skip $Keep | Where-Object { $_.Name -ne $webPkg } | ForEach-Object { Remove-Path $_.FullName }
}

$heavyDirs = "api-before", "api-new", "apibuild", "api-stage", "art", "web-new", "node_modules"
$heavyFiles = "*.dump", "*.log", "*.png", "*.jpg", "*.webm"
$deploys = Get-ChildItem $Scratch -Directory -Filter "*-deploy-*" | Sort-Object @{e={Get-NameDate $_.Name}}, CreationTime -Descending

foreach ($d in ($deploys | Select-Object -Skip $Keep)) {
    foreach ($h in $heavyDirs) { Get-ChildItem $d.FullName -Directory -Recurse -Filter $h -ErrorAction SilentlyContinue | ForEach-Object { if (Test-Path $_.FullName) { Remove-Path $_.FullName } } }
    foreach ($f in $heavyFiles) { Get-ChildItem $d.FullName -File -Recurse -Filter $f -ErrorAction SilentlyContinue | ForEach-Object { Remove-Path $_.FullName } }
    Get-ChildItem $d.FullName -File -Filter "api-before.zip" -ErrorAction SilentlyContinue | ForEach-Object { Remove-Path $_.FullName }
}

foreach ($d in ($deploys | Select-Object -First $Keep)) {
    $before = Join-Path $d.FullName "api-before"
    $zip = "$before.zip"
    if (Test-Path $before) {
        if ($Apply) {
            if (-not (Test-Path $zip)) { Compress-Archive -Path "$before\*" -DestinationPath $zip -CompressionLevel Optimal }
            $count = (Get-ChildItem $before -Recurse -File).Count
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            $zipped = [IO.Compression.ZipFile]::OpenRead($zip); $n = @($zipped.Entries | Where-Object { $_.Name -ne '' }).Count; $zipped.Dispose()  # file entries only, not folders
            if ($n -eq $count) { Remove-Item -LiteralPath $before -Recurse -Force } else { Write-Warning "zip of $before has $n of $count files; folder kept" }
        } else { Write-Output "would zip $before" }
    }
    foreach ($dump in Get-ChildItem $d.FullName -File -Filter "*.dump" -ErrorAction SilentlyContinue) {
        $target = Join-Path $DumpDir $d.Name
        if ($Apply) {
            New-Item -ItemType Directory -Force $target | Out-Null
            Move-Item -LiteralPath $dump.FullName -Destination (Join-Path $target $dump.Name)
            Set-Content -Path "$($dump.FullName).moved.txt" -Value (Join-Path $target $dump.Name) -Encoding utf8
        } else { Write-Output "would move $($dump.FullName) -> $target" }
    }
}
