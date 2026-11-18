# Helpers for the game master (dot-sourced by gm.ps1). Windows PowerShell 5.1 and PowerShell 7.
# Expects $Root (repository root) and $EnvFile (optional extra env file) in the caller's scope.

$script:Toxiproxy = 'http://localhost:8474'
$script:FakeBank = 'http://localhost:8090'
$script:RabbitApi = 'http://localhost:15672/api'
$script:RabbitAuth = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes('lab:lab'))
$script:Prometheus = 'http://localhost:9090'
$script:PaymentsApi = 'http://localhost:8081'

function Get-EnvArgs {
    $a = @('--env-file', (Join-Path $Root '.env'))
    if ($EnvFile) { $a += @('--env-file', (Resolve-Path $EnvFile).Path) }
    return $a
}

# Value of a variable from .env (and the optional extra env file, which wins).
function Get-EnvValue([string]$Name) {
    $files = @(Join-Path $Root '.env')
    if ($EnvFile) { $files += (Resolve-Path $EnvFile).Path }
    $value = $null
    foreach ($f in $files) {
        foreach ($line in Get-Content $f) {
            if ($line -match "^\s*$Name\s*=\s*([^#\s]*)") { $value = $Matches[1] }
        }
    }
    return $value
}

# Runs a native command without letting stderr abort the script; output as strings, exit code in $script:ExitCode.
function Invoke-Native([string]$Exe, [string[]]$Arguments) {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $out = & $Exe @Arguments 2>&1 | ForEach-Object { "$_" } }
    finally { $ErrorActionPreference = $prev }
    $script:ExitCode = $LASTEXITCODE
    return $out
}

function Invoke-Dc([string[]]$Arguments) {
    return Invoke-Native 'docker' (@('compose') + (Get-EnvArgs) + $Arguments)
}

function Invoke-Sql([string]$Service, [string]$Db, [string]$Sql) {
    return Invoke-Dc @('exec', '-T', $Service, 'psql', '-U', $Db, '-d', $Db, '-At', '-c', $Sql)
}

function Get-ContainerId([string]$Service) {
    return (@(Invoke-Dc @('ps', '-q', $Service)) | Where-Object { $_ -match '^[0-9a-f]{12,}$' } | Select-Object -First 1)
}

function Invoke-Api([string]$Method, [string]$Url, $Body = $null, [hashtable]$Headers = @{}) {
    # Toxiproxy refuses browser-like user agents (Windows PowerShell's default starts with "Mozilla") with 403.
    $p = @{ Method = $Method; Uri = $Url; Headers = $Headers; TimeoutSec = 15; UseBasicParsing = $true; UserAgent = 'paymentlab-gm' }
    if ($null -ne $Body) { $p.Body = ($Body | ConvertTo-Json -Depth 5 -Compress); $p.ContentType = 'application/json' }
    return Invoke-RestMethod @p
}

# ---- toxiproxy
function Add-Toxic([string]$Proxy, [string]$Type, [hashtable]$Attributes, [string]$Stream = 'downstream', [double]$Toxicity = 1.0) {
    Invoke-Api POST "$Toxiproxy/proxies/$Proxy/toxics" @{ name = "t-$Type-$Stream"; type = $Type; stream = $Stream; toxicity = $Toxicity; attributes = $Attributes } | Out-Null
}
function Reset-Toxiproxy { try { Invoke-Api POST "$Toxiproxy/reset" | Out-Null } catch { } }

# ---- fake bank
function Get-BankConfig { return Invoke-Api GET "$FakeBank/admin/config" }
function Set-BankConfig($Config) { Invoke-Api POST "$FakeBank/admin/config" $Config | Out-Null }
function Reset-BankConfig {
    Set-BankConfig @{
        latencyMs = [int](Get-EnvValue 'FAKEBANK_LATENCY_MS'); jitterMs = [int](Get-EnvValue 'FAKEBANK_JITTER_MS')
        rejectRate = [double](Get-EnvValue 'FAKEBANK_REJECT_RATE'); errorRate = 0.0; slowRate = 0.0; slowMs = 10000
        maxRequestsPerSecond = 0
    }
}

# ---- rabbitmq management API
function Set-RabbitPolicy([string]$Name, [string]$Pattern, [hashtable]$Definition, [int]$Priority = 10) {
    Invoke-Api PUT "$RabbitApi/policies/%2F/$Name" @{ pattern = $Pattern; definition = $Definition; priority = $Priority; 'apply-to' = 'queues' } @{ Authorization = $RabbitAuth } | Out-Null
}
function Remove-ResultsBinding {
    Invoke-Api DELETE "$RabbitApi/bindings/%2F/e/payments.events/q/payments.bank-results/bank.transfer.result" $null @{ Authorization = $RabbitAuth } | Out-Null
}
# Idempotent: re-creating an existing binding is a no-op.
function Restore-ResultsBinding {
    try { Invoke-Api POST "$RabbitApi/bindings/%2F/e/payments.events/q/payments.bank-results" @{ routing_key = 'bank.transfer.result' } @{ Authorization = $RabbitAuth } | Out-Null } catch { }
}
function Remove-RabbitPolicies {
    try {
        foreach ($p in @(Invoke-Api GET "$RabbitApi/policies" $null @{ Authorization = $RabbitAuth })) {
            if ($p.name) { Invoke-Api DELETE "$RabbitApi/policies/%2F/$($p.name)" $null @{ Authorization = $RabbitAuth } | Out-Null }
        }
    } catch { }
}

