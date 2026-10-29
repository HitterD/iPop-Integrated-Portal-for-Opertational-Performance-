# Standalone PowerShell 5.1 release regression tests; fake tools and fresh TEMP folders only.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$publisher = Join-Path $repo 'src\IPop.Notifier\publish-notifier.ps1'
$legacy = Join-Path $repo 'src\IPop.Notifier\release.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) ('notifier-release-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $root | Out-Null
$passed = 0
function Assert($condition, [string]$message) { if (-not $condition) { throw $message } }
function Reject([scriptblock]$action, [string]$pattern) {
  try { & $action; throw 'Expected rejection, but publishing succeeded' }
  catch { if ($_.Exception.Message -notmatch $pattern) { throw } }
  $script:passed++
}
function ReadJson([string]$path) { Get-Content -LiteralPath $path -Raw | ConvertFrom-Json }
function Hash([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
function Seed([string]$name) {
  $folder = Join-Path $root $name
  New-Item -ItemType Directory $folder | Out-Null
  '{"Assets":[{"PackageId":"iPopNotifier","Version":"1.2.2","FileName":"iPopNotifier-1.2.2-full.nupkg","Type":"Full"}]}' | Set-Content (Join-Path $folder 'releases.win.json')
  'old package' | Set-Content (Join-Path $folder 'iPopNotifier-1.2.2-full.nupkg')
  'old installer' | Set-Content (Join-Path $folder 'iPopNotifier-win-Setup.exe')
  return $folder
}
try {
  $dotnet = Join-Path $root 'fake-dotnet.ps1'
  @'
$ToolArgs = @($args | ForEach-Object { [string]$_ })
if ($env:NOTIFIER_FAKE_BUILD_FAIL) { $global:LASTEXITCODE = 12; return }
$version = ($ToolArgs | Where-Object { $_ -like '-p:Version=*' }) -replace '^-p:Version=', ''
$dir = $ToolArgs[[array]::IndexOf($ToolArgs, '-o') + 1]
New-Item -ItemType Directory -Force (Join-Path $dir 'Assets') | Out-Null
$version | Set-Content (Join-Path $dir 'build-version.txt')
($ToolArgs | Where-Object { $_ -like '-p:Trial=*' }) | Set-Content (Join-Path $dir 'build-trial.txt')
'icon' | Set-Content (Join-Path $dir 'Assets\ipop.ico')
$global:LASTEXITCODE = 0
'@ | Set-Content $dotnet
  $vpk = Join-Path $root 'fake-vpk.ps1'
  @'
$ToolArgs = @($args | ForEach-Object { [string]$_ })
function Arg([string]$key) { $ToolArgs[[array]::IndexOf($ToolArgs, $key) + 1] }
$version = Arg '--packVersion'; $dir = Arg '--outputDir'; $id = Arg '--packId'
if ((Get-Content (Join-Path (Arg '--packDir') 'build-version.txt') -Raw).Trim() -ne $version) { throw 'Build/package version mismatch' }
$expectedTrial = if ($id -eq 'iPopNotifierQA') { '-p:Trial=true' } else { '-p:Trial=false' }
if ((Get-Content (Join-Path (Arg '--packDir') 'build-trial.txt') -Raw).Trim() -ne $expectedTrial) { throw 'Build/package Trial identity mismatch' }
if ($env:NOTIFIER_FAKE_PACK_FAIL) { $global:LASTEXITCODE = 17; return }
$feedPath = Join-Path $dir 'releases.win.json'
$assets = @(); if (Test-Path $feedPath) { $assets = @((Get-Content $feedPath -Raw | ConvertFrom-Json).Assets) }
$file = "$id-$version-full.nupkg"
$assets += [pscustomobject]@{PackageId=$id; Version=$version; FileName=$file; Type='Full'}
@{Assets=$assets} | ConvertTo-Json -Depth 6 | Set-Content $feedPath
'package' | Set-Content (Join-Path $dir $file)
foreach ($name in @("$id-win-Setup.exe", 'RELEASES', 'assets.win.json')) { "$id $version" | Set-Content (Join-Path $dir $name) }
if ($env:NOTIFIER_FAKE_MISSING_SETUP) { Remove-Item (Join-Path $dir "$id-win-Setup.exe") -Confirm:$false }
$global:LASTEXITCODE = 0
'@ | Set-Content $vpk
  $tools = @{ Dotnet = $dotnet; Vpk = $vpk }
  $folder = Seed 'pilot'
  $beforeFeed = Hash (Join-Path $folder 'releases.win.json')
  $beforeSetup = Hash (Join-Path $folder 'iPopNotifier-win-Setup.exe')
  & $publisher -Releases $folder @tools | Out-Null
  Assert ((ReadJson (Join-Path $folder 'release.json')).pilot.version -eq '1.4.0') 'Default must use project version 1.4.0'
  Assert ((Hash (Join-Path $folder 'releases.win.json')) -eq $beforeFeed) 'Pilot changed public feed'
  Assert ((Hash (Join-Path $folder 'iPopNotifier-win-Setup.exe')) -eq $beforeSetup) 'Pilot changed public installer'
  $passed++
  Reject { & $publisher -Releases $folder -Version 1.4.0 @tools } 'not newer|already published'
  Reject { & $publisher -Releases $folder -Version 1.2.9 @tools } 'not newer'
  Reject { & $publisher -Releases $folder -Version 01.4.1 @tools } 'Version'
  $heldSetup = Join-Path $folder 'pilot\1.4.0\iPopNotifier-win-Setup.exe'
  $heldBytes = [IO.File]::ReadAllBytes($heldSetup)
  Remove-Item -LiteralPath $heldSetup -Confirm:$false
  Reject { & $publisher -Release -Releases $folder } 'Missing.*Setup'
  Assert ((Hash (Join-Path $folder 'releases.win.json')) -eq $beforeFeed) 'Missing installer modified public feed'
  [IO.File]::WriteAllBytes($heldSetup, $heldBytes)
  'tampered' | Set-Content $heldSetup
  Reject { & $publisher -Release -Releases $folder } 'hash|changed'
  [IO.File]::WriteAllBytes($heldSetup, $heldBytes)
  & $publisher -Release -Releases $folder | Out-Null
  $manifest = ReadJson (Join-Path $folder 'release.json')
  Assert ($manifest.all.version -eq '1.4.0' -and -not $manifest.pilot) 'Promotion manifest incorrect'
  Assert ((ReadJson (Join-Path $folder 'releases.win.json')).Assets[-1].Version -eq '1.4.0') 'Promotion feed incorrect'
  $passed++
  # An older stale pilot feed must not survive a direct-to-all release.
  & $publisher -All -Releases $folder -Version 1.4.1 -Mandatory @tools | Out-Null
  Assert ((ReadJson (Join-Path $folder 'release.json')).all.mandatory) 'Mandatory flag lost'
  Assert ((Hash (Join-Path $folder 'releases.win.json')) -eq (Hash (Join-Path $folder 'releases.win.pilot.json'))) 'Stale pilot feed after All'
  $passed++
  $failure = Seed 'failure'
  $failureBefore = Hash (Join-Path $failure 'releases.win.json')
  $env:NOTIFIER_FAKE_PACK_FAIL = '1'
  Reject { & $publisher -Releases $failure @tools } 'vpk pack failed'
  Remove-Item Env:NOTIFIER_FAKE_PACK_FAIL
  Assert ((Hash (Join-Path $failure 'releases.win.json')) -eq $failureBefore) 'Failed pack changed live feed'
  Assert (-not (Test-Path (Join-Path $failure 'release.json'))) 'Failed pack wrote manifest'
  $env:NOTIFIER_FAKE_BUILD_FAIL = '1'
  Reject { & $publisher -Releases $failure @tools } 'dotnet publish failed'
  Remove-Item Env:NOTIFIER_FAKE_BUILD_FAIL
  $env:NOTIFIER_FAKE_MISSING_SETUP = '1'
  Reject { & $publisher -Releases $failure @tools } 'Missing.*Setup'
  Remove-Item Env:NOTIFIER_FAKE_MISSING_SETUP
  Assert ((Hash (Join-Path $failure 'releases.win.json')) -eq $failureBefore) 'Incomplete artifacts changed feed'
  Reject { & $publisher -Trial @tools } 'explicit.*Releases'
  Reject { & $publisher -Trial -All -Releases 'C:\ipop-ops\notifier\releases' @tools } 'explicit.*Releases'
  Reject { & $publisher -Trial -Releases (Join-Path $root 'empty-pilot') @tools } 'initialised with -All'
  $qa = Join-Path $root 'qa-new'
  & $legacy -Trial -Releases $qa @tools | Out-Null
  Assert (Test-Path (Join-Path $qa 'iPopNotifierQA-win-Setup.exe')) 'Legacy Trial installer identity broken'
  Assert ((ReadJson (Join-Path $qa 'release.json')).all.version -eq '1.4.0') 'Legacy default version wrong'
  $passed++
  & $publisher -Trial -Releases $qa -Version 1.4.1 @tools | Out-Null
  & $publisher -Trial -Release -Releases $qa | Out-Null
  Assert ((ReadJson (Join-Path $qa 'release.json')).all.version -eq '1.4.1') 'Trial pilot promotion failed'
  $passed++
  Reject { & $publisher -Releases $qa -Version 1.4.2 @tools } 'identity|Trial'
  Reject { & $publisher -Releases $folder -Release -All } 'cannot combine'
  # Crash leftovers are immutable history too, even if no manifest references the package.
  'orphan' | Set-Content (Join-Path $failure 'iPopNotifier-1.5.0-full.nupkg')
  Reject { & $publisher -Releases $failure -Version 1.4.9 @tools } 'not newer'
  # Every held side file must be present before touching the public feed.
  $side = Seed 'missing-side'
  & $publisher -Releases $side @tools | Out-Null
  Remove-Item (Join-Path $side 'pilot\1.4.0\assets.win.json') -Confirm:$false
  Reject { & $publisher -Release -Releases $side } 'Missing.*assets.win.json'
  Assert ((Hash (Join-Path $side 'releases.win.json')) -eq $beforeFeed) 'Missing side file modified public feed'
  $locked = Seed 'locked'
  $lock = [IO.File]::Open((Join-Path $locked '.publish.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
  try { Reject { & $publisher -Releases $locked @tools } 'publisher|lock|used by another process' } finally { $lock.Dispose() }
  $advance = Seed 'advance'
  & $publisher -Releases $advance @tools | Out-Null
  & $publisher -Releases $advance -Version 1.4.1 @tools | Out-Null
  $advanceFeed = ReadJson (Join-Path $advance 'releases.win.pilot.json')
  Assert (@($advanceFeed.Assets | Where-Object { $_.Version -eq '1.4.0' }).Count -eq 1) 'Second pilot lost prior pilot package history'
  & $publisher -Release -Releases $advance | Out-Null
  Assert ((ReadJson (Join-Path $advance 'release.json')).all.version -eq '1.4.1') 'Latest pilot promotion failed'
  $passed++
  Reject { & $publisher -Releases $advance -Version 1.4.2-beta.1 @tools } 'Version'
  $packageCheck = Seed 'tampered-package'
  & $publisher -Releases $packageCheck @tools | Out-Null
  'tampered' | Set-Content (Join-Path $packageCheck 'iPopNotifier-1.4.0-full.nupkg')
  Reject { & $publisher -Release -Releases $packageCheck } 'package hash changed'
  $tmpFiles = @(Get-ChildItem -LiteralPath $root -Recurse -Filter '*.tmp')
  Assert ($tmpFiles.Count -eq 0) 'Atomic staging files leaked after tests'
  "PASS: $passed release regression cases (fake dotnet/vpk, temporary output only)."
} finally {
  foreach ($variable in @('NOTIFIER_FAKE_PACK_FAIL', 'NOTIFIER_FAKE_BUILD_FAIL', 'NOTIFIER_FAKE_MISSING_SETUP')) { Remove-Item "Env:$variable" -ErrorAction SilentlyContinue }
  Remove-Item -LiteralPath $root -Recurse -Force -Confirm:$false
}
