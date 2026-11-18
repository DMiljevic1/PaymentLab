<#
  SPOILERS. Runs scenarios one after another: inject -> wait until the symptom is visible in
  Prometheus / the database -> resolve -> smoke test. Used to check the chaos framework itself.

    ./chaos/verify.ps1              # all scenarios
    ./chaos/verify.ps1 -Ids 1,7,9   # some
#>
param([int[]]$Ids, [string]$EnvFile, [int]$TimeoutSeconds = 240)

$ErrorActionPreference = 'Stop'
$Root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'lib.ps1')
$Scenarios = . (Join-Path $PSScriptRoot 'scenarios.ps1')
if (-not $Ids) { $Ids = $Scenarios | ForEach-Object { $_.Id } }
$gm = Join-Path $PSScriptRoot 'gm.ps1'
$log = Join-Path $PSScriptRoot ".state/verify-$(Get-Date -Format 'yyyyMMdd-HHmmss').log"
New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null
$gmArgs = @{ Quiet = $true }
if ($EnvFile) { $gmArgs.EnvFile = $EnvFile }

$results = foreach ($id in $Ids) {
    $s = $Scenarios | Where-Object { $_.Id -eq $id }
    $started = Get-Date
    $visible = $false; $recovered = $false; $note = ''
    try {
        & $gm -Command incident -Id $id @gmArgs
        Start-Sleep -Seconds $s.SettleSeconds
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        do {
            if (& $s.Check) { $visible = $true; break }
            Start-Sleep -Seconds 10
        } while ((Get-Date) -lt $deadline)
    } catch { $note = "inject: $($_.Exception.Message)" }
    try { & $gm -Command resolve @gmArgs; $recovered = $true } catch { $note += " resolve: $($_.Exception.Message)" }
    $r = [pscustomobject]@{
        Id = $id; Title = $s.Title; Symptom = $visible; Recovered = $recovered
        Minutes = [math]::Round(((Get-Date) - $started).TotalMinutes, 1); Note = $note.Trim()
    }
    $line = '{0,2} {1,-50} symptom={2,-5} recovered={3,-5} {4,5} min {5}' -f $r.Id, $r.Title, $r.Symptom, $r.Recovered, $r.Minutes, $r.Note
    Write-Host $line
    Add-Content -Path $log -Value $line
    $r
}
$ok = @($results | Where-Object { $_.Symptom -and $_.Recovered }).Count
Write-Host "$ok / $(@($results).Count) scenarios produced their symptom and recovered. Log: $log"
