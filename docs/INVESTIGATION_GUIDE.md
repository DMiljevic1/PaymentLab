# Investigation guide

A generic way to investigate an incident in PaymentLab (no spoilers). The same method works in production.

## 1. The method

1. **Understand the symptom.** Read the ticket carefully: *who* is affected (all customers? some? only
   external payments?), *since when*, *what exactly* they see (errors, slowness, wrong state, wrong money).
   Write down what you expect a healthy system to do.
2. **Confirm it in metrics.** Find the symptom on a dashboard before forming a theory. Note the start time –
   it narrows everything else down.
3. **Localise.** Which service / dependency deviates first? Use RED (rate, errors, duration) per service and
   USE (utilisation, saturation, errors) per resource (CPU, memory, connections, queues, locks).
   Distinguish *cause* from *victims*: a slow dependency makes everything that waits on it slow too.
4. **Follow examples in traces.** Pick a concrete affected request or payment and open its trace: where does
   the time go, which span fails, what happened between two spans (gaps are often queues or waits)?
5. **Read the logs of that trace.** From the trace go to the logs of all services for the same `trace_id`;
   look for warnings, errors, retries, unexpected states.
6. **Check the dependency itself.** Database (`pg_stat_activity`, locks, `pg_stat_statements`), broker
   (queues, consumers, policies), container (limits, restarts), configuration (env, runtime changes).
7. **Form a hypothesis and test it.** What would you expect to see if the hypothesis is true – and if it is
   false? Look for that evidence. Change one thing at a time.
8. **Mitigate, then fix.** Restore service first (roll back, remove the fault, scale, shed load), then fix
   the underlying cause in code or configuration, then prove it with the same reproduction.
9. **Write it down** (`docs/postmortems/TEMPLATE.md`): timeline, root cause, detection gaps, prevention.

Useful questions: *What changed?* (deploys, config, data volume, traffic shape, dependency behaviour) ·
*Is it all requests or a subset – what do the affected ones have in common?* · *Is the system slow or is it
waiting?* · *Is work piling up somewhere (queue, outbox, saga state, pool)?* · *Does it recover by itself?*

## 2. Where to look first

| Dashboard | Typical questions |
|---|---|
| **Overview** | Is the business flow healthy (created vs. finished, in flight by state, E2E time)? Which service's RED metrics deviate? Is a queue / DB / container saturated? Recent errors and error traces. |
| Gateway | Is traffic normal? Status codes, latency per route, rate-limiter decisions, upstream latency. |
| Payments | Which saga step is slow or failing, retries, transitions into `RequiresAttention`, claim query, outbox backlog and lag, inbox, calls to Wallet, DB pool, runtime. |
| Wallet | Reserve / commit / release outcomes and latency, DB pool. |
| Connector | Are messages consumed, how are they settled (ack, retry, dead-letter, requeue), bank call outcomes and latency, publishing. |
| Fake Bank | The bank's own view – does it even receive the calls? |
| RabbitMQ | Queue depth (ready vs. unacked), consumers, publish / deliver / ack rates, redeliveries, DLQs, memory alarm. |
| Postgres | Sessions by application and state, waiting sessions, blocked sessions, oldest transaction, locks, table scans, top statements. |
| Containers | CPU vs. limit, throttling, memory vs. limit, OOM kills, restarts. |
| Load (k6) | What the clients experience (latency, failures, retries, E2E). |

Other tools: RabbitMQ UI (http://localhost:15672, `lab`/`lab`), Swagger, `./lab.ps1 status`,
`./lab.ps1 logs <service>`, Payments operator API (`GET /admin/sagas?state=RequiresAttention`,
`GET /admin/sagas/summary`), Toxiproxy API (`GET http://localhost:8474/proxies`), Fake Bank config
(`GET http://localhost:8090/admin/config`).

## 3. From metrics to traces to logs

- **Metric → trace**: latency panels show *exemplars* (small dots); click one → *View trace*. Or in Explore →
  Tempo, search with TraceQL for the time window of the symptom.
- **Trace → logs**: in a trace, a span's *Logs for this span* (or the trace's logs link) runs a Loki query
  for the same `trace_id` across all services.
