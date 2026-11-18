<#
.SYNOPSIS
    PaymentLab control script.

.EXAMPLE
    ./lab.ps1 up                 # build and start everything
    ./lab.ps1 status             # containers, health, resource usage
    ./lab.ps1 logs payments      # follow logs of one service
    ./lab.ps1 smoke              # end-to-end smoke test
    ./lab.ps1 seed medium        # replace DB contents with synthetic history (small | medium | large)
    ./lab.ps1 load steady 50 10m # k6 load test (steady | ramp | spike | soak) [rate/s] [duration]
    ./lab.ps1 incident           # start a random incident and print the ticket
    ./lab.ps1 incident -Id 3     # a specific incident (also: --id 3, -Difficulty easy|medium|hard)
    ./lab.ps1 hint               # one more hint (max 3)
    ./lab.ps1 reveal             # show the root cause
    ./lab.ps1 resolve            # remove the fault, restore the system, run the smoke test
    ./lab.ps1 down               # stop (keeps data)
    ./lab.ps1 reset              # stop and wipe all data, then start fresh
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('up', 'down', 'reset', 'status', 'logs', 'smoke', 'seed', 'load', 'incident', 'hint', 'reveal', 'resolve', 'urls', 'help')]
    [string]$Command = 'help',

    [Parameter(Position = 1, ValueFromRemainingArguments = $true)]
    [string[]]$Rest,

    # Extra env file layered over .env, e.g. limits.tight.env
    [string]$EnvFile,

    # Skip confirmation prompts (reset, seed)
    [switch]$Force,

    # incident: pick a specific scenario, or one of a difficulty
    [int]$Id = 0,
    [ValidateSet('', 'easy', 'medium', 'hard')]
    [string]$Difficulty = ''
)

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
Set-Location $Root

# Deliberately a simple function (no param block): with an advanced function PowerShell would
# bind "-d" to -Debug instead of passing it through to docker compose.
function Get-EnvArgs {
    $envArgs = @('--env-file', (Join-Path $Root '.env'))
    if ($EnvFile) { $envArgs += @('--env-file', (Resolve-Path $EnvFile).Path) }
    return $envArgs
}

function Invoke-Compose {
    $envArgs = Get-EnvArgs
    # docker writes progress to stderr; in Windows PowerShell 5.1 with ErrorActionPreference=Stop that would
    # abort the script when output is redirected, so only the exit code decides about failure.
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & docker compose @envArgs @args 2>&1 | ForEach-Object { "$_" } }
    finally { $ErrorActionPreference = $prev }
    if ($LASTEXITCODE -ne 0) { throw "docker compose $($args -join ' ') failed with exit code $LASTEXITCODE" }
}

function Test-Docker {
    docker info --format '{{.ServerVersion}}' *> $null
    if ($LASTEXITCODE -ne 0) { throw 'Docker is not running. Start Docker Desktop and try again.' }
}

$Endpoints = [ordered]@{
    'Grafana'          = 'http://localhost:3000  (dashboards: PaymentLab folder; admin / admin to edit)'
    'Gateway (API)'    = 'http://localhost:8080'
    'Payments Swagger' = 'http://localhost:8081/swagger'
    'Wallet Swagger'   = 'http://localhost:8082/swagger'
    'Fake Bank config' = 'http://localhost:8090/admin/config'
    'RabbitMQ UI'      = 'http://localhost:15672  (lab / lab)'
    'Toxiproxy API'    = 'http://localhost:8474/proxies'
    'Prometheus'       = 'http://localhost:9090'
    'cAdvisor'         = 'http://localhost:8088'
    'Postgres'         = 'payments: localhost:5432, wallet: localhost:5433 (user = password = db name)'
}

