<#
.SYNOPSIS
    PaymentLab control script.

.EXAMPLE
    ./lab.ps1 up                 # build and start everything
    ./lab.ps1 status             # containers, health, resource usage
    ./lab.ps1 logs payments      # follow logs of one service
    ./lab.ps1 smoke              # end-to-end smoke test
    ./lab.ps1 down               # stop (keeps data)
    ./lab.ps1 reset              # stop and wipe all data, then start fresh
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('up', 'down', 'reset', 'status', 'logs', 'smoke', 'urls', 'help')]
    [string]$Command = 'help',

    [Parameter(Position = 1, ValueFromRemainingArguments = $true)]
    [string[]]$Rest,

    # Extra env file layered over .env, e.g. limits.tight.env
    [string]$EnvFile,

    # Skip confirmation prompts (reset)
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
Set-Location $Root

# Deliberately a simple function (no param block): with an advanced function PowerShell would
# bind "-d" to -Debug instead of passing it through to docker compose.
function Invoke-Compose {
    $envArgs = @('--env-file', (Join-Path $Root '.env'))
    if ($EnvFile) { $envArgs += @('--env-file', (Resolve-Path $EnvFile).Path) }
    & docker compose @envArgs @args
    if ($LASTEXITCODE -ne 0) { throw "docker compose $($args -join ' ') failed with exit code $LASTEXITCODE" }
}

function Test-Docker {
    docker info --format '{{.ServerVersion}}' *> $null
    if ($LASTEXITCODE -ne 0) { throw 'Docker is not running. Start Docker Desktop and try again.' }
}

$Endpoints = [ordered]@{
    'Gateway (API)'    = 'http://localhost:8080'
    'Payments Swagger' = 'http://localhost:8081/swagger'
    'Wallet Swagger'   = 'http://localhost:8082/swagger'
    'Fake Bank config' = 'http://localhost:8090/admin/config'
    'RabbitMQ UI'      = 'http://localhost:15672  (lab / lab)'
    'Toxiproxy API'    = 'http://localhost:8474/proxies'
    'Postgres'         = 'payments: localhost:5432, wallet: localhost:5433 (user = password = db name)'
}

$HealthChecks = [ordered]@{
    gateway   = 'http://localhost:8080/healthz'
    payments  = 'http://localhost:8081/ready'
    wallet    = 'http://localhost:8082/ready'
    connector = 'http://localhost:8085/ready'
    fakebank  = 'http://localhost:8090/healthz'
}

function Test-Url([string]$Url) {
    try {
        $r = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 3
        return $r.StatusCode -lt 400
    } catch { return $false }
}

function Wait-Healthy([int]$TimeoutSeconds = 180) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $pending = @($HealthChecks.Keys)
    while ($pending.Count -gt 0 -and (Get-Date) -lt $deadline) {
        $pending = @($pending | Where-Object { -not (Test-Url $HealthChecks[$_]) })
        if ($pending.Count -gt 0) {
            Write-Host ("  waiting for: {0}" -f ($pending -join ', ')) -ForegroundColor DarkGray
            Start-Sleep -Seconds 3
        }
    }
    if ($pending.Count -gt 0) { throw "Services not ready after $TimeoutSeconds s: $($pending -join ', '). Check ./lab.ps1 logs <service>." }
}

function Show-Urls {
    Write-Host ''
    foreach ($k in $Endpoints.Keys) { Write-Host ("  {0,-18} {1}" -f $k, $Endpoints[$k]) }
    Write-Host ''
}

switch ($Command) {
    'up' {
        Test-Docker
        Write-Host 'Building and starting PaymentLab...' -ForegroundColor Cyan
        Invoke-Compose up -d --build --remove-orphans
        Write-Host 'Waiting for services to become ready...' -ForegroundColor Cyan
        Wait-Healthy
        Write-Host 'PaymentLab is up.' -ForegroundColor Green
        Show-Urls
    }
    'down' {
        Test-Docker
        Invoke-Compose down --remove-orphans
    }
    'reset' {
        Test-Docker
        if (-not $Force) {
            $answer = Read-Host 'This deletes ALL lab data (databases, queues). Type "yes" to continue'
            if ($answer -ne 'yes') { Write-Host 'Aborted.'; return }
        }
        Invoke-Compose down -v --remove-orphans
        & $PSCommandPath up -EnvFile:$EnvFile
    }
    'status' {
        Test-Docker
        Invoke-Compose ps --format 'table {{.Service}}\t{{.Status}}\t{{.Ports}}'
        Write-Host ''
        Write-Host 'Readiness:' -ForegroundColor Cyan
        foreach ($k in $HealthChecks.Keys) {
            $ok = Test-Url $HealthChecks[$k]
            Write-Host ("  {0,-10} {1}" -f $k, $(if ($ok) { 'ready' } else { 'NOT READY' })) -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' })
        }
        Write-Host ''
        Write-Host 'Resource usage:' -ForegroundColor Cyan
        $ids = & docker compose ps -q
        if ($ids) { docker stats --no-stream --format 'table {{.Name}}\t{{.CPUPerc}}\t{{.MemUsage}}\t{{.MemPerc}}' @ids }
    }
    'logs' {
        Test-Docker
        if ($Rest -and $Rest.Count -gt 0) { Invoke-Compose logs -f --tail 200 @Rest }
        else { Invoke-Compose logs -f --tail 50 }
    }
    'smoke' {
        $smokeArgs = @(); if ($Rest) { $smokeArgs = $Rest }
        & (Join-Path $Root 'scripts/smoke.ps1') @smokeArgs
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
    'urls' { Show-Urls }
    default { Get-Help $PSCommandPath -Detailed | Out-Host }
}