- **Log → trace**: in Explore → Loki, expand a log line; the `trace_id` field has an *Open trace* link.
- **Payment id → everything**: `payment.id` is a span attribute and part of many log lines.
  `{ span.payment.id = "<id>" }` in Tempo; `{service_name=~".+"} |= "<id>"` in Loki;
  `GET /api/payments/<id>/history` for the saga transitions.
- **Correlation id**: the gateway returns `X-Correlation-Id`; it is a span attribute (`correlation.id`) and a
  log field (`correlation_id`).

## 4. Useful queries

### PromQL (Explore → Prometheus)

```promql
# request rate / 5xx ratio / p95 per service
sum by (job) (rate(http_server_request_duration_seconds_count[5m]))
sum by (job) (rate(http_server_request_duration_seconds_count{http_response_status_code=~"5.."}[5m]))
  / sum by (job) (rate(http_server_request_duration_seconds_count[5m]))
histogram_quantile(0.95, sum by (le, job) (rate(http_server_request_duration_seconds_bucket[5m])))

# business flow
sum by (state) (payments_by_state)                          # in flight per saga state
sum by (state) (rate(payments_finished_total[5m]))          # outcomes
histogram_quantile(0.95, sum by (le) (rate(payment_processing_duration_seconds_bucket[5m])))
sum by (step, phase, outcome) (rate(saga_step_duration_seconds_count[5m]))
sum by (step) (rate(saga_step_retries_total[5m]))
outbox_pending  outbox_oldest_pending_age_seconds  inbox_pending

# dependencies from the client side
histogram_quantile(0.95, sum by (le, job) (rate(db_client_operation_duration_seconds_bucket[5m])))
sum by (job, db_client_connection_state) (db_client_connection_count)
sum by (operation, outcome) (rate(bank_calls_total[5m]))

# broker
sum by (queue) (rabbitmq_queue_messages_ready)
sum by (queue) (rabbitmq_queue_messages_unacked)
sum by (queue) (rabbitmq_queue_consumers)
sum by (queue) (rate(rabbitmq_queue_messages_redelivered_total[5m]))

# database
sum by (db, application_name, state) (pg_sessions_count)
max by (db, state) (pg_oldest_transaction_age_seconds)
sum by (db) (pg_blocked_sessions)
topk(10, sum by (relname) (rate(pg_stat_user_tables_seq_tup_read[5m])))

# containers
sum by (service) (rate(container_cpu_cfs_throttled_periods_total[5m])) / sum by (service) (rate(container_cpu_cfs_periods_total[5m]))
max by (service) (container_memory_working_set_bytes) / max by (service) (container_spec_memory_limit_bytes > 0)
changes(container_start_time_seconds{service!=""}[15m])
```

### LogQL (Explore → Loki)

```logql
{service_name="payments"} | detected_level=~"warn|error"
{service_name=~".+"} | trace_id="<trace id>"
{service_name=~".+"} |= "<payment id>"
sum by (service_name, detected_level) (count_over_time({service_name=~".+"} | detected_level=~"warn|error" [1m]))
{service_name="connector"} | json | __error__="" | line_format "{{.body}}"
```

Log attributes (e.g. `PaymentId`, `outcome`, `attempt`, `correlation_id`) are structured metadata: filter
with `| PaymentId="<id>"`, `| outcome="Rejected"`.

### TraceQL (Explore → Tempo)

```traceql
{ status = error }
{ resource.service.name = "payments" && duration > 2s }
{ span.payment.id = "<payment id>" }
{ name = "POST /reservations/" } | duration > 500ms
{ span.db.system.name = "postgresql" && duration > 200ms }
{ span.messaging.destination.name = "connector.bank-transfers" }
```

### Postgres

