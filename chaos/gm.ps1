<#
  SPOILERS. Game master for ./lab.ps1 incident | hint | reveal | resolve.
  Picks a scenario from scenarios.ps1, injects it, prints an incident ticket and keeps the answer in chaos/.state/.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('incident', 'hint', 'reveal', 'resolve', 'list')]
    [string]$Command,
    [int]$Id = 0,
    [string]$Difficulty,
    [string]$EnvFile,
    # verify.ps1: no ticket box, no confirmation output
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path $PSScriptRoot -Parent
$StateDir = Join-Path $PSScriptRoot '.state'
$StateFile = Join-Path $StateDir 'incident.json'
. (Join-Path $PSScriptRoot 'lib.ps1')
$Scenarios = . (Join-Path $PSScriptRoot 'scenarios.ps1')

function Read-State {
    if (-not (Test-Path $StateFile)) { return $null }
    return Get-Content $StateFile -Raw | ConvertFrom-Json
}

function Save-State($State) {
    New-Item -ItemType Directory -Force $StateDir | Out-Null
    $State | ConvertTo-Json -Depth 6 | Set-Content -Path $StateFile -Encoding UTF8
}

function Get-Scenario([int]$ScenarioId) { return $Scenarios | Where-Object { $_.Id -eq $ScenarioId } | Select-Object -First 1 }

function Write-Box([string]$Title, [string[]]$Lines, [string]$Color = 'White') {
    $bar = '=' * 78
    Write-Host $bar -ForegroundColor $Color
    Write-Host "  $Title" -ForegroundColor $Color
    Write-Host $bar -ForegroundColor $Color
    foreach ($l in $Lines) { Write-Host $l }
    Write-Host $bar -ForegroundColor $Color
}

function Start-Incident {
    $active = Read-State
    if ($active) {
        Write-Host "An incident is already open ($($active.ticketId), opened $($active.openedAt)). Run ./lab.ps1 resolve first." -ForegroundColor Yellow
        return
    }
    $pool = @($Scenarios)
    if ($Id -gt 0) {
        $pool = @($Scenarios | Where-Object { $_.Id -eq $Id })
        if ($pool.Count -eq 0) { throw "There is no scenario with id $Id (valid: 1-$($Scenarios.Count))." }
    } elseif ($Difficulty) {
        $pool = @($Scenarios | Where-Object { $_.Difficulty -eq $Difficulty.ToLower() })
        if ($pool.Count -eq 0) { throw "Unknown difficulty '$Difficulty' (easy | medium | hard)." }
    }
    $s = $pool | Get-Random

    if (-not (Wait-AppHealthy 30)) { throw 'The lab is not healthy. Start it first (./lab.ps1 up) or resolve the previous incident.' }
    if (-not $Quiet) { Write-Host 'Preparing the incident (this can take a moment)...' -ForegroundColor DarkGray }

    $state = @{ scenarioId = $s.Id; ticketId = 'INC-' + (Get-Date -Format 'yyyyMMdd-HHmm'); hintsGiven = 0 }
    # Saved before injecting, so resolve can clean up even if injection fails half-way.
    $state.openedAt = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    Save-State $state
    & $s.Inject $state
    if ($s.Load -gt 0) { Start-Traffic 'traffic-1' 'main.js' @{ SCENARIO = 'steady'; RATE = $s.Load; DURATION = '6h' } }
    $state.openedAt = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    $state.rootCause = $s.RootCause
    $state.explanation = $s.Explanation
    $state.fix = $s.Fix
    Save-State $state

    if ($Quiet) { return }
    $ticket = ($s.Ticket -f (Get-Date -Format 'HH:mm')).Trim() -split "`r?`n"
    $lines = @("Ticket   : $($state.ticketId)    Severity: $($s.Severity)    Opened: $($state.openedAt)", '') + $ticket
    if ($s.Load -gt 0) { $lines += @('', "Traffic  : simulated customers are active (~$($s.Load) requests/s of mixed traffic).") }
    $lines += @('', 'Start with Grafana: http://localhost:3000 (PaymentLab / Overview).',
        './lab.ps1 hint (up to 3)  |  ./lab.ps1 reveal  |  ./lab.ps1 resolve')
    Write-Box 'INCIDENT' $lines 'Red'
}

