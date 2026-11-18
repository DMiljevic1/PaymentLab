# SPOILERS. Scenario catalogue for the game master (dot-sourced by gm.ps1, returns an array).
#
# Fields: Id, Title, Category, Difficulty, Severity, Load (background payments/s, 0 = none),
#         Ticket (what support / alerting reports; {0} = start time), Hints (3, progressively more specific),
#         RootCause, Explanation, Fix, Inject / Resolve (scriptblocks, $State is a hashtable persisted in
#         chaos/.state), Check (scriptblock, $true once the symptom is visible; used by chaos/verify.ps1),
#         SettleSeconds (how long verify.ps1 waits before it starts checking).

@(
    # ------------------------------------------------------------------------------------------ 1
    @{
        Id = 1; Title = 'Slow network path to the payments database'; Category = 'databases'; Difficulty = 'easy'; Severity = 'SEV-2'; Load = 20
        Ticket = @'
Alert "PaymentsApiLatencyHigh" fired at {0}: p95 latency of POST /api/payments is above 1 s (normal: ~30 ms).
Customer support: the app spins for a long time when customers submit a payment, and payments take
much longer than usual to be confirmed. No deployment today.
Impact: all customers, all payment types.
'@
        Hints = @(
            'Compare latency across services (Overview -> p95 latency). Which service is slow on its own, and which are only slow because they wait for it?',
            'Open a slow POST /payments trace in Tempo and look at where the time goes inside the Payments spans.',
            'Every database span of the Payments service is slow, while the Postgres dashboard shows an idle, fast database. Look at what sits between the service and its database (toxiproxy, port 15432).'
        )
        RootCause = 'Network latency (~150 ms per round trip) injected on the path between the Payments service and payments-db (Toxiproxy proxy "payments-db").'
        Explanation = 'Every SQL round trip pays the extra latency. A request or saga run does several round trips, so latency multiplies; connections are held longer, the Npgsql pool gets busier and the saga backlog grows. The database itself is healthy: server-side statement times stay low while client-side command duration jumps.'
        Fix = 'Remove the latency (network/infra). In code: fewer round trips per operation, sensible command timeouts and alerting on client-side DB latency vs. server-side time.'
        Inject = { param($State) Add-Toxic 'payments-db' 'latency' @{ latency = 150; jitter = 30 } }
        Resolve = { param($State) Reset-Toxiproxy }
        Check = { $v = Get-Prom 'histogram_quantile(0.95, sum by (le) (rate(db_client_operation_duration_seconds_bucket{job="payments"}[1m])))'; $v -and $v -gt 0.1 }
        SettleSeconds = 60
    }

    # ------------------------------------------------------------------------------------------ 2
    @{
        Id = 2; Title = 'Forgotten transaction holding row locks'; Category = 'databases'; Difficulty = 'medium'; Severity = 'SEV-2'; Load = 20
        Ticket = @'
Customer support, {0}: some customers (not all) cannot pay. Their payments stay "Pending" for a long
time and then end as "Failed" or "Under review". Other customers pay normally. The affected customers
are consistent: if a customer is affected, all of their payments are.
Impact: roughly one in five customers.
'@
        Hints = @(
            'The impact is partial and sticky per customer. Find which saga step the affected payments are stuck in and what the affected accounts have in common.',
            'Look at the Wallet service: reserve/commit latency, database spans in a slow trace, and the Postgres dashboard for the wallet database (blocked sessions, oldest transaction).',
            'Something other than the Wallet service holds row locks on accounts for a very long time. pg_stat_activity (application_name, state, xact_start) and pg_blocking_pids() show who.'
        )
        RootCause = 'A session with application_name "nightly-report" opened a transaction, locked ~20 % of the account rows with SELECT ... FOR UPDATE and stayed "idle in transaction".'
        Explanation = 'Reservations and commits update the account row; for the locked accounts those statements wait for the lock until the Wallet HTTP call times out (3 s). The saga retries, exhausts its attempts, tries to compensate (which needs the same lock) and parks the payment for an operator.'
        Fix = 'Terminate the session (pg_terminate_backend). Prevent it: idle_in_transaction_session_timeout / statement and lock timeouts for reporting roles, reporting on a replica, alert on oldest transaction age and blocked sessions.'
        Inject = { param($State) Invoke-Dc @('exec', '-d', 'wallet-db', 'sh', '/opt/lab/hold.sh') | Out-Null }
        Resolve = {
            param($State)
            Invoke-Sql 'wallet-db' 'wallet' "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name = 'nightly-report'" | Out-Null
            Invoke-Dc @('exec', '-T', 'wallet-db', 'pkill', '-f', 'sleep 86400') | Out-Null
        }
        Check = { $v = Get-Prom 'max(pg_oldest_transaction_age_seconds{db="wallet"})'; $b = Get-Prom 'max_over_time(sum(pg_blocked_sessions{db="wallet"})[2m:])'; ($v -and $v -gt 30) -and ($b -and $b -gt 0) }
        SettleSeconds = 60
    }

    # ------------------------------------------------------------------------------------------ 3
    @{
        Id = 3; Title = 'Per-role connection limit on the payments database'; Category = 'configuration'; Difficulty = 'medium'; Severity = 'SEV-2'; Load = 20
        Ticket = @'
Alert "PaymentsHttp5xx" fired at {0}: more than 2 % of Payments API requests return HTTP 500.
Some payment submissions fail with an error, retries sometimes succeed. Pending payments complete
slowly. The database team finished a "role and permission clean-up" earlier today; they say the
database server is healthy (CPU and memory normal).
Impact: intermittent errors for all customers.
'@
        Hints = @(
            'Read the exact exception of a failing request in the Payments logs (Loki, level error) or in the trace.',
            'Compare how many connections the Payments pool tries to use (Payments -> database client) with the sessions Postgres actually has for that application.',
            'The limit is neither Max Pool Size nor max_connections: Postgres can also cap connections per role. Check pg_roles.rolconnlimit.'
        )
        RootCause = 'ALTER ROLE payments CONNECTION LIMIT 8 - the role may hold only 8 sessions while the service pool and its workers need more.'
        Explanation = 'Whenever the Npgsql pool needs a new physical connection beyond 8, Postgres refuses it with 53300 "too many connections for role". API requests fail with 500, saga steps fail and are retried, the backlog grows. The server looks healthy because the refusal is cheap.'
        Fix = 'ALTER ROLE payments CONNECTION LIMIT -1 (or a value above pool size x instances). Size Max Pool Size, role limits and max_connections together; alert on connection errors.'
        Inject = {
            param($State)
            Invoke-Sql 'payments-db' 'payments' 'ALTER ROLE payments CONNECTION LIMIT 8' | Out-Null
            Invoke-Sql 'payments-db' 'payments' "SELECT count(pg_terminate_backend(pid)) FROM pg_stat_activity WHERE application_name = 'payments'" | Out-Null
        }
        Resolve = { param($State) Invoke-Sql 'payments-db' 'payments' 'ALTER ROLE payments CONNECTION LIMIT -1' | Out-Null }
        Check = { $v = Get-Prom 'sum(increase(dotnet_exceptions_total{job="payments"}[2m]))'; $v -and $v -gt 20 }
        SettleSeconds = 60
    }

    # ------------------------------------------------------------------------------------------ 4
    @{
        Id = 4; Title = 'Historical data import (volume)'; Category = 'data volume'; Difficulty = 'hard'; Severity = 'SEV-2'; Load = 25
        Ticket = @'
Since the overnight import of historical payments into the platform (finished {0}), payment
processing is sluggish: new payments stay "Pending" for many seconds before they complete, and the
payments database runs hot even when traffic is moderate. Nothing was deployed; the import itself
completed without errors.
Impact: all customers; processing time up from <1 s to several seconds.
'@
        Hints = @(
            'Look at what the payments database spends its time on (Postgres -> top statements by total time) and at the saga panels on the Payments dashboard.',
            'One statement runs constantly (also when there is little traffic) and its cost grows with the size of a table. Run EXPLAIN (ANALYZE, BUFFERS) on it.',
            'The saga claim query ORs the due-time condition with a subquery on the inbox. With the OR the planner cannot use the partial index on next_attempt_at and scans all payment_sagas rows on every poll.'
        )
        RootCause = 'Weakness: SagaProcessor.ClaimDueAsync uses "next_attempt_at <= now() OR payment_id IN (SELECT ... inbox ...)". The OR defeats the partial index, so every poll (2x per second, plus every batch) is a sequential scan of payment_sagas, whose size grows with history.'
        Explanation = 'With little data the scan is cheap and invisible. With millions of finished sagas each poll reads the whole table: high DB CPU, slow claims, fewer saga runs per second, a growing "Created"/"AwaitingBank" backlog and higher end-to-end latency.'
        Fix = 'Split the query into two index-friendly parts (UNION of "due" via the partial index and "has unprocessed inbox" via the inbox index), or index only non-final sagas; consider archiving finished sagas. Verify with EXPLAIN and the claim-duration panel.'
        Inject = {
            param($State)
            $State.previousScale = Get-SeedScale
            & (Join-Path $Root 'lab.ps1') seed large -Force -EnvFile:$EnvFile *> $null
        }
        Resolve = {
            param($State)
            $scale = $State.previousScale
            if (-not $scale) { $scale = 'small' }
            & (Join-Path $Root 'lab.ps1') seed $scale -Force -EnvFile:$EnvFile *> $null
        }
        Check = { $v = Get-Prom 'histogram_quantile(0.95, sum by (le) (rate(saga_claim_duration_seconds_bucket[1m])))'; $v -and $v -gt 0.2 }
        SettleSeconds = 60
    }

    # ------------------------------------------------------------------------------------------ 5
    @{
        Id = 5; Title = 'Back-office list polling'; Category = 'data volume'; Difficulty = 'hard'; Severity = 'SEV-2'; Load = 20
        Ticket = @'
Alert "PaymentsDbCpuHigh" fired at {0}: payments-db CPU above 90 % of its limit.
Customers report that the app is slow and that payment confirmations take long. Support adds that
their own back-office screens are "very slow today" as well. Traffic from customers is normal.
Impact: all customers; latency up across the payments API.
'@
        Hints = @(
            'The database is busy but customer traffic is normal. Find which statements use the CPU (Postgres -> top statements, sequential scans per table).',
            'Find the HTTP endpoint that issues the expensive statement (Tempo: search spans by statement, or the gateway routes) and who calls it, how often.',
            'Listing payments by status (or with no account filter) has no supporting index and uses OFFSET paging joined to the saga table: every request scans and sorts the payments table.'
        )
        RootCause = 'Weakness: GET /payments?status=... (and without accountId) has no index for the filter/sort and pages with OFFSET, joining payment_sagas. A back-office screen polls it every second from several browsers.'
        Explanation = 'Each call reads and sorts (a large part of) payments + payment_sagas. A handful of pollers is enough to saturate the database CPU, which slows every other query: payment creation, saga steps, status polling.'
        Fix = 'Index for the access path (e.g. (status, created_at DESC)), keyset pagination instead of OFFSET, require a filter / cap page depth, project only the needed columns, serve back-office from a replica or a read model, and rate-limit the screen.'
        Inject = {
            param($State)
            $State.previousScale = Get-SeedScale
            if ((Get-SeededAccounts) -lt 100000) { & (Join-Path $Root 'lab.ps1') seed medium -Force -EnvFile:$EnvFile *> $null; $State.reseeded = $true }
            Start-Traffic 'traffic-2' '/extra/traffic-a.js' @{ VUS = 6 } '256m'
        }
        Resolve = {
            param($State)
            Stop-Traffic
            if ($State.reseeded) {
                $scale = $State.previousScale
                if (-not $scale) { $scale = 'small' }
                & (Join-Path $Root 'lab.ps1') seed $scale -Force -EnvFile:$EnvFile *> $null
            }
        }
        Check = { $v = Get-Prom 'sum(rate(pg_stat_user_tables_seq_tup_read{db="payments",relname="payments"}[1m]))'; $v -and $v -gt 100000 }
        SettleSeconds = 60
    }

    # ------------------------------------------------------------------------------------------ 6
    @{
        Id = 6; Title = 'Latency between Payments and the broker'; Category = 'message broker'; Difficulty = 'hard'; Severity = 'SEV-2'; Load = 20
        Ticket = @'
Customer support, {0}: outgoing (external) payments stay "Pending" far longer than usual - several
minutes - and some end up "Under review". Internal transfers between our own customers are fine.
The bank's status page shows all systems operational; RabbitMQ is up and has no alarms.
Impact: all external payments delayed.
'@
        Hints = @(
            'Follow one delayed external payment: which saga state does it wait in, and did the command ever reach the connector (connector consume span)?',
            'Look at the outbox panels on the Payments dashboard (backlog, oldest pending row, publish lag) and at the oldest open transaction of the payments database.',
            'The outbox relay publishes one message at a time and waits for each broker confirm while it keeps its database transaction open. A little extra latency on the Payments -> RabbitMQ connection caps its throughput below the incoming rate.'
        )
        RootCause = 'Network latency (~150 ms) on the Payments <-> RabbitMQ path (Toxiproxy "rabbitmq-payments") combined with a weakness: OutboxRelay publishes sequentially and awaits every publisher confirm inside an open DB transaction.'
        Explanation = 'Throughput of the relay is ~1 / round-trip time (~6 messages/s), below the rate of bank commands. The outbox backlog grows without bound, commands reach the bank minutes late, the saga times out and adds status inquiries to the same backlog, and some sagas give up and park. Meanwhile the relay transaction stays open for seconds ("idle in transaction").'
        Fix = 'Publish the batch and await confirms together (pipelining / batch confirms), keep the DB transaction short (claim rows, commit, publish, then mark published), scale the relay; alert on outbox age. Remove the network latency.'
        Inject = { param($State) Add-Toxic 'rabbitmq-payments' 'latency' @{ latency = 150; jitter = 30 } }
        Resolve = { param($State) Reset-Toxiproxy }
        Check = { $v = Get-Prom 'max(outbox_pending)'; $v -and $v -gt 50 }
        SettleSeconds = 90
    }

    # ------------------------------------------------------------------------------------------ 7
    @{
        Id = 7; Title = 'Frozen consumer'; Category = 'consumers and workers'; Difficulty = 'easy'; Severity = 'SEV-1'; Load = 15
        Ticket = @'
Customer support, {0}: no outgoing (external) payment has completed for a while. Customers see
"Pending" and, after a minute or two, "Under review". Internal transfers work normally.
Impact: 100 % of external payments.
'@
        Hints = @(
            'Where do external payments stop? Check the queues on the RabbitMQ dashboard.',
            'Messages pile up in one queue. Who is supposed to consume it, does it have consumers, and is that process alive (Containers, ./lab.ps1 status)?',
            'The connector container is paused (docker pause): the process exists but gets no CPU, so it consumes nothing and RabbitMQ drops its connection after the heartbeat timeout.'
        )
        RootCause = 'The connector container was paused (docker pause).'
        Explanation = 'Commands accumulate in connector.bank-transfers with zero consumers. The saga times out after 30 s, asks for the bank status (which also needs the connector), and after the inquiries are exhausted parks the payment in RequiresAttention.'
        Fix = 'docker unpause; then resume the parked payments (POST /admin/sagas/{id}/retry). Alert on consumers = 0 and on queue age, not only on process liveness.'
        Inject = { param($State) Invoke-Native 'docker' @('pause', (Get-ContainerId 'connector')) | Out-Null }
        Resolve = { param($State) Invoke-Native 'docker' @('unpause', (Get-ContainerId 'connector')) | Out-Null }
        Check = { $v = Get-Prom 'sum(rabbitmq_queue_messages_ready{queue="connector.bank-transfers"})'; $v -and $v -gt 20 }
        SettleSeconds = 45
    }

    # ------------------------------------------------------------------------------------------ 8
    @{
        Id = 8; Title = 'Results no longer routed'; Category = 'message broker'; Difficulty = 'medium'; Severity = 'SEV-1'; Load = 15
        Ticket = @'
Customer support, {0}: external payments are stuck in "Pending" and later end "Under review". The
bank confirms that it received and executed these transfers. RabbitMQ health checks are green, there
are no alarms, and all services report ready. The platform team did a "clean-up of unused RabbitMQ
objects" this week.
Impact: external payments are executed at the bank but never confirmed to customers.
'@
        Hints = @(
            'The bank executed the transfers, so the problem is on the way back. Follow the result message from the connector to Payments.',
            'The connector reports every result as published and confirmed, yet Payments consumes nothing. Compare what arrives at the exchange with what is routed into queues (RabbitMQ dashboard: unroutable messages; management UI: exchange and queue bindings).',
            'The binding payments.events -> payments.bank-results (routing key bank.transfer.result) was deleted. The connector publishes without the mandatory flag, so the broker confirms and silently drops unroutable messages.'
        )
        RootCause = 'The binding between exchange payments.events and queue payments.bank-results was deleted; results are unroutable and dropped (the connector publishes without mandatory=true, so nobody notices).'
        Explanation = 'Publisher confirms only say "the broker took responsibility", not "a queue got it": an unroutable message is confirmed and discarded. Payments never receives results; sagas time out, send status inquiries whose answers are dropped as well, and finally park in RequiresAttention. Restarting Payments would hide the problem, because it re-declares the binding at start-up.'
        Fix = 'Re-create the binding (management UI, rabbitmqctl or restart Payments) and resume the parked payments. Prevent: publish with mandatory=true and handle basic.return, alternate exchange for unroutable messages, alert on unroutable/dropped messages, manage topology as code.'
        Inject = { param($State) Remove-ResultsBinding }
        Resolve = { param($State) Restore-ResultsBinding }
        Check = { $v = Get-Prom 'sum(increase(rabbitmq_global_messages_unroutable_dropped_total[2m]))'; $v -and $v -gt 5 }
        SettleSeconds = 45
    }

    # ------------------------------------------------------------------------------------------ 9
    @{
        Id = 9; Title = 'Bank throttling'; Category = 'external bank'; Difficulty = 'medium'; Severity = 'SEV-2'; Load = 20
        Ticket = @'
Customer support, {0}: many customers complain that their outgoing payments failed with "bank
rejected". The customers' money was returned. The partner bank says it has no record of rejecting
these transfers.
Impact: a large share of external payments fail; internal transfers are fine.
'@
        Hints = @(
            'Look at the failure reasons of the failed payments (API, or the payments table) and at the bank call outcomes on the Connector dashboard.',
            'What HTTP status does the bank actually return for these calls? Check the connector HTTP client spans or the "HTTP client to bank by status" panel.',
            'The bank throttles with 429 Too Many Requests. The connector treats every 4xx as a final business rejection, so throttled transfers are failed and compensated instead of retried.'
        )
        RootCause = 'The bank started rate limiting (5 requests/s) and the connector maps every 4xx response, including 429, to a final rejection (weakness in bank/client.go).'
        Explanation = '429 means "try again later", not "rejected". Because the connector reports Rejected, the saga releases the funds and the payment ends Failed with reason "bank rejected: HTTP429".'
        Fix = 'Treat 429 (and 408/409 depending on the API) as transient: retry with backoff and honour Retry-After; limit the connector''s own request rate / concurrency to the bank''s quota.'
        Inject = { param($State) $State.bank = Get-BankConfig; Set-BankConfig @{ maxRequestsPerSecond = 5 } }
        Resolve = { param($State) Reset-BankConfig }
        Check = { $v = Get-Prom 'sum(increase(fakebank_transfers_total{result="throttled"}[2m]))'; $v -and $v -gt 5 }
        SettleSeconds = 45
    }

    # ------------------------------------------------------------------------------------------ 10
    @{
        Id = 10; Title = 'Slow bank responses'; Category = 'external bank'; Difficulty = 'easy'; Severity = 'SEV-3'; Load = 20
        Ticket = @'
Customer support, {0}: some outgoing payments take much longer than usual to complete (10 s or more
instead of under a second). They do complete eventually. Internal transfers are fast.
Impact: roughly a third of external payments are slow.
'@
        Hints = @(
            'Which saga step takes long for the slow payments? Compare a slow and a fast external payment trace.',
            'Look at bank call latency and outcomes on the Connector dashboard and at the connector HTTP client spans to the bank.',
            'About a third of bank calls take ~8 s and exceed the connector''s 5 s timeout. The retry 5 s later is answered from the bank''s idempotency store, so the payment completes, just late.'
        )
        RootCause = 'The bank answers ~30 % of transfer requests only after ~8 s (Fake Bank slowRate/slowMs), above the connector timeout of 5 s.'
        Explanation = 'The connector times out, schedules a retry through the delay queue, and the retry finds the already executed transfer. Correct thanks to idempotency keys, but slow; duplicates are avoided only because the key is the payment id.'
        Fix = 'External dependency: escalate to the bank. Tune timeouts / retry delays to the bank''s SLA, alert on bank latency percentiles.'
        Inject = { param($State) $State.bank = Get-BankConfig; Set-BankConfig @{ slowRate = 0.3; slowMs = 8000 } }
        Resolve = { param($State) Reset-BankConfig }
        Check = { $v = Get-Prom 'sum(increase(bank_calls_total{operation="submit",outcome="error"}[2m]))'; $v -and $v -gt 3 }
        SettleSeconds = 45
    }

    # ------------------------------------------------------------------------------------------ 11
    @{
        Id = 11; Title = 'Connection resets towards the bank'; Category = 'external bank'; Difficulty = 'easy'; Severity = 'SEV-2'; Load = 20
        Ticket = @'
Alert "ConnectorErrors" fired at {0}: error log rate of the connector is high. Customer support
reports that some outgoing payments are slow and a few end up "Under review". The bank says its
API is healthy and shows no errors on their side.
Impact: part of the external payments delayed.
'@
        Hints = @(
            'Check the bank call outcomes and the connector error logs: what kind of error is it?',
            'The errors are network-level (connection reset / EOF), not HTTP status codes. Is the bank itself healthy (Fake Bank dashboard - does it even see these requests)?',
            'TCP resets are injected on ~50 % of new connections between the connector and the bank (Toxiproxy "fakebank-http", reset_peer).'
        )
        RootCause = 'A network fault between connector and bank: Toxiproxy reset_peer toxic on ~50 % of connections of the "fakebank-http" proxy.'
        Explanation = 'Reset connections fail the HTTP call before the bank sees it. The connector retries with a fixed delay; after 5 attempts the command is dead-lettered and the saga recovers through its status inquiry, or parks the payment.'
        Fix = 'Network/infra. In code: retries with backoff and jitter, DLQ monitoring and replay.'
        Inject = { param($State) Add-Toxic 'fakebank-http' 'reset_peer' @{ timeout = 0 } 'downstream' 0.5 }
        Resolve = { param($State) Reset-Toxiproxy }
        Check = { $v = Get-Prom 'sum(increase(bank_calls_total{outcome="error"}[2m]))'; $v -and $v -gt 5 }
        SettleSeconds = 45
    }

    # ------------------------------------------------------------------------------------------ 12
    @{
        Id = 12; Title = 'CPU limit lowered on the Payments container'; Category = 'resource limits'; Difficulty = 'medium'; Severity = 'SEV-2'; Load = 20
        Ticket = @'
Alert "PaymentsApiLatencyHigh" fired at {0}: Payments API latency up ~10x, and payments take
seconds to complete. The on-call engineer looked at the host: overall CPU usage is low and there
is plenty of free memory.
Impact: all customers.
'@
        Hints = @(
            'Look at the Containers dashboard: CPU usage relative to the limit, and throttling.',
            'One container is heavily throttled although its absolute CPU usage is small. Compare its CPU limit with the value in .env.',
            'The Payments container''s CPU limit was lowered at runtime (docker update --cpus 0.25); docker compose does not notice live changes.'
        )
        RootCause = 'docker update --cpus 0.25 on the Payments container.'
        Explanation = 'The CFS quota throttles the process every period, so all work (requests, saga worker, outbox, GC) queues behind the quota: latency goes up while host CPU looks idle.'
        Fix = 'Restore the limit (docker update or recreate the container from compose); alert on throttling, not just on CPU usage.'
        Inject = {
            param($State)
            $State.limits = Get-Limits 'payments'
            Invoke-Native 'docker' @('update', '--cpus', '0.25', $State.limits.Id) | Out-Null
        }
        Resolve = { param($State) Restore-Limits $State.limits }
        Check = { $v = Get-Prom 'sum(rate(container_cpu_cfs_throttled_periods_total{service="payments"}[1m])) / sum(rate(container_cpu_cfs_periods_total{service="payments"}[1m]))'; $v -and $v -gt 0.3 }
        SettleSeconds = 60
    }

    # ------------------------------------------------------------------------------------------ 13
    @{
        Id = 13; Title = 'Memory limit lowered on the Wallet container'; Category = 'resource limits'; Difficulty = 'medium'; Severity = 'SEV-2'; Load = 25
        Ticket = @'
Alert "WalletRestarts" fired at {0}: the wallet service has restarted several times in the last
minutes. Customers see payments fail or hang intermittently; some end "Under review".
Impact: intermittent, all customers.
'@
        Hints = @(
            'Why does the container restart? Look at the Containers dashboard (restarts, memory vs. limit) and at docker inspect (State.OOMKilled, RestartCount).',
            'Compare the wallet''s memory limit and working set with the value in .env, and with how much heap the .NET runtime thinks it may use.',
            'The memory limit was lowered at runtime (docker update --memory). The .NET GC sized its heap for the limit it saw at start-up, so it grows past the new limit and the kernel OOM-kills the process.'
        )
        RootCause = 'docker update --memory 90m on the Wallet container (the runtime still assumes the original limit).'
        Explanation = 'The kernel kills the process when it exceeds the new limit; Docker restarts it. Requests in flight fail, the saga retries, some retries are exhausted and payments park or fail.'
        Fix = 'Restore the limit and restart; size limits from measurements; set GC heap limits explicitly (DOTNET_GCHeapHardLimit) when limits change; alert on OOM kills and restarts.'
        Inject = {
            param($State)
            $State.limits = Get-Limits 'wallet'
            Invoke-Native 'docker' @('update', '--memory', '90m', '--memory-swap', '90m', $State.limits.Id) | Out-Null
        }
        Resolve = {
            param($State)
            Restore-Limits $State.limits
            Invoke-Native 'docker' @('restart', $State.limits.Id) | Out-Null
        }
        Check = { $v = Get-Prom 'sum(increase(container_oom_events_total{service="wallet"}[5m])) + sum(changes(container_start_time_seconds{service="wallet"}[5m]))'; $v -and $v -gt 0 }
        SettleSeconds = 90
    }

    # ------------------------------------------------------------------------------------------ 14
    @{
        Id = 14; Title = 'Concurrent payments from the same account'; Category = 'concurrency'; Difficulty = 'hard'; Severity = 'SEV-1'; Load = 15
        Ticket = @'
Finance reconciliation alert, {0}: several customer accounts have a NEGATIVE balance. Overdrafts are
not offered on these accounts and must never happen. The affected customers are business customers
who submit salary batches (many payments at once) through the API.
Impact: money paid out that customers did not have; regulatory reporting required.
'@
        Hints = @(
            'Find the affected accounts in the wallet database (balance_minor < 0 or balance_minor - reserved_minor < 0) and list their reservations and ledger entries in time order.',
            'How many reservations for the same account were created at practically the same moment? Look at the Wallet reserve traces / database spans for one of them: what did each reservation see as available balance?',
            'Reserve reads the account without a lock, checks the available balance and then increments reserved_minor in SQL. Concurrent reservations for one account all pass the check (check-then-act race).'
        )
        RootCause = 'Weakness: WalletService.ReserveAsync checks the available balance on an unlocked read and then does an atomic increment. The increment avoids lost updates, but not the race between check and update.'
        Explanation = 'The saga processes payments in parallel; a batch of payments from one account produces concurrent reserve calls. Each sees the same available balance, all succeed, reserved exceeds balance, and the commits drive the balance negative.'
        Fix = 'Make check and update atomic: SELECT ... FOR UPDATE on the account (as commit/release do), or a conditional UPDATE ... SET reserved = reserved + x WHERE balance - reserved >= x and check the row count; add a CHECK constraint (balance_minor - reserved_minor >= 0) as a safety net.'
        Inject = { param($State) Start-Traffic 'traffic-2' '/extra/traffic-b.js' @{} '256m' }
        Resolve = { param($State) Stop-Traffic }
        Check = { $o = Invoke-Sql 'wallet-db' 'wallet' 'SELECT count(*) FROM accounts WHERE balance_minor < 0 OR balance_minor - reserved_minor < 0'; [int](@($o)[-1]) -gt 0 }
        SettleSeconds = 60
    }

    # ------------------------------------------------------------------------------------------ 15
    @{
        Id = 15; Title = 'Integration partner flooding the API'; Category = 'configuration'; Difficulty = 'medium'; Severity = 'SEV-2'; Load = 15
        Ticket = @'
Alert "ApiLatencyHigh" fired at {0}: API latency is high for all customers. Account management
knows that one integration partner polls our API heavily; the gateway has a per-client rate limit of
200 requests/s, which should cap them, but the gateway reports no throttling (no HTTP 429 at all).
Impact: all customers; payments are slow.
'@
        Hints = @(
            'Look at the Gateway dashboard: total request rate, rate limiter decisions, hot routes. Is the limit ever reached?',
            'The limiter partitions requests by a key. Read how the gateway derives that key (Gateway/Program.cs) and whether the caller controls it.',
            'The partition key is the client-supplied X-Client-Id header. The partner sends a different value with every request, so every request gets a fresh token bucket (and the limiter keeps a bucket per value).'
        )
        RootCause = 'Weakness: the gateway rate limit is keyed on the client-supplied X-Client-Id header, so any caller can bypass it by varying the header; the per-client limit (200/s) is also higher than what the backend can serve.'
        Explanation = 'The partner rotates X-Client-Id values; the limiter never rejects anything. Its traffic saturates the gateway and Payments/Wallet CPU, which slows every customer. The limiter also accumulates one partition per distinct header value.'
        Fix = 'Key rate limits on an authenticated identity (API key / client certificate / token subject), fall back to the IP, add a global concurrency or rate limit sized to backend capacity, and cap partitions.'
        Inject = { param($State) Start-Traffic 'traffic-2' '/extra/traffic-c.js' @{ RATE = 250 } '384m' }
        Resolve = { param($State) Stop-Traffic }
        Check = { $v = Get-Prom 'sum(rate(http_server_request_duration_seconds_count{job="gateway"}[1m]))'; $r = Get-Prom 'sum(rate(aspnetcore_rate_limiting_requests_total{job="gateway",aspnetcore_rate_limiting_result!="acquired"}[1m]))'; ($v -and $v -gt 150) -and (-not $r -or $r -eq 0) }
        SettleSeconds = 60
    }
)