```sql
-- psql: docker compose exec payments-db psql -U payments -d payments   (wallet: wallet-db / wallet)

-- who is connected, what are they doing, for how long
SELECT pid, application_name, client_addr, state, wait_event_type, wait_event,
       now() - xact_start AS xact_age, now() - query_start AS query_age, left(query, 80) AS query
FROM pg_stat_activity WHERE datname = current_database() ORDER BY xact_start NULLS LAST;

-- who blocks whom
SELECT a.pid, a.application_name, a.state, pg_blocking_pids(a.pid) AS blocked_by, left(a.query, 80)
FROM pg_stat_activity a WHERE cardinality(pg_blocking_pids(a.pid)) > 0;

-- locks held / awaited
SELECT l.locktype, l.relation::regclass, l.mode, l.granted, a.pid, a.application_name, a.state
FROM pg_locks l JOIN pg_stat_activity a USING (pid) WHERE a.datname = current_database();

-- most expensive statements
SELECT calls, round(total_exec_time) AS total_ms, round(mean_exec_time::numeric, 2) AS mean_ms, rows,
       left(regexp_replace(query, '\s+', ' ', 'g'), 120) AS query
FROM pg_stat_statements ORDER BY total_exec_time DESC LIMIT 15;
SELECT pg_stat_statements_reset();            -- start measuring from now

-- how a query is executed
EXPLAIN (ANALYZE, BUFFERS) SELECT ...;

-- table sizes and scans
SELECT relname, n_live_tup, n_dead_tup, seq_scan, seq_tup_read, idx_scan,
       pg_size_pretty(pg_total_relation_size(relid)) AS size
FROM pg_stat_user_tables ORDER BY seq_tup_read DESC;

-- settings
SHOW max_connections;
SELECT name, setting, unit, source FROM pg_settings WHERE source NOT IN ('default', 'override');

-- terminate a session (mitigation)
SELECT pg_terminate_backend(<pid>);
```

Postgres server logs (slow statements > 500 ms, lock waits > 1 s, deadlocks): `./lab.ps1 logs payments-db`.

Domain checks (wallet):

```sql
SELECT count(*) FROM accounts WHERE balance_minor <> (SELECT coalesce(sum(amount_minor), 0) FROM ledger_entries l WHERE l.account_id = accounts.id);
SELECT status, count(*) FROM reservations GROUP BY status;
```

### RabbitMQ

```powershell
docker compose exec rabbitmq rabbitmqctl list_queues name type messages_ready messages_unacknowledged consumers
docker compose exec rabbitmq rabbitmqctl list_consumers
docker compose exec rabbitmq rabbitmqctl list_connections name state channels recv_oct send_oct
docker compose exec rabbitmq rabbitmqctl list_channels name consumer_count messages_unacknowledged prefetch_count
docker compose exec rabbitmq rabbitmqctl list_policies
docker compose exec rabbitmq rabbitmq-diagnostics alarms
docker compose exec rabbitmq rabbitmq-diagnostics memory_breakdown
```

In the management UI: *Queues* (message rates, consumers, features column), *Get messages* on a DLQ to read
dead-lettered messages and their `x-death` header, *Admin → Policies*.

### Containers and network

```powershell
./lab.ps1 status
docker inspect paymentlab-payments-1 --format '{{.State.Status}} restarts={{.RestartCount}} oom={{.State.OOMKilled}}'
docker inspect paymentlab-payments-1 --format 'cpus={{.HostConfig.NanoCpus}} mem={{.HostConfig.Memory}}'
Invoke-RestMethod http://localhost:8474/proxies     # Toxiproxy: proxies and active toxics
Invoke-RestMethod http://localhost:8090/admin/config # Fake Bank behaviour
```

## 5. Things that are easy to miss

- A healthy-looking service can be slow because it **waits** (pool, lock, queue, throttled CPU), not because
  it works hard. Compare client-side and server-side durations.
- **Retries hide failures** and multiply load. Check retry counters, redeliveries, DLQs.
- **Partial impact** is a strong clue: find what the affected requests share (account, payment type, route,
  client).
- **Volume-dependent** problems do not show up in the smoke test – check `seq_tup_read`, query plans and how
  latency changes with data size.
- **The live state can differ from the repository** – compare what is actually running (containers,
  broker, databases, dependencies) with the configuration you expect.
