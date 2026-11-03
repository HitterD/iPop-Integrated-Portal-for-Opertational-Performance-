<#
.SYNOPSIS
  Ship committed work to the k3s cluster: push to GitHub, wait for CI (tests -> images on GHCR -> tag
  written into the manifest), then let Argo CD roll it out.

.DESCRIPTION
  Best practice it follows:
    1. Every change goes to STAGING first (deployed automatically, test it at staging.192.168.2.89.nip.io).
    2. Only the exact commit that was green on staging is promoted to PRODUCTION (main) -- same code, same tests.
    3. Production never changes by itself: Argo CD shows "OutOfSync" and this script syncs only after you
       type "yes" (or pass -Yes). Rollback = Argo CD > ipop > History and rollback.

  Usage (from the repo, after `git commit`):
    .\deploy\ship.ps1                 # current branch -> staging, waits, prints the staging URL
    .\deploy\ship.ps1 -Prod           # promote what is on staging to production (asks before syncing)
    .\deploy\ship.ps1 -Prod -Yes      # same, no question (for when you already tested staging)
    .\deploy\ship.ps1 -Status         # what runs where right now

  Needs: git remote "github", gh (signed in), kubectl with ~/.kube/ipop-k3s.yaml (only for the sync step).
#>
[CmdletBinding()]
param(
    [switch]$Prod,
    [switch]$Yes,
    [switch]$Status,
    [string]$Remote = 'github',
    [string]$Repo = 'HitterD/iPop-Integrated-Portal-for-Opertational-Performance-',
    [string]$KubeConfig = "$HOME\.kube\ipop-k3s.yaml",
    [int]$TimeoutMinutes = 40
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$env:GIT_TERMINAL_PROMPT = '0'   # never hang on a hidden credential prompt

function Say([string]$text, [string]$color = 'Gray') { Write-Host $text -ForegroundColor $color }
function Fail([string]$text) { Write-Host "X $text" -ForegroundColor Red; exit 1 }
# git/kubectl write progress to stderr; in Windows PowerShell 5.1 that becomes an error under 'Stop', so relax it here.
# Named Invoke-*: PowerShell names are case-insensitive, so a function "Git" would call itself instead of git.exe.
function Invoke-Git { $ErrorActionPreference = 'Continue'; $out = & git.exe @args 2>&1 | ForEach-Object { "$_" }; if ($LASTEXITCODE -ne 0) { Fail "git $($args -join ' '): $out" }; $out }
function Invoke-Kube { $ErrorActionPreference = 'Continue'; $env:KUBECONFIG = $KubeConfig; $out = & kubectl.exe @args 2>&1 | ForEach-Object { "$_" }; if ($LASTEXITCODE -ne 0) { Fail "kubectl $($args -join ' '): $out" }; $out }

$urls = @{ staging = 'http://staging.192.168.2.89.nip.io'; main = 'http://ipop.192.168.2.89.nip.io' }
$apps = @{ staging = 'ipop-staging'; main = 'ipop' }

function AppState([string]$app) {
    $env:KUBECONFIG = $KubeConfig
    $j = & kubectl -n argocd get application $app -o json 2>$null | ConvertFrom-Json
    if (-not $j) { return $null }
    $img = (& kubectl -n ($(if ($app -eq 'ipop') { 'ipop' } else { 'ipop-staging' })) get deploy ipop-api -o jsonpath='{.spec.template.spec.containers[0].image}' 2>$null)
    [pscustomobject]@{ App = $app; Sync = $j.status.sync.status; Health = $j.status.health.status; Image = $img }
}

if ($Status) {
    Invoke-Git fetch -q $Remote | Out-Null
    foreach ($b in 'staging', 'main') {
        $sha = (Invoke-Git rev-parse --short "$Remote/$b").Trim()
        $runs = gh run list -R $Repo --branch $b --workflow 'Build and deploy' --limit 1 --json headSha,status,conclusion | ConvertFrom-Json
        $run = if ($runs) { "$($runs[0].status) $($runs[0].conclusion) @ $($runs[0].headSha.Substring(0,7))" } else { 'none' }
        $st = AppState $apps[$b]
        Say ("{0,-8} branch {1}  last build: {2}" -f $b, $sha, $run) 'Cyan'
        if ($st) { Say ("         Argo {0}: {1}/{2}  running {3}" -f $st.App, $st.Sync, $st.Health, $st.Image) }
    }
    exit 0
}

# ---- 1. what are we shipping ----
if (git.exe status --porcelain --untracked-files=no) { Fail 'There are uncommitted changes. Commit (or stash) them first.' }
Invoke-Git fetch -q $Remote | Out-Null

if ($Prod) {
    # Promote exactly what staging built and tested (fast-forward main to it).
    $target = 'main'
    $sha = (Invoke-Git rev-parse "$Remote/staging").Trim()
    $green = gh run list -R $Repo --branch staging --workflow 'Build and deploy' --commit $sha --json conclusion --jq '.[0].conclusion' 2>$null
    # The bot's tag commit on staging has no build of its own: look at its parent too.
    if ($green -ne 'success') {
        $parent = (Invoke-Git rev-parse "$sha^").Trim()
        $green = gh run list -R $Repo --branch staging --workflow 'Build and deploy' --commit $parent --json conclusion --jq '.[0].conclusion' 2>$null
    }
    if ($green -ne 'success') { Fail "staging ($($sha.Substring(0,7))) has no green build. Run .\deploy\ship.ps1 first and test staging." }
    & git.exe merge-base --is-ancestor "$Remote/main" $sha
    if ($LASTEXITCODE -ne 0) { Fail 'main has commits that staging does not. Merge main into your branch, ship to staging again, then promote.' }
    $source = $sha
    Say "Promote staging $($sha.Substring(0,7)) -> production (main)" 'Cyan'
}
else {
    $target = 'staging'
    $source = (Invoke-Git rev-parse HEAD).Trim()
    $branch = (Invoke-Git rev-parse --abbrev-ref HEAD).Trim()
    & git.exe merge-base --is-ancestor "$Remote/main" HEAD
    if ($LASTEXITCODE -ne 0) { Fail "Your branch '$branch' is behind main. Run: git merge $Remote/main   (then ship again)" }
    Say "Ship $branch $($source.Substring(0,7)) -> staging" 'Cyan'
    Invoke-Git log --oneline "$Remote/staging..HEAD" | Select-Object -First 15 | ForEach-Object { Say "  $_" }
}

# ---- 2. push (staging may be replaced: it always mirrors the latest thing under test) ----
$before = Get-Date
if ($target -eq 'staging') { Invoke-Git push --force-with-lease $Remote "${source}:refs/heads/staging" | Out-Null }
else { Invoke-Git push $Remote "${source}:refs/heads/main" | Out-Null }
Say "Pushed. Waiting for GitHub Actions (tests, images)..."

# ---- 3. wait for the workflow run of this commit ----
$run = $null
for ($i = 0; $i -lt 30 -and -not $run; $i++) {
    Start-Sleep -Seconds 5
    $list = gh run list -R $Repo --branch $target --workflow 'Build and deploy' --commit $source --json databaseId,status --limit 1 | ConvertFrom-Json
    if ($list) { $run = $list[0].databaseId }
}
if (-not $run) { Fail 'GitHub did not start a "Build and deploy" run. Check the Actions tab.' }
Say "  run https://github.com/$Repo/actions/runs/$run"
& gh run watch $run -R $Repo --exit-status --interval 20 | Out-Null
if ($LASTEXITCODE -ne 0) {
    gh run view $run -R $Repo --log-failed 2>$null | Select-String -Pattern '\[FAIL\]|Error Message|error CS|##\[error\]' | Select-Object -First 15 | ForEach-Object { Say "  $($_.Line)" 'Yellow' }
    Fail "Build failed - nothing was deployed. Details: gh run view $run -R $Repo --log-failed"
}
Say 'Tests green, images on GHCR, manifest updated.' 'Green'

# ---- 4. roll out ----
$app = $apps[$target]
$env:KUBECONFIG = $KubeConfig
Invoke-Kube -n argocd annotate application $app argocd.argoproj.io/refresh=normal --overwrite | Out-Null
if ($target -eq 'main') {
    Start-Sleep -Seconds 10
    $st = AppState $app
    Say "Production (Argo '$app') is $($st.Sync); running $($st.Image)." 'Cyan'
    if (-not $Yes) {
        $answer = Read-Host 'Sync production now? Users on the cluster get the new version (type yes)'
        if ($answer -ne 'yes') { Say 'Not synced. Do it later in Argo CD (ipop > SYNC) or run: .\deploy\ship.ps1 -Prod -Yes' 'Yellow'; exit 0 }
    }
    $op = '{"operation":{"initiatedBy":{"username":"ship.ps1"},"sync":{"revision":"main"}}}'
    Invoke-Kube -n argocd patch application $app --type merge -p $op | Out-Null
}

# ---- 5. wait until Argo is Synced + Healthy on the new image ----
$deadline = (Get-Date).AddMinutes(15)
do {
    Start-Sleep -Seconds 10
    $st = AppState $app
    $ok = $st -and $st.Sync -eq 'Synced' -and $st.Health -eq 'Healthy' -and $st.Image -like "*$($source.Substring(0,12))*"
} until ($ok -or (Get-Date) -gt $deadline)
if (-not $ok) { Fail "Argo '$app' did not become Synced+Healthy on the new image in 15 min (now $($st.Sync)/$($st.Health), $($st.Image)). Check Argo CD." }

$code = try { (Invoke-WebRequest "$($urls[$target])/login" -UseBasicParsing -TimeoutSec 15).StatusCode } catch { 0 }
if ($code -ne 200) { Fail "$($urls[$target])/login answered $code after the rollout." }
Say "LIVE on $target : $($urls[$target])  ($($st.Image), $([int]((Get-Date) - $before).TotalMinutes) min)" 'Green'
if ($target -eq 'staging') { Say 'Test it there. When it is good: .\deploy\ship.ps1 -Prod' 'Cyan' }
