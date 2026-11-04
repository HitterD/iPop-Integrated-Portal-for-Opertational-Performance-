# Builds the Group Policy MSI around an existing Velopack Setup.exe (see Notifier.wxs).
#   .\build-gpo-msi.ps1 -Setup out\iPopNotifier-win-Setup.exe -Version 1.4.8 -OutDir out
# Writes <OutDir>\<PackId>-win-GPO.msi. Needs the WiX 5 dotnet tool (dotnet tool install -g wix --version 5.0.2)
# and the .NET Framework 4 csc.exe that ships with Windows (no SDK needed for the small bootstrapper).
param(
  [Parameter(Mandatory)] [string]$Setup,
  [Parameter(Mandatory)] [string]$Version,
  [Parameter(Mandatory)] [string]$OutDir,
  [string]$Wix = 'wix'
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') { throw "Invalid Version '$Version'." }
$Setup = (Resolve-Path -LiteralPath $Setup).Path
$name = Split-Path $Setup -Leaf
if ($name -notmatch '^(iPopNotifier(QA)?)-win-Setup\.exe$') { throw "Unexpected setup name: $name" }
$packId = $Matches[1]
$title = if ($packId -eq 'iPopNotifierQA') { 'iPop Notifier QA' } else { 'iPop Notifier' }
# Fixed per identity forever: GPO/MSI upgrades find the previous version through it.
$upgradeCode = if ($packId -eq 'iPopNotifierQA') { '{6E0B3C55-2B7D-4C59-9C1E-6A4F2C1B9D02}' } else { '{0C7D6C4A-6F0E-4F0B-9E2B-3B8C2A1D5E01}' }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path -LiteralPath $OutDir).Path
$work = Join-Path ([IO.Path]::GetTempPath()) ('ipop-gpo-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
  $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
  if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc" }
  $exe = Join-Path $work 'iPopNotifierDeploy.exe'
  & $csc /nologo /target:winexe /platform:anycpu /optimize+ "/out:$exe" (Join-Path $PSScriptRoot 'Bootstrap.cs')
  if ($LASTEXITCODE -ne 0) { throw 'Bootstrapper compile failed' }

  $msi = Join-Path $OutDir "$packId-win-GPO.msi"
  & $Wix build (Join-Path $PSScriptRoot 'Notifier.wxs') -arch x64 -o $msi `
    -d "Version=$Version" -d "UpgradeCode=$upgradeCode" -d "Title=$title" -d "PackId=$packId" `
    -d "DeployExe=$exe" -d "SetupExe=$Setup" -nologo
  if ($LASTEXITCODE -ne 0) { throw 'wix build failed' }
  Remove-Item -LiteralPath ([IO.Path]::ChangeExtension($msi, '.wixpdb')) -ErrorAction SilentlyContinue
  "Built $msi"
} finally {
  Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
