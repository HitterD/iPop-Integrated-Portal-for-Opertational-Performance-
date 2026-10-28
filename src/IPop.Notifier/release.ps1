# Compatibility entry point: direct-to-all release (including existing -Trial QA usage).
# Prefer publish-notifier.ps1 for pilot -> promotion. Default version comes from IPop.Notifier.csproj.
# Optional -Version overrides BOTH build and packaging; stable three-part SemVer only.
# Trial needs an explicit isolated -Releases folder and keeps the iPopNotifierQA identity.
param(
  [string]$Version,
  [string]$Releases = 'C:\ipop-ops\notifier\releases',
  [switch]$Trial,
  [string]$Dotnet = 'dotnet',
  [string]$Vpk = (Join-Path $env:USERPROFILE '.dotnet\tools\vpk.exe')
)
$ErrorActionPreference = 'Stop'
# Forward only explicitly supplied arguments so Trial cannot accidentally use the production default.
& (Join-Path $PSScriptRoot 'publish-notifier.ps1') @PSBoundParameters -All
