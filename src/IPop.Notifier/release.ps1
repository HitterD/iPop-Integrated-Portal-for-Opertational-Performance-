# Builds a new iPop Notifier release (per-user installer + Velopack update feed).
#   .\release.ps1 -Version 1.0.1
# Output: C:\ipop-ops\notifier\releases (what the Web host serves at /download/notifier/). Earlier packages in that
# folder are kept so Velopack can build a small delta for clients updating from the previous version.
# Needs: dotnet 10 SDK, the vpk tool (dotnet tool install -g vpk --version 1.2.161).
param(
  [Parameter(Mandatory = $true)][string]$Version,
  [string]$Releases = 'C:\ipop-ops\notifier\releases',
  # Trial build: talks to the QA copy on 10.10.6.13 and installs as "iPop Notifier QA" next to the real app.
  [switch]$Trial
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.0.1" }
$projDir = $PSScriptRoot
$publishDir = Join-Path $env:TEMP "ipop-notifier-publish-$Version"
if (Test-Path $publishDir) { Remove-Item -Recurse -Force -Confirm:$false $publishDir }
New-Item -ItemType Directory -Force $Releases | Out-Null

$trialProp = if ($Trial) { '-p:Trial=true' } else { '-p:Trial=false' }
dotnet publish (Join-Path $projDir 'IPop.Notifier.csproj') -c Release -r win-x64 --self-contained -p:Version=$Version $trialProp -o $publishDir -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

$vpk = Join-Path $env:USERPROFILE '.dotnet\tools\vpk.exe'
$packId = if ($Trial) { 'iPopNotifierQA' } else { 'iPopNotifier' }
$packTitle = if ($Trial) { 'iPop Notifier QA' } else { 'iPop Notifier' }
& $vpk pack --packId $packId --packVersion $Version --packDir $publishDir --mainExe iPopNotifier.exe `
  --packTitle $packTitle --packAuthors 'PT Santos Jaya Abadi' --icon (Join-Path $publishDir 'Assets\ipop.ico') `
  --shortcuts StartMenuRoot --outputDir $Releases
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }

Remove-Item -Recurse -Force -Confirm:$false $publishDir
Get-ChildItem $Releases | Sort-Object LastWriteTime | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
"Installer: /download/notifier/iPopNotifier-win-Setup.exe · clients pick up $Version within 6 hours (tray → Check for updates for now)."
