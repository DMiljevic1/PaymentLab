# ⚠️ SPOILERS – answers to every weakness and incident

Do not read this before you have finished investigating. Everything below describes the deliberate
weaknesses in the code and the faults the game master (`./lab.ps1 incident`) injects.

- [Part 1 – weaknesses in the code](#part-1--weaknesses-in-the-code) (W1–W6, plus minor observations)
- [Part 2 – incident scenarios](#part-2--incident-scenarios) (1–15)
- [Scenario ↔ weakness map](#scenario--weakness-map)

`chaos/verify.ps1` runs every scenario (inject → wait for the symptom → resolve → smoke test).

---

## Part 1 – weaknesses in the code

These are honest first-implementation mistakes. The system works on the happy path and in the smoke
test; they show up only with volume, concurrency, slow dependencies or hostile clients.

### W1 – Saga claim query scans all sagas (data volume)

| | |
|---|---|
| **Where** | `src/dotnet/Payments/Saga/SagaProcessor.cs` → `ClaimDueAsync` |
| **Symptom** | As the history grows (seed medium/large), payments stay `Pending` longer, payments-db CPU is high even at moderate traffic, throughput of the saga worker drops. |
| **How to see it** | Postgres dashboard (db = payments) → *Top statements by total execution time*: the `UPDATE payment_sagas SET locked_by …` claim statement is on top with a mean time of tens–hundreds of ms. *Rows read by seq scans* on `payment_sagas` is huge. Payments dashboard → *Claim query duration*. `EXPLAIN (ANALYZE, BUFFERS)` shows `Seq Scan on payment_sagas … Rows Removed by Filter: ~1 000 000`. |
| **Root cause** | `WHERE (s.next_attempt_at <= now() OR s.payment_id IN (SELECT … FROM inbox_messages …))`. Because of the `OR` with a subquery the planner cannot use the partial index `ix_payment_sagas_next_attempt_at`, so every poll (every 500 ms when idle, continuously under load) scans the whole table. With 10 k rows it costs nothing; with millions it dominates the database. |
| **Fix** | Split the condition into two index-friendly queries, e.g. `SELECT payment_id FROM payment_sagas WHERE next_attempt_at <= now() … UNION SELECT payment_id FROM inbox_messages WHERE processed_at IS NULL`, then lock/claim those ids (`FOR UPDATE SKIP LOCKED`). Alternatives: a partial index on non-final sagas only, archiving finished sagas, or relying on the in-process nudge for inbox messages. |
| **Verify** | `EXPLAIN` uses index scans; claim p95 drops to ~1 ms; `./lab.ps1 seed large` + `./lab.ps1 load steady 25 5m` keeps E2E p95 below ~1 s. |

### W2 – Outbox relay: one confirm at a time inside an open transaction (broker latency)

| | |
|---|---|
| **Where** | `src/dotnet/Payments/Messaging/OutboxRelay.cs` → `PublishBatchAsync` |
| **Symptom** | With a little latency between Payments and RabbitMQ the outbox backlog grows without bound; external payments wait minutes in `AwaitingBank` before the command even reaches the connector; status inquiries pile on top; some sagas park. |
| **How to see it** | Payments → *Outbox backlog*, *Outbox: oldest pending row and publish lag*; Postgres (payments) → *Oldest open transaction by session state* (`idle in transaction` for seconds); trace of a slow payment: big gap between the saga span and the `payments.commands publish` span. |
| **Root cause** | The relay reads up to 100 rows `FOR UPDATE SKIP LOCKED`, then for each row calls `BasicPublishAsync` on a channel with publisher-confirm tracking, which waits for the broker's confirm before returning. Throughput = 1 / RTT (≈ 6 msg/s at 150 ms) and the DB transaction stays open for the whole batch. |
| **Fix** | Publish the whole batch and await the confirms together (start all publishes, `Task.WhenAll`, or use batched confirms), keep the database transaction short (claim rows → commit → publish → mark published in a second short transaction), and alert on outbox age. Several relay instances with `SKIP LOCKED` scale further. |
| **Verify** | Scenario 6: with the latency toxic active the backlog stays near zero and publish lag stays ≈ RTT. |

### W3 – Wallet reserve: check-then-act without a lock (concurrency)

| | |
|---|---|
| **Where** | `src/dotnet/Wallet/Services/WalletService.cs` → `ReserveAsync` |
| **Symptom** | Accounts end with a negative balance (or `balance - reserved < 0`) after many payments from the same account were submitted at once. All of those payments are `Completed`. |
| **How to see it** | SQL on wallet-db: `SELECT id, balance_minor, reserved_minor FROM accounts WHERE balance_minor < 0 OR balance_minor - reserved_minor < 0;` then the account's `reservations` / `ledger_entries` ordered by time: many reservations within the same few milliseconds. Wallet traces for `POST /reservations`: concurrent `SELECT accounts` spans for the same account. |
| **Root cause** | The available balance is checked on a plain (unlocked, `AsNoTracking`) read; the reservation is then applied with an atomic `UPDATE … SET reserved_minor = reserved_minor + x`. The atomic increment prevents lost updates – the comment says so – but not the race between *check* and *update*: N concurrent reservations all see the same available balance and all succeed. Commit later subtracts them from the balance → overdraft. |
| **Fix** | Make check and update one atomic step: either lock the row (`SELECT … FOR UPDATE`, as commit and release do), or a conditional update `UPDATE accounts SET reserved_minor = reserved_minor + @x WHERE id = @id AND balance_minor - reserved_minor >= @x` and treat 0 affected rows as insufficient funds. Add `CHECK (balance_minor - reserved_minor >= 0)` as a safety net. |
| **Verify** | Scenario 14 for a few minutes → the SQL above returns no rows; each salary batch gets exactly 10 completed and 20 rejected payments. |

### W4 – Connector treats every 4xx from the bank as a final rejection (external bank)

| | |
|---|---|
| **Where** | `src/go/connector/internal/bank/client.go` → `Submit` (`resp.StatusCode >= 400 && < 500` → `Accepted: false`) |
| **Symptom** | When the bank throttles (429) or answers another retryable 4xx (408, 409 during maintenance…), payments end `Failed` with reason `bank rejected: HTTP429` and the funds are released, although the bank never rejected them. |
| **How to see it** | Connector → *Bank calls by outcome* (`rejected` rises), *HTTP client to bank by status* (`429`); Fake Bank → *Transfers by result* (`throttled`); payments API / table: `failure_reason = 'bank rejected: HTTP429'`. |
| **Root cause** | 4xx is assumed to mean "the request is wrong, retrying will not help". 429 means the opposite. |
| **Fix** | Classify 429 (and 408, and 409 if the bank documents it as "in progress") as transient: retry via the delay queue with backoff, honour `Retry-After`; cap the connector's own request rate to the bank's quota (token bucket per bank) so it does not trigger throttling in the first place. |
| **Verify** | Scenario 9: payments complete (slower) instead of failing; `failure_reason` never contains `HTTP429`. |

### W5 – Gateway rate limit keyed on a client-supplied header (configuration / security)

| | |
|---|---|
| **Where** | `src/dotnet/Gateway/Program.cs` → `AddRateLimiter` partition key = `X-Client-Id` header (fallback IP); `GATEWAY_RATE_LIMIT=200` in `.env` |
| **Symptom** | A noisy caller can push any amount of traffic through the gateway: no 429s at all, latency rises for everyone. |
| **How to see it** | Gateway → *Rate limiter decisions* (only `acquired`), *Requests / s* far above the per-client limit; reading the code shows the key is controlled by the caller. (The limiter also keeps a partition per distinct header value.) |
| **Root cause** | Per-client limiting only works if "client" is something the caller cannot choose. In addition the per-client limit (200 req/s) is higher than what the backend can serve (~40–65 payments/s on the default limits), so even an honest single client could overload it. |
| **Fix** | Partition by authenticated identity (API key, mTLS identity, token subject) or at least by IP, add a global limiter / concurrency limiter sized to backend capacity, return 429 early, and bound the number of partitions. |
| **Verify** | Scenario 15: gateway answers the partner with 429s; customer latency stays normal. |

### W6 – Payment list without an index for the filter, with OFFSET paging (data volume)

| | |
|---|---|
| **Where** | `src/dotnet/Payments/Api/PaymentEndpoints.cs` → `GET /payments` (`status` filter or no filter; `Skip/Take`) |
| **Symptom** | A few clients polling `GET /api/payments?status=…&page=N` saturate payments-db CPU on a seeded database; every other request slows down. |
| **How to see it** | Postgres (payments) → *Top statements*: the `SELECT … FROM payments … JOIN payment_sagas … ORDER BY created_at DESC LIMIT … OFFSET …` statement; *Rows read by seq scans* on `payments`; Gateway / Payments → latency of the list route; Tempo: slow `GET /payments/` server spans with one long DB span. |
| **Root cause** | The only index starting with a filter column is `(source_account_id, created_at)`. Filtering by status (or not filtering) forces a scan of all payments plus the join to `payment_sagas` and a sort; `OFFSET` makes deep pages even more expensive. |
| **Fix** | Index for the access path (`(status, created_at DESC)`; `(created_at DESC)` for the unfiltered list), keyset pagination (`WHERE created_at < @cursor`), require a filter / limit page depth, select only needed columns, serve back-office from a read model / replica, rate-limit back-office screens. |
| **Verify** | `EXPLAIN` shows an index scan with a small LIMIT; scenario 5 keeps DB CPU low. |

### Minor observations (not used by scenarios, but known)

- **Fixed retry delay without jitter in the connector** (`RETRY_DELAY_MS`, delay queue TTL): after a bank outage all failed commands retry in synchronized waves. Better: per-attempt delay queues with exponential backoff, or jitter.
- **Saga lease vs. long runs**: the lease is 60 s and the same process may re-claim a saga whose lease expired while it is still running it (`locked_by` is per process). Steps are idempotent and the saga row has an `xmin` concurrency token, so this produces an error log, not a double effect – but long-running steps should extend the lease.
- **History shows `AwaitingBank → AwaitingBank (waiting up to 30s)`**: cosmetic – the wait is recorded as a transition.
- **Rate-limit partitions are never bounded** (part of W5).
- **Connector publishes results with `mandatory=false`** (`internal/broker/session.go`): an unroutable result is confirmed by the broker and dropped silently (exposed by scenario 8).

---

## Part 2 – incident scenarios

Start: `./lab.ps1 incident [-Id N | -Difficulty easy|medium|hard]`. All faults come from outside the
code (Toxiproxy toxics, Fake Bank admin config, Postgres/RabbitMQ admin actions, `docker pause/update`,
extra traffic) – no hidden flags in the services. Background customer traffic (k6 `main.js`, container
`paymentlab-traffic-1`) runs during every incident; extra traffic for some scenarios runs as
`paymentlab-traffic-2` (it does not report client metrics, like a real third party). `resolve` stops
all of it, removes every fault, resumes payments parked in `RequiresAttention` and runs the smoke test.

| # | Scenario | Category | Difficulty | Weakness |
|---|---|---|---|---|
| 1 | Slow network path to the payments database | databases | easy | – |
| 2 | Forgotten transaction holding row locks | databases | medium | – |
| 3 | Per-role connection limit on the payments database | configuration | medium | – |
| 4 | Historical data import | data volume | hard | W1 |
| 5 | Back-office list polling | data volume | hard | W6 |
| 6 | Latency between Payments and the broker | message broker | hard | W2 |
| 7 | Frozen consumer | consumers and workers | easy | – |
| 8 | Results no longer routed | message broker | medium | (minor: publish without mandatory) |
| 9 | Bank throttling | external bank | medium | W4 |
| 10 | Slow bank responses | external bank | easy | – |
| 11 | Connection resets towards the bank | external bank | easy | – |
| 12 | CPU limit lowered on the Payments container | resource limits | medium | – |
| 13 | Memory limit lowered on the Wallet container | resource limits | medium | – |
| 14 | Concurrent payments from the same account | concurrency | hard | W3 |
| 15 | Integration partner flooding the API | configuration | medium | W5 |

### 1 – Slow network path to the payments database
- **Injected**: Toxiproxy `latency` 150 ms ± 30 ms (downstream) on proxy `payments-db` (`toxiproxy:15432`).
- **Where it shows**: Overview p95 latency (payments), Payments → *Command duration* (client side) high while Postgres → statements are fast and CPU is idle; traces: every `SELECT/INSERT/UPDATE` span ≥ 150 ms; DB pool *in use* rises.
- **Root cause / fix**: network latency between service and DB; remove the toxic (`./lab.ps1 resolve`). Lesson: compare client-side and server-side DB time.

### 2 – Forgotten transaction holding row locks
- **Injected**: `chaos/sql/hold.sh` runs inside wallet-db: `BEGIN; SELECT … FROM accounts WHERE hashtext(id) % 5 = 0 FOR UPDATE;` then leaves the session *idle in transaction* (`application_name = nightly-report`).
- **Where it shows**: Postgres (wallet) → *Blocked sessions*, *Oldest transaction* (idle in transaction, growing), *Sessions waiting (Lock)*; Wallet → reserve/commit latency ~3 s and 5xx; Payments → step retries for ReserveFunds/CommitFunds, `RequiresAttention` rises; ~20 % of accounts affected. `SELECT pid, application_name, state, xact_start FROM pg_stat_activity;` and `pg_blocking_pids()` name the culprit.
- **Root cause / fix**: `pg_terminate_backend(pid)`; prevention: `idle_in_transaction_session_timeout`, `lock_timeout` for app and reporting roles, reporting on a replica, alert on old transactions.

### 3 – Per-role connection limit on the payments database
- **Injected**: `ALTER ROLE payments CONNECTION LIMIT 8` and termination of existing payments sessions.
- **Where it shows**: Payments logs (`53300: too many connections for role "payments"`), Gateway/Payments 5xx, exceptions panel; Payments → *Connection pool* stuck at ≤ 8 although demand is higher; Postgres sessions for `payments` = 8.
- **Root cause / fix**: `ALTER ROLE payments CONNECTION LIMIT -1`. Size role limits, `Max Pool Size` × instances and `max_connections` together.

### 4 – Historical data import (W1)
- **Injected**: `./lab.ps1 seed large` (4 M payments, ~21 M history rows) while the lab is running, then normal traffic.
- **Where it shows / cause / fix**: see W1. `resolve` re-seeds the previous scale (or `small`).

### 5 – Back-office list polling (W6)
- **Injected**: seeds `medium` if the database is smaller, then 6 virtual users poll `GET /api/payments?status=…&page=1..20&pageSize=50` (70 %) or the unfiltered list (30 %) once per second with `X-Client-Id: backoffice-ui`.
- **Where it shows / cause / fix**: see W6. `resolve` stops the traffic and restores the previous seed scale if it was changed.

### 6 – Latency between Payments and the broker (W2)
- **Injected**: Toxiproxy `latency` 150 ms ± 30 ms on proxy `rabbitmq-payments` (`toxiproxy:35672`).
- **Where it shows / cause / fix**: see W2. Note that consuming (prefetch 50) is barely affected – only the sequential confirm path is.

### 7 – Frozen consumer
- **Injected**: `docker pause` on the connector container.
- **Where it shows**: RabbitMQ → `connector.bank-transfers` ready messages grow, consumers 0 (after heartbeat timeout); Connector dashboard flat / no data; `./lab.ps1 status` shows `(Paused)`; Payments: `AwaitingBank` → `VerifyingBankStatus` → `RequiresAttention`.
- **Fix**: `docker unpause`, then resume parked payments (`POST /admin/sagas/{id}/retry`). Alert on "queue has messages but no consumers" and on message age.

### 8 – Results no longer routed
- **Injected**: the binding `payments.events` → `payments.bank-results` (routing key `bank.transfer.result`) is deleted via the management API.
- **Where it shows**: RabbitMQ dashboard → *Dead-lettered / unroutable* (`unroutable dropped` rises), `payments.bank-results` receives nothing while the connector shows results as `confirmed` / `published`; Payments → bank results consumed drops to 0, `AwaitingBank` → `VerifyingBankStatus` → `RequiresAttention`; management UI → queue *Bindings* only shows the default binding. The bank (Fake Bank dashboard) executed the transfers.
- **Root cause**: missing binding + the connector publishes with `mandatory=false`, so the broker confirms and drops unroutable messages; publisher confirms do not mean "routed to a queue".
- **Fix**: re-create the binding (or restart Payments, which re-declares its topology – a misleading "restart fixed it"), resume parked payments. Prevent with `mandatory=true` + handling `basic.return`, an alternate exchange, alerts on unroutable messages, topology managed as code.

### 9 – Bank throttling (W4)
- **Injected**: Fake Bank `maxRequestsPerSecond = 5` (`POST /admin/config`).
- **Where it shows / cause / fix**: see W4.

### 10 – Slow bank responses
- **Injected**: Fake Bank `slowRate = 0.3`, `slowMs = 8000` (the transfer is executed, the answer comes after 8 s).
- **Where it shows**: Connector → bank call latency p95/p99 ≈ 5 s (client timeout) and `error` outcomes; connector logs `context deadline exceeded`; the retry is a fast `replay` at the bank (Fake Bank → *Transfers by result*); E2E p95 of external payments ≈ 10 s.
- **Fix**: external dependency; check timeouts vs. the bank's SLA. Idempotency keys make the retry safe.

### 11 – Connection resets towards the bank
- **Injected**: Toxiproxy `reset_peer` (timeout 0, toxicity 0.5) on proxy `fakebank-http`.
- **Where it shows**: Connector → bank calls `error`, HTTP client errors without status code (`connection reset by peer` / `EOF` in logs); Fake Bank does **not** see these requests; retries via `connector.bank-transfers.retry`; some commands dead-lettered after 5 attempts; sagas recover via status inquiry or park.
- **Fix**: network path; retries with backoff and jitter, DLQ alerting.

### 12 – CPU limit lowered on the Payments container
- **Injected**: `docker update --cpus 0.25` on the payments container.
- **Where it shows**: Containers → *CPU throttling* for payments near 100 %, *CPU usage (% of limit)* at 100 % while absolute usage is small; latency everywhere in Payments; `docker inspect` (`NanoCpus = 250000000`) differs from `.env`.
- **Fix**: restore the limit (`docker update --cpus 1` or recreate from compose).

### 13 – Memory limit lowered on the Wallet container
- **Injected**: `docker update --memory 90m --memory-swap 90m` on the wallet container.
- **Where it shows**: Containers → working set at the limit, *OOM kills*, *Container (re)starts*; wallet 5xx / connection errors in Payments traces; `docker inspect` → `OOMKilled`, `RestartCount`.
- **Root cause**: the .NET GC sized its heap for the limit it saw at start-up; the new, lower limit is hit and the kernel kills the process.
- **Fix**: restore the limit and restart; set `DOTNET_GCHeapHardLimit`/`GCHeapHardLimitPercent` explicitly; alert on OOM kills.

### 14 – Concurrent payments from the same account (W3)
- **Injected**: extra traffic: 3 virtual users, each repeatedly creates an "employer" account with 50.00 EUR and immediately submits 30 internal payments of 5.00 EUR in parallel (`X-Client-Id: payroll-api`).
- **Where it shows / cause / fix**: see W3. Expected outcome per batch: 10 completed, 20 rejected – with the bug more than 10 complete and the employer account goes negative. The negative accounts remain after `resolve` (evidence).

### 15 – Integration partner flooding the API (W5)
- **Injected**: extra traffic at 250 req/s of `GET /api/accounts/{id}` and `GET /api/payments?accountId=…` with a random `X-Client-Id` on every request.
- **Where it shows / cause / fix**: see W5.

---

## Scenario ↔ weakness map

| Weakness | Scenarios that expose it |
|---|---|
| W1 claim query scan | 4 (also makes 1, 12 and every load test on seeded data worse) |
| W2 sequential confirms in outbox | 6 (also amplifies 1 and 12) |
| W3 reserve race | 14 |
| W4 4xx = rejected | 9 |
| W5 rate-limit key | 15 |
| W6 list query | 5 |
