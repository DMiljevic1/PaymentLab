<#
.SYNOPSIS
    End-to-end smoke test: creates accounts and payments through the gateway and checks that every payment
    reaches a final state and that balances add up.
#>
[CmdletBinding()]
param(
    [string]$Gateway = 'http://localhost:8080',
    [int]$ExternalCount = 5,
    [int]$InternalCount = 3,
    [int]$TimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'
$failures = New-Object System.Collections.Generic.List[string]
$runId = [guid]::NewGuid().ToString('N').Substring(0, 8)
$headers = @{ 'X-Client-Id' = "smoke-$runId" }

function Invoke-Api {
    param([string]$Method, [string]$Path, $Body, [hashtable]$ExtraHeaders = @{})
    $h = $headers.Clone()
    foreach ($k in $ExtraHeaders.Keys) { $h[$k] = $ExtraHeaders[$k] }
    $params = @{ Method = $Method; Uri = "$Gateway$Path"; Headers = $h; UseBasicParsing = $true; TimeoutSec = 15 }
    if ($null -ne $Body) {
        $params.Body = ($Body | ConvertTo-Json -Depth 5)
        $params.ContentType = 'application/json'
    }
    try {
        $r = Invoke-WebRequest @params
        return [pscustomobject]@{ Status = [int]$r.StatusCode; Body = $(if ($r.Content) { $r.Content | ConvertFrom-Json } else { $null }) }
    } catch {
        $resp = $_.Exception.Response
        if ($null -eq $resp) { throw }
        $status = [int]$resp.StatusCode
        $text = $null
        try { $text = $_.ErrorDetails.Message } catch { }
        return [pscustomobject]@{ Status = $status; Body = $(if ($text) { try { $text | ConvertFrom-Json } catch { $text } } else { $null }) }
    }
}

function Check([bool]$Condition, [string]$Message) {
    if ($Condition) { Write-Host "  [ok]   $Message" -ForegroundColor Green }
    else { Write-Host "  [FAIL] $Message" -ForegroundColor Red; $failures.Add($Message) }
}

function New-Account([string]$Name, [long]$Balance) {
    $r = Invoke-Api POST '/api/accounts' @{ ownerName = $Name; currency = 'EUR'; initialBalanceMinor = $Balance }
    if ($r.Status -ne 201) { throw "Creating account $Name failed: HTTP $($r.Status) $($r.Body | ConvertTo-Json -Compress)" }
    return $r.Body
}

function New-Payment([hashtable]$Body, [string]$Key) {
    if (-not $Key) { $Key = [guid]::NewGuid().ToString() }
    $r = Invoke-Api POST '/api/payments' $Body @{ 'Idempotency-Key' = $Key }
    if ($r.Status -notin 200, 202) { throw "Creating payment failed: HTTP $($r.Status) $($r.Body | ConvertTo-Json -Compress)" }
    return [pscustomobject]@{ Key = $Key; Payment = $r.Body; Status = $r.Status }
}

Write-Host "Smoke test $runId against $Gateway" -ForegroundColor Cyan

# ---------------------------------------------------------------- setup
$alice = New-Account "smoke-$runId-alice" 100000   # 1000.00 EUR
$bob = New-Account "smoke-$runId-bob" 0
$carol = New-Account "smoke-$runId-carol" 500      # 5.00 EUR
Write-Host "  accounts: alice=$($alice.id) bob=$($bob.id) carol=$($carol.id)"

$expect = @{}   # paymentId -> expected final status ('Completed' | 'Failed' | 'Rejected' | 'CompletedOrBankRejected')
$amounts = @{}
$created = @()

# External payments that should succeed (the fake bank also rejects a small random share: AG01).
for ($i = 1; $i -le $ExternalCount; $i++) {
    $p = New-Payment @{ type = 'External'; sourceAccountId = $alice.id; creditorIban = 'HR1210010051863000160'; amountMinor = 1000; currency = 'EUR'; reference = "smoke $runId ext $i" }
    $expect[$p.Payment.id] = 'CompletedOrBankRejected'; $amounts[$p.Payment.id] = 1000; $created += $p
}
# External payment the bank always rejects (IBAN ending in 9999) -> compensated -> Failed
$p = New-Payment @{ type = 'External'; sourceAccountId = $alice.id; creditorIban = 'HR1210010051863009999'; amountMinor = 1500; currency = 'EUR'; reference = "smoke $runId bank reject" }
$expect[$p.Payment.id] = 'Failed'; $amounts[$p.Payment.id] = 1500; $created += $p
# Internal payments alice -> bob
for ($i = 1; $i -le $InternalCount; $i++) {
    $p = New-Payment @{ type = 'Internal'; sourceAccountId = $alice.id; destinationAccountId = $bob.id; amountMinor = 2000; currency = 'EUR'; reference = "smoke $runId int $i" }
    $expect[$p.Payment.id] = 'Completed'; $amounts[$p.Payment.id] = 2000; $created += $p
}
# Internal payment with insufficient funds -> Rejected
$p = New-Payment @{ type = 'Internal'; sourceAccountId = $carol.id; destinationAccountId = $bob.id; amountMinor = 10000; currency = 'EUR'; reference = "smoke $runId nsf" }
$expect[$p.Payment.id] = 'Rejected'; $amounts[$p.Payment.id] = 10000; $created += $p

Write-Host "  created $($created.Count) payments"

# Idempotency: same key + same body returns the same payment
$first = $created[0]
$body = @{ type = 'External'; sourceAccountId = $alice.id; creditorIban = 'HR1210010051863000160'; amountMinor = 1000; currency = 'EUR'; reference = "smoke $runId ext 1" }
$replay = New-Payment $body $first.Key
Check ($replay.Payment.id -eq $first.Payment.id) 'idempotent replay returns the same payment id'
$conflict = Invoke-Api POST '/api/payments' (@{ type = 'External'; sourceAccountId = $alice.id; creditorIban = 'HR1210010051863000160'; amountMinor = 999; currency = 'EUR' }) @{ 'Idempotency-Key' = $first.Key }
Check ($conflict.Status -eq 422) "reusing a key with a different body is refused (HTTP $($conflict.Status))"

# ---------------------------------------------------------------- wait for final states
$final = @{}
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ($final.Count -lt $expect.Count -and (Get-Date) -lt $deadline) {
    foreach ($id in @($expect.Keys)) {
        if ($final.ContainsKey($id)) { continue }
        $r = Invoke-Api GET "/api/payments/$id"
        if ($r.Status -eq 200 -and $r.Body.status -in 'Completed', 'Failed', 'Rejected', 'UnderReview') { $final[$id] = $r.Body }
    }
    if ($final.Count -lt $expect.Count) { Start-Sleep -Milliseconds 500 }
}

Write-Host ''
Write-Host 'Payments:' -ForegroundColor Cyan
foreach ($id in $expect.Keys) {
    $want = $expect[$id]
    if (-not $final.ContainsKey($id)) { Check $false "payment $id reached a final state (expected $want)"; continue }
    $got = $final[$id].status
    $ok = if ($want -eq 'CompletedOrBankRejected') { $got -eq 'Completed' -or ($got -eq 'Failed' -and $final[$id].failureReason -like '*AG01*') } else { $got -eq $want }
    $detail = if ($final[$id].failureReason) { " ($($final[$id].failureReason))" } else { '' }
    Check $ok "payment $id -> $got$detail, expected $want"
}

# ---------------------------------------------------------------- balances
$completedOut = 0L; $completedInternal = 0L
foreach ($id in $final.Keys) {
    if ($final[$id].status -ne 'Completed') { continue }
    if ($final[$id].sourceAccountId -eq $alice.id) { $completedOut += $amounts[$id] }
    if ($final[$id].type -eq 'Internal' -and $final[$id].destinationAccountId -eq $bob.id) { $completedInternal += $amounts[$id] }
}
$a = (Invoke-Api GET "/api/accounts/$($alice.id)").Body
$b = (Invoke-Api GET "/api/accounts/$($bob.id)").Body
$c = (Invoke-Api GET "/api/accounts/$($carol.id)").Body
Write-Host ''
Write-Host 'Balances:' -ForegroundColor Cyan
Check ($a.balanceMinor -eq (100000 - $completedOut)) "alice balance $($a.balanceMinor) = 100000 - $completedOut"
Check ($a.reservedMinor -eq 0) "alice has no reservations left ($($a.reservedMinor))"
Check ($b.balanceMinor -eq $completedInternal) "bob balance $($b.balanceMinor) = $completedInternal"
Check ($c.balanceMinor -eq 500 -and $c.reservedMinor -eq 0) "carol untouched ($($c.balanceMinor) / reserved $($c.reservedMinor))"

Write-Host ''
if ($failures.Count -eq 0) {
    Write-Host "SMOKE TEST PASSED ($($expect.Count) payments)" -ForegroundColor Green
    exit 0
}
Write-Host "SMOKE TEST FAILED: $($failures.Count) check(s) failed" -ForegroundColor Red
exit 1