function Show-Hint {
    $st = Read-State
    if (-not $st) { Write-Host 'No open incident. Start one with ./lab.ps1 incident.'; return }
    $s = Get-Scenario $st.scenarioId
    $n = [int]$st.hintsGiven
    if ($n -ge 3) {
        Write-Host 'No more hints. ./lab.ps1 reveal shows the answer.' -ForegroundColor Yellow
        Write-Host "Hint 3/3: $($s.Hints[2])"
        return
    }
    $n++
    $st.hintsGiven = $n
    $st | ConvertTo-Json -Depth 6 | Set-Content -Path $StateFile -Encoding UTF8
    Write-Host "Hint $n/3: $($s.Hints[$n - 1])" -ForegroundColor Cyan
}

function Show-Reveal {
    $st = Read-State
    if (-not $st) { Write-Host 'No open incident.'; return }
    $s = Get-Scenario $st.scenarioId
    Write-Box "ANSWER - scenario $($s.Id): $($s.Title) [$($s.Category), $($s.Difficulty)]" @(
        "Root cause : $($s.RootCause)", '',
        "Why        : $($s.Explanation)", '',
        "Fix        : $($s.Fix)", '',
        'Details    : docs/spoilers/ANSWERS.md',
        'Restore    : ./lab.ps1 resolve'
    ) 'Yellow'
}

function Invoke-Resolve {
    $st = Read-State
    if (-not $Quiet) { Write-Host 'Removing the fault and restoring the system...' -ForegroundColor Cyan }
    Stop-Traffic
    if ($st) {
        $s = Get-Scenario $st.scenarioId
        try { & $s.Resolve $st } catch { Write-Host "  scenario clean-up: $($_.Exception.Message)" -ForegroundColor Yellow }
    }
    # Generic clean-up: safe to run even when nothing was injected.
    Reset-Toxiproxy
    try { Reset-BankConfig } catch { }
    Remove-RabbitPolicies
    Restore-ResultsBinding
    foreach ($svc in @('connector', 'payments', 'wallet', 'gateway', 'fakebank')) {
        $cid = Get-ContainerId $svc
        if ($cid -and ((Invoke-Native 'docker' @('inspect', '-f', '{{.State.Paused}}', $cid)) -eq 'true')) { Invoke-Native 'docker' @('unpause', $cid) | Out-Null }
    }
    Invoke-Dc @('up', '-d') | Out-Null

    if (-not (Wait-AppHealthy 300)) { throw 'Services did not become ready after the clean-up. Check ./lab.ps1 status.' }
    Start-Sleep -Seconds 5
    $resumed = Resume-ParkedPayments

    # Let in-flight payments drain, then prove the system works end to end.
    Start-Sleep -Seconds 10
    $shell = (Get-Process -Id $PID).Path
    $smoke = Invoke-Native $shell @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $Root 'scripts/smoke.ps1'))
    $ok = $script:ExitCode -eq 0
    if ($st) {
        $hist = Join-Path $StateDir 'history'
        New-Item -ItemType Directory -Force $hist | Out-Null
        Move-Item $StateFile (Join-Path $hist "$($st.ticketId)-scenario$($st.scenarioId).json") -Force
    }
    if ($Quiet) { if (-not $ok) { throw 'smoke test failed after resolve' }; return }
    if ($resumed -gt 0) { Write-Host "  $resumed payment(s) that were parked for review were resumed." -ForegroundColor DarkGray }
    if ($ok) { Write-Host 'Resolved. Smoke test passed - the system is healthy again.' -ForegroundColor Green }
    else {
        Write-Host ($smoke | Select-Object -Last 15 | Out-String)
        Write-Host 'Fault removed, but the smoke test failed. Check ./lab.ps1 status and logs.' -ForegroundColor Red
    }
}

switch ($Command) {
    'incident' { Start-Incident }
    'hint' { Show-Hint }
    'reveal' { Show-Reveal }
    'resolve' { Invoke-Resolve }
    'list' { $Scenarios | ForEach-Object { '{0,2} {1,-7} {2,-22} {3}' -f $_.Id, $_.Difficulty, $_.Category, $_.Title } }
}