$HealthChecks = [ordered]@{
    gateway   = 'http://localhost:8080/healthz'
    payments  = 'http://localhost:8081/ready'
    wallet    = 'http://localhost:8082/ready'
    connector = 'http://localhost:8085/ready'
    fakebank  = 'http://localhost:8090/healthz'
    grafana   = 'http://localhost:3000/api/health'
    prometheus = 'http://localhost:9090/-/ready'
    loki      = 'http://localhost:3100/ready'
    tempo     = 'http://localhost:3200/ready'
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

# Runs SQL in one of the databases and returns the output (never throws; $LASTEXITCODE tells success).
function Invoke-Psql([string]$Service, [string]$Db, [string]$Sql) {
    $envArgs = Get-EnvArgs
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { return (& docker compose @envArgs exec -T $Service psql -U $Db -d $Db -At -c $Sql 2>&1 | ForEach-Object { "$_" }) }
    finally { $ErrorActionPreference = $prev }
}

function Get-SeededAccounts {
    $out = Invoke-Psql 'wallet-db' 'wallet' "SELECT CASE WHEN to_regclass('lab_seed_info') IS NULL THEN 0 ELSE (SELECT coalesce(max(accounts), 0) FROM lab_seed_info) END"
    $n = 0
    if ($LASTEXITCODE -eq 0 -and [int]::TryParse((@($out)[-1]).Trim(), [ref]$n)) { return $n }
    return 0
}

$SeedScales = [ordered]@{
    small  = @{ Accounts = 10000;  Payments = 10000;   Note = '~50k history rows, seconds' }
    medium = @{ Accounts = 100000; Payments = 1000000; Note = '~5M history rows, about 1-3 minutes, ~1.5 GB disk' }
    large  = @{ Accounts = 500000; Payments = 4000000; Note = '~21M history rows, about 5-15 minutes, ~6 GB disk' }
}

function Invoke-Seed([string]$Scale) {
    if (-not $SeedScales.Contains($Scale)) { throw "Unknown scale '$Scale'. Use: $($SeedScales.Keys -join ' | ')" }
    $s = $SeedScales[$Scale]
    Write-Host ("Seed '{0}': {1:N0} accounts, {2:N0} payments ({3})." -f $Scale, $s.Accounts, $s.Payments, $s.Note) -ForegroundColor Cyan
    if (-not $Force) {
        $answer = Read-Host 'This REPLACES all data in both databases. Type "yes" to continue'
        if ($answer -ne 'yes') { Write-Host 'Aborted.'; return }
    }
    $base = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    $sw = [Diagnostics.Stopwatch]::StartNew()

    Write-Host 'Stopping application services (databases keep running)...' -ForegroundColor Cyan
    Invoke-Compose stop gateway payments wallet connector | Out-Null
    Invoke-Compose up -d payments-db wallet-db | Out-Null
    foreach ($db in @(@('payments-db', 'payments'), @('wallet-db', 'wallet'))) {
        do { Invoke-Psql $db[0] $db[1] 'SELECT 1' | Out-Null; if ($LASTEXITCODE -ne 0) { Start-Sleep 2 } } while ($LASTEXITCODE -ne 0)
    }

    Write-Host 'Seeding both databases in parallel...' -ForegroundColor Cyan
    $jobs = foreach ($t in @(@('wallet-db', 'wallet', 'wallet.sql'), @('payments-db', 'payments', 'payments.sql'))) {
        Start-Job -ArgumentList $Root, (Get-EnvArgs), $t[0], $t[1], $t[2], $s.Accounts, $s.Payments, $base, $Scale -ScriptBlock {
            param($root, $envArgs, $svc, $db, $file, $accounts, $payments, $base, $scale)
            Set-Location $root
            $ErrorActionPreference = 'Continue'
            $started = Get-Date
            $out = & docker compose @envArgs exec -T $svc psql -U $db -d $db -q -v ON_ERROR_STOP=1 --single-transaction `
                -v "accounts=$accounts" -v "payments=$payments" -v "base=$base" -v "scale=$scale" -f "/seed/$file" 2>&1 | ForEach-Object { "$_" }
            $code = $LASTEXITCODE
            if ($code -eq 0) {
                $out += & docker compose @envArgs exec -T $svc psql -U $db -d $db -q -c 'VACUUM (ANALYZE)' 2>&1 | ForEach-Object { "$_" }
                $code = $LASTEXITCODE
            }
            [pscustomobject]@{ Service = $svc; ExitCode = $code; Seconds = [int]((Get-Date) - $started).TotalSeconds; Output = ($out -join "`n") }
        }
    }
    $results = $jobs | Wait-Job | Receive-Job
    $jobs | Remove-Job
    $failed = $false
    foreach ($r in $results) {
        if ($r.ExitCode -eq 0) { Write-Host ("  {0,-12} done in {1}s" -f $r.Service, $r.Seconds) -ForegroundColor Green }
        else { $failed = $true; Write-Host ("  {0,-12} FAILED:`n{1}" -f $r.Service, $r.Output) -ForegroundColor Red }
    }

    Write-Host 'Starting application services...' -ForegroundColor Cyan
    Invoke-Compose up -d gateway payments wallet connector | Out-Null
    Wait-Healthy
    if ($failed) { throw 'Seeding failed (see above).' }

    $p = Invoke-Psql 'payments-db' 'payments' "SELECT format('%s payments, %s history rows', (SELECT count(*) FROM payments), (SELECT count(*) FROM payment_history))"
    $w = Invoke-Psql 'wallet-db' 'wallet' "SELECT format('%s accounts, %s ledger entries, %s reservations', (SELECT count(*) FROM accounts), (SELECT count(*) FROM ledger_entries), (SELECT count(*) FROM reservations))"
    Write-Host ("Seed '{0}' finished in {1:N0}s." -f $Scale, $sw.Elapsed.TotalSeconds) -ForegroundColor Green
    Write-Host "  payments-db: $(@($p)[-1])"
    Write-Host "  wallet-db:   $(@($w)[-1])"
}

function Invoke-Load([string[]]$LoadArgs) {
    $scenario = if ($LoadArgs -and $LoadArgs.Count -ge 1) { $LoadArgs[0].ToLower() } else { 'steady' }
    if ($scenario -notin @('steady', 'ramp', 'spike', 'soak')) { throw "Unknown scenario '$scenario'. Use: steady | ramp | spike | soak" }
    if (-not (Test-Url $HealthChecks['gateway'])) { throw 'The gateway is not reachable. Start the lab first: ./lab.ps1 up' }

    $envs = @('-e', "SCENARIO=$scenario", '-e', "SEEDED_ACCOUNTS=$(Get-SeededAccounts)")
    if ($LoadArgs.Count -ge 2) { $envs += @('-e', "RATE=$($LoadArgs[1])") }
    if ($LoadArgs.Count -ge 3) { $envs += @('-e', "DURATION=$($LoadArgs[2])") }

    $results = Join-Path $Root 'load/results'
    New-Item -ItemType Directory -Force $results | Out-Null
    $summary = "$scenario-$(Get-Date -Format 'yyyyMMdd-HHmmss').json"
    Write-Host "k6 '$scenario' against the gateway (progress is in Grafana: PaymentLab / Load (k6) and Overview)." -ForegroundColor Cyan
    Write-Host 'The summary is printed when the test ends. Ctrl+C stops the test early.' -ForegroundColor DarkGray
    $envArgs = Get-EnvArgs
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & docker compose @envArgs --profile load run --rm -T @envs k6 run --quiet -o opentelemetry `
            --summary-export "/load/results/$summary" main.js 2>&1 | ForEach-Object { "$_" }
    } finally { $ErrorActionPreference = $prev }
    switch ($LASTEXITCODE) {
        0 { Write-Host "All thresholds passed. Summary: load/results/$summary" -ForegroundColor Green }
        99 { Write-Host "Test finished, but some thresholds were crossed (see above). Summary: load/results/$summary" -ForegroundColor Yellow }
        default { throw "k6 exited with code $LASTEXITCODE" }
    }
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
            Write-Host ("  {0,-11} {1}" -f $k, $(if ($ok) { 'ready' } else { 'NOT READY' })) -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' })
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
    'seed' {
        Test-Docker
        $scale = if ($Rest -and $Rest.Count -ge 1) { $Rest[0].ToLower() } else { 'small' }
        Invoke-Seed $scale
    }
    'load' {
        Test-Docker
        Invoke-Load $Rest
    }
    { $_ -in @('incident', 'hint', 'reveal', 'resolve') } {
        Test-Docker
        # also accept the --id N / --difficulty X spelling
        for ($i = 0; $Rest -and $i -lt $Rest.Count - 1; $i++) {
            if ($Rest[$i] -eq '--id') { $Id = [int]$Rest[$i + 1] }
            if ($Rest[$i] -eq '--difficulty') { $Difficulty = $Rest[$i + 1] }
        }
        $gm = @{ Command = $Command; Id = $Id; Difficulty = $Difficulty }
        if ($EnvFile) { $gm.EnvFile = $EnvFile }
        & (Join-Path $Root 'chaos/gm.ps1') @gm
    }
    'urls' { Show-Urls }
    default { Get-Help $PSCommandPath -Detailed | Out-Host }
}