# ---- prometheus
function Get-Prom([string]$Query) {
    try {
        $r = Invoke-RestMethod -Uri "$Prometheus/api/v1/query?query=$([uri]::EscapeDataString($Query))" -TimeoutSec 15 -UseBasicParsing
        $v = @($r.data.result)
        if ($v.Count -eq 0) { return $null }
        $d = 0.0
        if ([double]::TryParse($v[0].value[1], [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$d)) { return $d }
    } catch { }
    return $null
}

# ---- background traffic (k6 containers named paymentlab-traffic-*)
function Get-SeededAccounts {
    $out = Invoke-Sql 'wallet-db' 'wallet' "SELECT CASE WHEN to_regclass('lab_seed_info') IS NULL THEN 0 ELSE (SELECT coalesce(max(accounts), 0) FROM lab_seed_info) END"
    $n = 0
    if ($script:ExitCode -eq 0 -and [int]::TryParse((@($out)[-1]).Trim(), [ref]$n)) { return $n }
    return 0
}

function Get-SeedScale {
    $out = Invoke-Sql 'wallet-db' 'wallet' "SELECT CASE WHEN to_regclass('lab_seed_info') IS NULL THEN '' ELSE (SELECT coalesce(max(scale), '') FROM lab_seed_info) END"
    if ($script:ExitCode -eq 0) { return (@($out)[-1]).Trim() }
    return ''
}

# Only the simulated customers (main.js) report client-side metrics; other traffic is "someone else's" client.
function Start-Traffic([string]$Name, [string]$Script = 'main.js', [hashtable]$Env = @{}, [string]$Memory = $null) {
    $output = @()
    if ($Script -eq 'main.js') { $output = @('-o', 'opentelemetry') }
    $envArgs = @()
    $all = @{ SEEDED_ACCOUNTS = (Get-SeededAccounts) }
    foreach ($k in $Env.Keys) { $all[$k] = $Env[$k] }
    foreach ($k in $all.Keys) { $envArgs += @('-e', "$k=$($all[$k])") }
    $prevMem = $env:K6_MEM
    if ($Memory) { $env:K6_MEM = $Memory }
    try {
        Invoke-Native 'docker' (@('rm', '-f', "paymentlab-$Name")) | Out-Null
        $out = Invoke-Dc (@('--profile', 'load', 'run', '-d', '--rm', '--name', "paymentlab-$Name") + $envArgs + @('k6', 'run', '--quiet') + $output + @($Script))
        if ($script:ExitCode -ne 0) { throw "could not start background traffic: $($out -join ' ')" }
    } finally { $env:K6_MEM = $prevMem }
}

function Stop-Traffic {
    $ids = Invoke-Native 'docker' @('ps', '-aq', '--filter', 'name=paymentlab-traffic')
    foreach ($id in @($ids)) { if ($id -match '^[0-9a-f]+$') { Invoke-Native 'docker' @('rm', '-f', $id) | Out-Null } }
}

# ---- container resources
function Get-Limits([string]$Service) {
    $id = Get-ContainerId $Service
    $v = (Invoke-Native 'docker' @('inspect', '-f', '{{.HostConfig.NanoCpus}} {{.HostConfig.Memory}} {{.HostConfig.MemorySwap}}', $id)) -split ' '
    return @{ Id = $id; NanoCpus = [long]$v[0]; Memory = [long]$v[1]; MemorySwap = [long]$v[2] }
}

function Restore-Limits($Saved) {
    if (-not $Saved) { return }
    $cpus = ([double]$Saved.NanoCpus / 1e9).ToString([Globalization.CultureInfo]::InvariantCulture)
    Invoke-Native 'docker' @('update', '--cpus', $cpus, '--memory', "$($Saved.Memory)", '--memory-swap', "$($Saved.MemorySwap)", $Saved.Id) | Out-Null
}

# ---- recovery helpers
function Test-Url([string]$Url) {
    try { return (Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 3).StatusCode -lt 400 } catch { return $false }
}

function Wait-AppHealthy([int]$TimeoutSeconds = 240) {
    $urls = @('http://localhost:8080/healthz', 'http://localhost:8081/ready', 'http://localhost:8082/ready', 'http://localhost:8085/ready', 'http://localhost:8090/healthz')
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $pending = @($urls | Where-Object { -not (Test-Url $_) })
        if ($pending.Count -eq 0) { return $true }
        Start-Sleep -Seconds 3
    } while ((Get-Date) -lt $deadline)
    return $false
}

# Payments parked for an operator during the incident are resumed (POST /admin/sagas/{id}/retry).
function Resume-ParkedPayments {
    $n = 0
    for ($round = 0; $round -lt 20; $round++) {
        try { $parked = @(Invoke-Api GET "$PaymentsApi/admin/sagas?state=RequiresAttention&limit=500") } catch { break }
        if ($parked.Count -eq 0) { break }
        foreach ($s in $parked) {
            try { Invoke-Api POST "$PaymentsApi/admin/sagas/$($s.paymentId)/retry" | Out-Null; $n++ } catch { }
        }
        if ($parked.Count -lt 500) { break }
    }
    return $n
}
