# Publishes an iPop Notifier release in two steps (grilled 2026-10-26):
#   .\publish-notifier.ps1 -Version 1.3.0 -Notes "- Update prompt`n- Signs in from the browser"   # pilot (ICT/Admin) first
#   .\publish-notifier.ps1 -Release                                                                # then everyone
#   .\publish-notifier.ps1 -Version 1.3.1 -Notes "..." -Mandatory -All                             # straight to all, installs at once
# Folder = what the Web host serves at /download/notifier/ and the API reads (Notifier:ReleasesPath):
#   releases.win.json        everyone (Notifier 1.2.x reads this one directly)
#   releases.win.pilot.json  everyone's releases + the pilot release (Notifier 1.3+ gets it through the API)
#   release.json             { all, pilot } versions + mandatory; the API signals running apps when it changes
# The build goes into a staging copy first; the live folder only changes once vpk succeeded.
param(
  [string]$Version,
  [string]$Notes = '',
  [switch]$Mandatory,
  # Skip the pilot step.
  [switch]$All,
  # Promote the current pilot release to everyone.
  [switch]$Release,
  [string]$Releases = 'C:\ipop-ops\notifier\releases'
)
$ErrorActionPreference = 'Stop'
$feedAll = Join-Path $Releases 'releases.win.json'
$feedPilot = Join-Path $Releases 'releases.win.pilot.json'
$manifestPath = Join-Path $Releases 'release.json'

function Read-Manifest {
  if (Test-Path $manifestPath) { return Get-Content $manifestPath -Raw | ConvertFrom-Json }
  $newest = (Get-Content $feedAll -Raw | ConvertFrom-Json).Assets | Sort-Object { [version]$_.Version } -Descending | Select-Object -First 1
  return [pscustomobject]@{ all = [pscustomobject]@{ version = $newest.Version; mandatory = $false; publishedAt = $null }; pilot = $null }
}
function Write-Utf8NoBom([string]$Path, [string]$Text) {
  $tmp = "$Path.tmp"
  [System.IO.File]::WriteAllText($tmp, $Text, (New-Object System.Text.UTF8Encoding($false)))
  Move-Item -Force $tmp $Path
}
function Write-Manifest($m) { Write-Utf8NoBom $manifestPath ($m | ConvertTo-Json -Depth 4) }

if ($Release) {
  $m = Read-Manifest
  if (-not $m.pilot) { throw 'No pilot release to promote.' }
  Copy-Item -Force $feedAll "$feedAll.bak"
  Copy-Item -Force $feedPilot $feedAll
  # The installer and Velopack's side files of the pilot build (kept in pilot\, which the Web host never serves).
  $held = Join-Path $Releases 'pilot'
  foreach ($f in 'iPopNotifier-win-Setup.exe', 'RELEASES', 'assets.win.json') {
    if (Test-Path (Join-Path $held $f)) { Copy-Item -Force (Join-Path $held $f) (Join-Path $Releases $f) }
  }
  $m.all = [pscustomobject]@{ version = $m.pilot.version; mandatory = $m.pilot.mandatory; publishedAt = (Get-Date).ToUniversalTime().ToString('o') }
  $m.pilot = $null
  Write-Manifest $m
  "Released $($m.all.version) to everyone. Running apps hear it within ~30 s (and every 6 h)."
  return
}

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must look like 1.3.0' }
if (-not (Test-Path $feedAll)) { throw "No feed at $feedAll" }
$m = Read-Manifest
if ([version]$Version -le [version]$m.all.version) { throw "Version $Version is not newer than the released $($m.all.version)" }

# Build into a staging copy of the live folder (keeps old packages there, so vpk makes a delta).
$stage = Join-Path $env:TEMP "ipop-notifier-stage-$Version"
if (Test-Path $stage) { Remove-Item -Recurse -Force -Confirm:$false $stage }
New-Item -ItemType Directory -Force $stage | Out-Null
Get-ChildItem $Releases -File | Where-Object { $_.Name -ne 'releases.win.pilot.json' -and $_.Name -ne 'release.json' } | Copy-Item -Destination $stage
$notesFile = Join-Path $stage 'notes.md'
Write-Utf8NoBom $notesFile ($Notes -replace '`n', "`n")

$projDir = $PSScriptRoot
$publishDir = Join-Path $env:TEMP "ipop-notifier-publish-$Version"
if (Test-Path $publishDir) { Remove-Item -Recurse -Force -Confirm:$false $publishDir }
dotnet publish (Join-Path $projDir 'IPop.Notifier.csproj') -c Release -r win-x64 --self-contained -p:Version=$Version -p:Trial=false -o $publishDir -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
$vpk = Join-Path $env:USERPROFILE '.dotnet\tools\vpk.exe'
& $vpk pack --packId iPopNotifier --packVersion $Version --packDir $publishDir --mainExe iPopNotifier.exe `
  --packTitle 'iPop Notifier' --packAuthors 'PT Santos Jaya Abadi' --icon (Join-Path $publishDir 'Assets\ipop.ico') `
  --shortcuts StartMenuRoot --releaseNotes $notesFile --outputDir $stage
if ($LASTEXITCODE -ne 0) { throw 'vpk pack failed' }
Remove-Item -Recurse -Force -Confirm:$false $publishDir

# Packages and installer into the live folder; the new feed becomes the pilot feed (or everyone's with -All).
$newFeed = Join-Path $stage 'releases.win.json'
Get-ChildItem $stage -File | Where-Object { $_.Name -like "iPopNotifier-$Version-*.nupkg" } | Copy-Item -Destination $Releases -Force
$stamp = (Get-Date).ToUniversalTime().ToString('o')
$entry = [pscustomobject]@{ version = $Version; mandatory = [bool]$Mandatory; publishedAt = $stamp }
if ($All) {
  Copy-Item -Force $feedAll "$feedAll.bak"
  Copy-Item -Force $newFeed $feedAll
  Copy-Item -Force (Join-Path $stage 'iPopNotifier-win-Setup.exe') (Join-Path $Releases 'iPopNotifier-win-Setup.exe')
  foreach ($f in 'RELEASES', 'assets.win.json') { Copy-Item -Force (Join-Path $stage $f) (Join-Path $Releases $f) }
  $m.all = $entry
  $m.pilot = $null
} else {
  # The installer stays on the released version until -Release; the pilot gets it as an update.
  Copy-Item -Force $newFeed $feedPilot
  $held = Join-Path $Releases 'pilot'
  New-Item -ItemType Directory -Force $held | Out-Null
  foreach ($f in 'iPopNotifier-win-Setup.exe', 'RELEASES', 'assets.win.json') { Copy-Item -Force (Join-Path $stage $f) (Join-Path $held $f) }
  $m.pilot = $entry
}
Write-Manifest $m
Remove-Item -Recurse -Force -Confirm:$false $stage
if ($All) { "Published $Version to everyone" + $(if ($Mandatory) { ' (mandatory: installs at once)' } else { '' }) + '.' }
else { "Published $Version to the pilot (ICT/Admin). Release to everyone with: .\publish-notifier.ps1 -Release" }
