# Publish a pilot, then promote it: .\publish-notifier.ps1 -Notes 'Changes'; .\publish-notifier.ps1 -Release
# Default Version is the project's canonical 1.4.0. An explicit -Version overrides BOTH dotnet and vpk.
# -All bypasses the pilot. -Trial requires an explicit isolated -Releases folder, uses iPopNotifierQA,
# and may initialise an empty QA feed. Never point Trial at the production folder.
# Bootstrap: 1.2.2 only reads the public feed. Manually distribute the held 1.3 installer to pilot PCs;
# role-targeted pilot updates only work once those PCs run 1.3+. Publishing a pilot does NOT upgrade 1.2.2.
# Files are replaced atomically one at a time; release.json is the final commit signal. Not a folder transaction.
param(
  [string]$Version,
  [string]$Notes = '',
  [switch]$Mandatory,
  [switch]$All,
  [switch]$Release,
  [string]$Releases = 'C:\ipop-ops\notifier\releases',
  [switch]$Trial,
  # Tool injection supports isolated regression tests. Normal publishing uses the installed tools.
  [string]$Dotnet = 'dotnet',
  [string]$Vpk = (Join-Path $env:USERPROFILE '.dotnet\tools\vpk.exe')
)
$ErrorActionPreference = 'Stop'
$production = [IO.Path]::GetFullPath('C:\ipop-ops\notifier\releases').TrimEnd('\', '/')
$Releases = [IO.Path]::GetFullPath($Releases).TrimEnd('\', '/')
if ($Trial -and (-not $PSBoundParameters.ContainsKey('Releases') -or $Releases -ieq $production -or $Releases.StartsWith($production + '\', [StringComparison]::OrdinalIgnoreCase))) {
  throw 'Trial requires an explicit isolated -Releases folder outside production.'
}
if ($Release -and ($All -or $Mandatory -or $PSBoundParameters.ContainsKey('Version') -or $Notes)) {
  throw 'Release cannot combine with Version, Notes, Mandatory or All; it promotes the existing pilot unchanged.'
}
$packId = if ($Trial) { 'iPopNotifierQA' } else { 'iPopNotifier' }
$packTitle = if ($Trial) { 'iPop Notifier QA' } else { 'iPop Notifier' }
$installer = "$packId-win-Setup.exe"
$sideFiles = @($installer, 'RELEASES', 'assets.win.json')
$feedAll = Join-Path $Releases 'releases.win.json'
$feedPilot = Join-Path $Releases 'releases.win.pilot.json'
$manifestPath = Join-Path $Releases 'release.json'
$stage = $null
$publishDir = $null
$lock = $null

function Parse-Version([string]$Value) {
  # Stable three-part SemVer only: matches the API/Velopack release contract. No leading zeros/prereleases.
  if ($Value -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$' -or $Value.Length -gt 20) { throw "Invalid Version '$Value'; use stable SemVer such as 1.4.0." }
  return [version]$Value
}
function Require-File([string]$Path) {
  if (-not (Test-Path -LiteralPath $Path -PathType Leaf) -or (Get-Item -LiteralPath $Path).Length -eq 0) { throw "Missing or empty artifact: $Path" }
}
function Read-Feed([string]$Path) {
  Require-File $Path
  $feed = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
  if (-not $feed.Assets -or @($feed.Assets).Count -eq 0) { throw "No assets in feed $Path" }
  foreach ($asset in $feed.Assets) {
    $null = Parse-Version $asset.Version
    if ($asset.PackageId -ne $packId) { throw "Feed package identity must be $packId (check -Trial): $Path" }
    if (-not $asset.FileName -or [IO.Path]::GetFileName($asset.FileName) -ne $asset.FileName -or $asset.FileName -match '[/\\:]') { throw "Unsafe feed filename in $Path" }
  }
  return $feed
}
function Assert-FeedPackages($Feed, [string]$Folder, [string]$Target) {
  if (-not @($Feed.Assets | Where-Object { $_.Version -eq $Target -and $_.Type -eq 'Full' }).Count) { throw "No full package for $Target" }
  foreach ($asset in $Feed.Assets) { Require-File (Join-Path $Folder $asset.FileName) }
}
function Read-Manifest {
  if (Test-Path -LiteralPath $manifestPath) {
    $m = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if (-not $m.all) { throw 'Release manifest must have an all entry.' }
    return $m
  }
  $newest = $null
  if (Test-Path -LiteralPath $feedAll) { $newest = (Read-Feed $feedAll).Assets | Sort-Object { Parse-Version $_.Version } -Descending | Select-Object -First 1 }
  return [pscustomobject]@{ all = [pscustomobject]@{ version = $(if ($newest) { $newest.Version } else { '0.0.0' }); mandatory = $false; publishedAt = $null }; pilot = $null }
}
function Write-Utf8([string]$Path, [string]$Text) {
  [IO.File]::WriteAllText($Path, $Text, (New-Object Text.UTF8Encoding($false)))
}
function Install-Atomic([string]$Source, [string]$Destination) {
  # Same-directory staging + File.Replace on Windows/NTFS: readers see the old or new complete file.
  $temp = "$Destination.$([guid]::NewGuid().ToString('N')).tmp"
  try {
    [IO.File]::Copy($Source, $temp, $false)
    if ([IO.File]::Exists($Destination)) { [IO.File]::Replace($temp, $Destination, [System.Management.Automation.Language.NullString]::Value) }
    else { [IO.File]::Move($temp, $Destination) }
  } finally { if ([IO.File]::Exists($temp)) { [IO.File]::Delete($temp) } }
}
function File-Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function Validate-Receipt([string]$Held, [string]$ExpectedVersion) {
  $receiptPath = Join-Path $Held 'receipt.json'
  Require-File $receiptPath
  $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
  if ($receipt.version -ne $ExpectedVersion -or $receipt.packageId -ne $packId) { throw 'Pilot receipt version/package identity mismatch.' }
  foreach ($name in @($sideFiles) + @('releases.win.json')) {
    $path = Join-Path $Held $name
    Require-File $path
    $recorded = $receipt.files.PSObject.Properties[$name]
    if (-not $recorded -or (File-Hash $path) -ne $recorded.Value) { throw "Pilot artifact hash changed: $name" }
  }
  if ((File-Hash $feedPilot) -ne (File-Hash (Join-Path $Held 'releases.win.json'))) { throw 'Pilot feed changed since it was held.' }
  $feed = Read-Feed (Join-Path $Held 'releases.win.json')
  Assert-FeedPackages $feed $Releases $ExpectedVersion
  foreach ($asset in $feed.Assets) {
    $recorded = $receipt.files.PSObject.Properties[$asset.FileName]
    if (-not $recorded -or (File-Hash (Join-Path $Releases $asset.FileName)) -ne $recorded.Value) { throw "Pilot package hash changed: $($asset.FileName)" }
  }
}
try {
  if (-not (Test-Path -LiteralPath $Releases -PathType Container)) {
    if (-not $Trial -or $Release) { throw "No feed folder at $Releases" }
    New-Item -ItemType Directory -Path $Releases | Out-Null
  }
  # Serialise publishers. Keep the empty lock file after closing to avoid an unlink/reopen race.
  try { $lock = [IO.File]::Open((Join-Path $Releases '.publish.lock'), 'OpenOrCreate', 'ReadWrite', 'None') }
  catch { throw "Another publisher holds the release lock (or folder is not writable): $Releases" }
  if (-not (Test-Path -LiteralPath $feedAll) -and (-not $Trial -or $Release)) { throw "No feed at $feedAll" }
  if (-not (Test-Path -LiteralPath $feedAll) -and -not $All) { throw 'An empty Trial folder must be initialised with -All (or release.ps1 -Trial).' }
  $m = Read-Manifest
  $null = Parse-Version $m.all.version
  if ($m.pilot) { $null = Parse-Version $m.pilot.version }

  if ($Release) {
    if (-not $m.pilot) { throw 'No pilot release to promote.' }
    $publicNewest = @($m.all.version) + @((Read-Feed $feedAll).Assets | ForEach-Object { $_.Version })
    $publicNewest = $publicNewest | ForEach-Object { Parse-Version $_ } | Sort-Object -Descending | Select-Object -First 1
    if ((Parse-Version $m.pilot.version) -le $publicNewest) { throw 'Pilot is not newer than the public release/feed.' }
    $held = Join-Path $Releases "pilot\$($m.pilot.version)"
    # Validate EVERYTHING before the public feed/installer changes. Legacy unreceipted pilots fail closed.
    Validate-Receipt $held $m.pilot.version
    foreach ($name in $sideFiles) { Install-Atomic (Join-Path $held $name) (Join-Path $Releases $name) }
    Install-Atomic $feedAll "$feedAll.bak"
    Install-Atomic (Join-Path $held 'releases.win.json') $feedAll
    $m.all = [pscustomobject]@{ version = $m.pilot.version; mandatory = $m.pilot.mandatory; publishedAt = (Get-Date).ToUniversalTime().ToString('o') }
    $m.pilot = $null
  } else {
    if (-not $PSBoundParameters.ContainsKey('Version')) {
      [xml]$project = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'IPop.Notifier.csproj') -Raw
      $versionNodes = $project.SelectNodes('/Project/PropertyGroup/Version')
      if ($versionNodes.Count -ne 1) { throw 'Expected one canonical Version in IPop.Notifier.csproj.' }
      $Version = $versionNodes[0].InnerText.Trim()
    }
    $target = Parse-Version $Version
    # Include stale feeds, held builds and orphaned packages from interrupted publishes: never reuse bytes/version.
    $versions = @($m.all.version)
    if ($m.pilot) { $versions += $m.pilot.version }
    foreach ($path in @($feedAll, $feedPilot)) {
      if (Test-Path -LiteralPath $path) { $versions += @((Read-Feed $path).Assets | ForEach-Object { $_.Version }) }
    }
    foreach ($file in Get-ChildItem -LiteralPath $Releases -File -Filter '*.nupkg') {
      if ($file.Name -match ('^' + [regex]::Escape($packId) + '-(.+)-(full|delta)\.nupkg$')) { $versions += $Matches[1] }
      else { throw "Unexpected package identity or name: $($file.Name)" }
    }
    $pilotFolder = Join-Path $Releases 'pilot'
    if (Test-Path -LiteralPath $pilotFolder) { $versions += @(Get-ChildItem -LiteralPath $pilotFolder -Directory | ForEach-Object { $_.Name }) }
    $newest = $versions | ForEach-Object { Parse-Version $_ } | Sort-Object -Descending | Select-Object -First 1
    if ($target -le $newest) { throw "Version $Version is not newer than already published/held $newest. Use a new version; do not reuse packages." }
    $id = [guid]::NewGuid().ToString('N')
    $stage = Join-Path ([IO.Path]::GetTempPath()) "ipop-notifier-stage-$id"
    $publishDir = Join-Path ([IO.Path]::GetTempPath()) "ipop-notifier-publish-$id"
    New-Item -ItemType Directory -Path $stage | Out-Null
    # Stage the latest pilot feed when present so subsequent pilot deltas/history are not lost.
    Get-ChildItem -LiteralPath $Releases -File | Where-Object { $_.Name -like '*.nupkg' } | Copy-Item -Destination $stage
    $baseFeed = if ($m.pilot) { $feedPilot } else { $feedAll }
    if (Test-Path -LiteralPath $baseFeed) { Copy-Item -LiteralPath $baseFeed -Destination (Join-Path $stage 'releases.win.json') }
    $notesFile = Join-Path $stage 'notes.md'
    Write-Utf8 $notesFile ($Notes -replace '`n', "`n")
    $trialProp = if ($Trial) { '-p:Trial=true' } else { '-p:Trial=false' }
    $global:LASTEXITCODE = 0
    & $Dotnet publish (Join-Path $PSScriptRoot 'IPop.Notifier.csproj') -c Release -r win-x64 --self-contained "-p:Version=$Version" $trialProp -o $publishDir -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
    & $Vpk pack --packId $packId --packVersion $Version --packDir $publishDir --mainExe iPopNotifier.exe `
      --packTitle $packTitle --packAuthors 'PT Santos Jaya Abadi' --icon (Join-Path $publishDir 'Assets\ipop.ico') `
      --shortcuts StartMenuRoot --releaseNotes $notesFile --outputDir $stage
    if ($LASTEXITCODE -ne 0) { throw 'vpk pack failed' }
    $newFeed = Join-Path $stage 'releases.win.json'
    $feed = Read-Feed $newFeed
    Assert-FeedPackages $feed $stage $Version
    foreach ($asset in $feed.Assets) { if ((Parse-Version $asset.Version) -gt $target) { throw 'Packaged feed is newer than requested version.' } }
    foreach ($name in $sideFiles) { Require-File (Join-Path $stage $name) }
    $entry = [pscustomobject]@{ version = $Version; mandatory = [bool]$Mandatory; publishedAt = (Get-Date).ToUniversalTime().ToString('o') }
    # Prepare the complete receipt/held installer BEFORE exposing the new pilot feed.
    if (-not $All) {
      $held = Join-Path $Releases "pilot\$Version"
      New-Item -ItemType Directory -Path $held -Force | Out-Null
      $hashes = [ordered]@{}
      foreach ($name in @($sideFiles) + @('releases.win.json') + @($feed.Assets | ForEach-Object { $_.FileName })) { $hashes[$name] = File-Hash (Join-Path $stage $name) }
      foreach ($name in @($sideFiles) + @('releases.win.json')) { Install-Atomic (Join-Path $stage $name) (Join-Path $held $name) }
      Write-Utf8 (Join-Path $held 'receipt.json') ([pscustomobject]@{ version = $Version; packageId = $packId; files = $hashes } | ConvertTo-Json -Depth 6)
    }
    foreach ($asset in $feed.Assets) {
      $destination = Join-Path $Releases $asset.FileName
      if (Test-Path -LiteralPath $destination) {
        if ((File-Hash $destination) -ne (File-Hash (Join-Path $stage $asset.FileName))) { throw "Refusing to overwrite published package: $($asset.FileName)" }
      } else { Install-Atomic (Join-Path $stage $asset.FileName) $destination }
    }
    if ($All) {
      foreach ($name in $sideFiles) { Install-Atomic (Join-Path $stage $name) (Join-Path $Releases $name) }
      if (Test-Path -LiteralPath $feedAll) { Install-Atomic $feedAll "$feedAll.bak" }
      Install-Atomic $newFeed $feedAll
      Install-Atomic $newFeed $feedPilot
      $m.all = $entry
      $m.pilot = $null
    } else {
      Install-Atomic $newFeed $feedPilot
      $m.pilot = $entry
    }
  }
  $manifestTemp = Join-Path ([IO.Path]::GetTempPath()) ('ipop-notifier-manifest-' + [guid]::NewGuid().ToString('N') + '.json')
  try {
    Write-Utf8 $manifestTemp ($m | ConvertTo-Json -Depth 5)
    Install-Atomic $manifestTemp $manifestPath
  } finally { if (Test-Path -LiteralPath $manifestTemp) { Remove-Item -LiteralPath $manifestTemp -Force -Confirm:$false } }
  if ($Release -or $All) { "Published $($m.all.version) to everyone ($packId)." }
  else { "Published $Version to the pilot ($packId). Promote with -Release$(if ($Trial) { ' -Trial' }) -Releases '$Releases'. Pilot PCs on 1.2.2 require manual 1.3 installer distribution." }
} finally {
  if ($lock) { $lock.Dispose() }
  foreach ($folder in @($stage, $publishDir)) { if ($folder -and (Test-Path -LiteralPath $folder)) { Remove-Item -LiteralPath $folder -Recurse -Force -Confirm:$false } }
}
