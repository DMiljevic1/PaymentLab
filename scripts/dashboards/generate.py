"""Generates the provisioned Grafana dashboards into infra/grafana/dashboards/.

    python scripts/dashboards/generate.py

The JSON files are committed; Grafana reloads them automatically (provisioning polls every 30 s).
"""
import os
import sys

sys.path.insert(0, os.path.dirname(__file__))
from lib import *  # noqa: E402,F403

OUT = os.path.join(os.path.dirname(__file__), "..", "..", "infra", "grafana", "dashboards")

HTTP_SERVICES = "gateway|payments|wallet|fakebank"


def ratio(num, den):
    """num / den, but 0 instead of 'no data' when the numerator has no series (e.g. no errors yet)."""
    return f"({num} or {den} * 0) / {den}"
DOTNET = "gateway|payments|wallet"


# ---------------------------------------------------------------------------------------------- shared panels
def http_red(job, route_label="http_route", w=8):
    sel = f'job="{job}"'
    return [
        ts("Requests / s by route and status",
           [q(f'sum by ({route_label}, http_request_method, http_response_status_code) '
              f'(rate(http_server_request_duration_seconds_count{{{sel}}}[{RI}]))',
              "{{http_request_method}} {{" + route_label + "}} {{http_response_status_code}}")],
           unit="reqps", w=w),
        ts("Error ratio (5xx / all)",
           [q(ratio(f'sum by ({route_label}) (rate(http_server_request_duration_seconds_count{{{sel}, http_response_status_code=~"5.."}}[{RI}]))',
                    f'sum by ({route_label}) (rate(http_server_request_duration_seconds_count{{{sel}}}[{RI}]))'),
              "{{" + route_label + "}}")],
           unit="percentunit", w=w, min=0),
        ts("Latency by route (p50 / p95 / p99)",
           [q(f'histogram_quantile({p}, sum by (le, {route_label}) (rate(http_server_request_duration_seconds_bucket{{{sel}}}[{RI}])))',
              f"p{int(p * 100)} {{{{{route_label}}}}}", exemplar=(p == 0.95)) for p in (0.5, 0.95, 0.99)],
           unit="s", w=w),
    ]


def dotnet_runtime(job, http_client=True):
    sel = f'job="{job}"'
    return [
        row(f"{job}: .NET runtime"),
        ts("CPU (cores)", [q(f'rate(dotnet_process_cpu_time_seconds_total{{{sel}}}[{RI}])', "{{cpu_mode}}")],
           unit="short", w=6, stack=True),
        ts("Memory", [q(f'dotnet_process_memory_working_set_bytes{{{sel}}}', "working set"),
                      q(f'dotnet_gc_last_collection_heap_size_bytes{{{sel}}}', "heap {{dotnet_gc_heap_generation}}")],
           unit="bytes", w=6),
        ts("GC collections / s and pause",
           [q(f'sum by (dotnet_gc_heap_generation) (rate(dotnet_gc_collections_total{{{sel}}}[{RI}]))', "gen {{dotnet_gc_heap_generation}}"),
            q(f'rate(dotnet_gc_pause_time_seconds_total{{{sel}}}[{RI}])', "pause time (s/s)")],
           unit="short", w=6),
        ts("Thread pool", [q(f'dotnet_thread_pool_thread_count{{{sel}}}', "threads"),
                           q(f'dotnet_thread_pool_queue_length{{{sel}}}', "queue length"),
                           q(f'rate(dotnet_monitor_lock_contentions_total{{{sel}}}[{RI}])', "lock contentions / s")],
           w=6),
        ts("Exceptions / s", [q(f'sum by (error_type) (rate(dotnet_exceptions_total{{{sel}}}[{RI}]))', "{{error_type}}")],
           unit="short", w=12 if http_client else 24),
    ] + ([] if not http_client else [
        ts("Outgoing HTTP (client) by target and status",
           [q(f'sum by (server_address, server_port, http_response_status_code, error_type) '
              f'(rate(http_client_request_duration_seconds_count{{{sel}, server_address!="otel-collector"}}[{RI}]))',
              "{{server_address}}:{{server_port}} {{http_response_status_code}}{{error_type}}")],
           unit="reqps", w=12),
    ])


def npgsql_pool(job):
    sel = f'job="{job}"'
    return [
        row(f"{job}: database client (Npgsql)"),
        ts("Connection pool", [q(f'sum by (db_client_connection_state) (db_client_connection_count{{{sel}}})', "{{db_client_connection_state}}"),
                               q(f'max(db_client_connection_max{{{sel}}})', "max")],
           w=8, desc="Connections in use / idle in the service's Npgsql pool, compared to Maximum Pool Size."),
        ts("Commands in flight", [q(f'sum(db_client_operation_npgsql_executing{{{sel}}})', "executing")], w=8),
        ts("Command duration (p50 / p95 / p99)",
           [q(f'histogram_quantile({p}, sum by (le) (rate(db_client_operation_duration_seconds_bucket{{{sel}}}[{RI}])))',
              f"p{int(p * 100)}", exemplar=(p == 0.95)) for p in (0.5, 0.95, 0.99)],
           unit="s", w=8),
        ts("Connection open time (p95)",
           [q(f'histogram_quantile(0.95, sum by (le) (rate(db_client_connection_npgsql_create_time_seconds_bucket{{{sel}}}[{RI}])))', "p95")],
           unit="s", w=8),
        ts("Commands / s", [q(f'sum(rate(db_client_operation_duration_seconds_count{{{sel}}}[{RI}]))', "commands"),
                            q(f'sum(rate(db_client_operation_duration_seconds_count{{{sel}, error_type!=""}}[{RI}]))', "failed")],
           unit="ops", w=8),
        ts("Bytes / s", [q(f'rate(db_client_operation_npgsql_bytes_read_total{{{sel}}}[{RI}])', "read"),
                         q(f'rate(db_client_operation_npgsql_bytes_written_total{{{sel}}}[{RI}])', "written")],
           unit="Bps", w=8),
    ]


def go_runtime(job):
    sel = f'job="{job}"'
    return [
        row(f"{job}: Go runtime"),
        ts("Goroutines", [q(f'go_goroutine_count{{{sel}}}', "goroutines")], w=6),
        ts("Memory", [q(f'sum by (go_memory_type) (go_memory_used_bytes{{{sel}}})', "{{go_memory_type}}"),
                      q(f'go_memory_gc_goal_bytes{{{sel}}}', "GC goal")], unit="bytes", w=6),
        ts("Heap allocation rate", [q(f'rate(go_memory_allocated_bytes_total{{{sel}}}[{RI}])', "allocated"),
                                    q(f'rate(go_memory_allocations_total{{{sel}}}[{RI}])', "allocations / s")], unit="short", w=6),
        ts("Container CPU (cores)", [q(f'sum(rate(container_cpu_usage_seconds_total{{service="{job}"}}[{RI}]))', "used"),
                                     q(f'max(container_spec_cpu_quota{{service="{job}"}}) / max(container_spec_cpu_period{{service="{job}"}})', "limit")],
           w=6),
    ]


def service_logs(job, h=10):
    return [
        row(f"{job}: logs"),
        log_volume("Log lines by level", f'sum by (detected_level) (count_over_time({{service_name="{job}"}}[$__auto]))', w=24, h=6),
        logs("Warnings and errors (click a line → trace_id → Tempo)",
             f'{{service_name="{job}"}} | detected_level=~"warn|error"', h=h),
    ]


# ---------------------------------------------------------------------------------------------- dashboards
def overview():
    finished_ok = 'sum(rate(payments_finished_total{state="Completed"}[5m]))'
    finished_all = 'sum(rate(payments_finished_total[5m]))'
    p = [
        row("Business"),
        stat("Payments created / s", f'sum(rate(payments_created_total[{RI}]))', "reqps", decimals=1),
        stat("Completed (last 5m)", ratio(finished_ok, finished_all), "percentunit", decimals=1,
             steps=thr(("red", None), ("orange", 0.9), ("green", 0.97)),
             desc="Share of payments that reached a final/parked state in the last 5 minutes and ended Completed."),
        stat("In flight", 'sum(payments_by_state{state!="RequiresAttention"})', "short",
             steps=thr(("green", None), ("orange", 200), ("red", 1000))),
        stat("Under review", 'sum(payments_by_state{state="RequiresAttention"})', "short",
             steps=thr(("green", None), ("orange", 1), ("red", 20)),
             desc="Sagas parked in RequiresAttention (need an operator)."),
        stat("Oldest in-flight payment", 'max(saga_oldest_active_age_seconds)', "s",
             steps=thr(("green", None), ("orange", 60), ("red", 300))),
        stat("Outbox backlog", 'sum(outbox_pending)', "short", steps=thr(("green", None), ("orange", 100), ("red", 1000))),

        ts("Payments created and finished / s",
           [q(f'sum by (type) (rate(payments_created_total[{RI}]))', "created {{type}}"),
            q(f'sum by (state) (rate(payments_finished_total[{RI}]))', "finished {{state}}")],
           unit="reqps", w=8),
        ts("Payments in flight by saga state", [q('sum by (state) (payments_by_state)', "{{state}}")], w=8, stack=True, fill=40),
        ts("End-to-end processing time (create → final)",
           [q(f'histogram_quantile({p}, sum by (le) (rate(payment_processing_duration_seconds_bucket[{RI}])))', f"p{int(p * 100)}")
            for p in (0.5, 0.95, 0.99)], unit="s", w=8),

        row("Services (RED)"),
        ts("Requests / s", [q(f'sum by (job) (rate(http_server_request_duration_seconds_count{{job=~"{HTTP_SERVICES}"}}[{RI}]))', "{{job}}")],
           unit="reqps", w=8),
        ts("5xx ratio",
           [q(ratio(f'sum by (job) (rate(http_server_request_duration_seconds_count{{job=~"{HTTP_SERVICES}", http_response_status_code=~"5.."}}[{RI}]))',
                    f'sum by (job) (rate(http_server_request_duration_seconds_count{{job=~"{HTTP_SERVICES}"}}[{RI}]))'), "{{job}}")],
           unit="percentunit", w=8),
        ts("p95 latency", [q(f'histogram_quantile(0.95, sum by (le, job) (rate(http_server_request_duration_seconds_bucket{{job=~"{HTTP_SERVICES}"}}[{RI}])))', "{{job}}", exemplar=True)],
           unit="s", w=8),
        ts("Gateway responses by status", [q(f'sum by (http_response_status_code) (rate(http_server_request_duration_seconds_count{{job="gateway"}}[{RI}]))', "{{http_response_status_code}}")],
           unit="reqps", w=8, stack=True),
        ts("Bank calls by outcome (connector)", [q(f'sum by (operation, outcome) (rate(bank_calls_total[{RI}]))', "{{operation}} {{outcome}}")],
           unit="reqps", w=8),
        ts("Saga step retries / s", [q(f'sum by (step, phase) (rate(saga_step_retries_total[{RI}]))', "{{step}} {{phase}}")],
           unit="short", w=8),

        row("Infrastructure"),
        ts("Queue depth (ready + unacked)", [q('sum by (queue) (rabbitmq_queue_messages)', "{{queue}}")], w=8),
        ts("DB sessions by state", [q('sum by (db, state) (pg_stat_activity_count{datname=~"payments|wallet"})', "{{db}} {{state}}")], w=8),
        ts("Oldest open DB transaction", [q('max by (db) (pg_oldest_transaction_age_seconds)', "{{db}}")], unit="s", w=8),
        ts("Container CPU (% of limit)",
           [q(f'sum by (service) (rate(container_cpu_usage_seconds_total{{service!=""}}[{RI}])) '
              f'/ (max by (service) (container_spec_cpu_quota{{service!=""}}) / max by (service) (container_spec_cpu_period{{service!=""}}))', "{{service}}")],
           unit="percentunit", w=12, legend_calcs=["max", "lastNotNull"]),
        ts("Container memory (% of limit)",
           [q('max by (service) (container_memory_working_set_bytes{service!=""}) / max by (service) (container_spec_memory_limit_bytes{service!=""} > 0)', "{{service}}")],
           unit="percentunit", w=12, legend_calcs=["max", "lastNotNull"]),

        row("Logs and traces"),
        log_volume("Warnings and errors by level (all services)",
                   'sum by (detected_level) (count_over_time({service_name=~".+"} | detected_level=~"warn|error" [$__auto]))', w=8),
        logs("Latest warnings and errors", '{service_name=~".+"} | detected_level=~"warn|error"', w=16, h=8),
        traces("Recent traces with errors", '{ status = error }', limit=20),
    ]
    return dashboard("paymentlab-overview", "PaymentLab · Overview", p,
                     "Start here: business KPIs, service RED metrics, infrastructure saturation, errors.")


def gateway():
    p = [
        row("Traffic"),
        stat("Requests / s", f'sum(rate(http_server_request_duration_seconds_count{{job="gateway"}}[{RI}]))', "reqps", w=6, decimals=1),
        stat("5xx ratio", ratio(f'sum(rate(http_server_request_duration_seconds_count{{job="gateway", http_response_status_code=~"5.."}}[{RI}]))',
                                f'sum(rate(http_server_request_duration_seconds_count{{job="gateway"}}[{RI}]))'), "percentunit", w=6, decimals=2,
             steps=thr(("green", None), ("orange", 0.01), ("red", 0.05))),
        stat("429 / s (rate limited)", f'sum(rate(aspnetcore_rate_limiting_requests_total{{job="gateway", aspnetcore_rate_limiting_result!="acquired"}}[{RI}])) or vector(0)',
             "reqps", w=6, decimals=1, steps=thr(("green", None), ("orange", 0.1))),
        stat("p95 latency", f'histogram_quantile(0.95, sum by (le) (rate(http_server_request_duration_seconds_bucket{{job="gateway"}}[{RI}])))', "s", w=6,
             steps=thr(("green", None), ("orange", 0.5), ("red", 2))),
        *http_red("gateway"),
        row("Rate limiting and connections"),
        ts("Rate limiter decisions", [q(f'sum by (aspnetcore_rate_limiting_result) (rate(aspnetcore_rate_limiting_requests_total{{job="gateway"}}[{RI}]))',
                                        "{{aspnetcore_rate_limiting_result}}")], unit="reqps", w=8, stack=True),
        ts("Active requests / connections", [q('sum(http_server_active_requests{job="gateway"})', "active requests"),
                                             q('sum(kestrel_active_connections{job="gateway"})', "kestrel connections"),
                                             q('sum(kestrel_queued_connections{job="gateway"})', "queued connections")], w=8),
        ts("Upstream calls (YARP → service) p95 by target",
           [q(f'histogram_quantile(0.95, sum by (le, server_address) (rate(http_client_request_duration_seconds_bucket{{job="gateway", server_address!="otel-collector"}}[{RI}])))',
              "{{server_address}}")], unit="s", w=8),
        *dotnet_runtime("gateway"),
        *service_logs("gateway"),
    ]
    return dashboard("paymentlab-gateway", "PaymentLab · Gateway", p, "Edge: routing, rate limiting, latency seen by clients.")


def payments():
    p = [
        row("Payments"),
        stat("Created / s", f'sum(rate(payments_created_total[{RI}]))', "reqps", decimals=1),
        stat("Idempotent replays / s", f'sum(rate(payments_idempotent_replays_total[{RI}]))', "reqps", decimals=2),
        stat("In flight", 'sum(payments_by_state{state!="RequiresAttention"})', steps=thr(("green", None), ("orange", 200), ("red", 1000))),
        stat("Under review", 'sum(payments_by_state{state="RequiresAttention"})', steps=thr(("green", None), ("orange", 1), ("red", 20))),
        stat("Oldest in flight", 'max(saga_oldest_active_age_seconds)', "s", steps=thr(("green", None), ("orange", 60), ("red", 300))),
        stat("E2E p95", f'histogram_quantile(0.95, sum by (le) (rate(payment_processing_duration_seconds_bucket[5m])))', "s",
             steps=thr(("green", None), ("orange", 5), ("red", 30))),
        ts("Created by type / finished by state",
           [q(f'sum by (type) (rate(payments_created_total[{RI}]))', "created {{type}}"),
            q(f'sum by (type, state) (rate(payments_finished_total[{RI}]))', "finished {{type}} {{state}}")],
           unit="reqps", w=8),
        ts("In flight by saga state", [q('sum by (state) (payments_by_state)', "{{state}}")], w=8, stack=True, fill=40),
        ts("End-to-end processing time by type",
           [q(f'histogram_quantile({p}, sum by (le, type) (rate(payment_processing_duration_seconds_bucket[{RI}])))', f"p{int(p * 100)} {{{{type}}}}")
            for p in (0.5, 0.95)], unit="s", w=8),
        row("API"),
        *http_red("payments"),
        ts("Idempotent replays", [q(f'sum by (result) (rate(payments_idempotent_replays_total[{RI}]))', "{{result}}")], unit="reqps", w=12),
        ts("Oldest in-flight payment age", [q('max(saga_oldest_active_age_seconds)', "age")], unit="s", w=12),

        row("Saga orchestrator"),
        ts("Step duration p95 by step / phase",
           [q(f'histogram_quantile(0.95, sum by (le, step, phase) (rate(saga_step_duration_seconds_bucket[{RI}])))', "{{step}} {{phase}}", exemplar=True)],
           unit="s", w=8),
        ts("Step outcomes / s", [q(f'sum by (step, outcome) (rate(saga_step_duration_seconds_count[{RI}]))', "{{step}} → {{outcome}}")],
           unit="ops", w=8),
        ts("Step retries / s", [q(f'sum by (step, phase) (rate(saga_step_retries_total[{RI}]))', "{{step}} {{phase}}")], w=8),
        ts("Transitions / s (to state)", [q(f'sum by (to) (rate(saga_transitions_total[{RI}]))', "{{to}}")], unit="ops", w=8),
        ts("Saga runs by trigger / result", [q(f'sum by (trigger, result) (rate(saga_runs_total[{RI}]))', "{{trigger}} {{result}}")],
           unit="ops", w=8),
        ts("Leased sagas", [q('sum(saga_leased)', "leased")], w=8,
           desc="Sagas currently held by a worker (lease columns set)."),
        ts("Claim query duration (p50 / p95)",
           [q(f'histogram_quantile({p}, sum by (le) (rate(saga_claim_duration_seconds_bucket[{RI}])))', f"p{int(p * 100)}") for p in (0.5, 0.95)],
           unit="s", w=8),
        ts("Sagas claimed per poll (avg)", [q(f'sum(rate(saga_claim_batch_size_sum[{RI}])) / sum(rate(saga_claim_batch_size_count[{RI}]))', "avg batch")],
           w=8),
        ts("Polls / s", [q(f'sum(rate(saga_claim_batch_size_count[{RI}]))', "polls")], unit="ops", w=8),

        row("Messaging (outbox / inbox)"),
        ts("Outbox backlog", [q('sum(outbox_pending)', "pending rows"), q('sum(inbox_pending)', "inbox pending")], w=8),
        ts("Outbox: oldest pending row and publish lag",
           [q('max(outbox_oldest_pending_age_seconds)', "oldest pending"),
            q(f'histogram_quantile(0.95, sum by (le) (rate(outbox_publish_lag_seconds_bucket[{RI}])))', "publish lag p95")],
           unit="s", w=8),
        ts("Published / relay errors", [q(f'sum by (type) (rate(outbox_published_total[{RI}]))', "published {{type}}"),
                                        q(f'sum(rate(outbox_publish_errors_total[{RI}]))', "relay errors")], unit="ops", w=8),
        ts("Bank results consumed", [q(f'sum by (outcome, result) (rate(bank_results_received_total[{RI}]))', "{{outcome}} {{result}}")],
           unit="ops", w=12),
        ts("Calls to Wallet (HTTP) by status",
           [q(f'sum by (http_response_status_code, error_type) (rate(http_client_request_duration_seconds_count{{job="payments", server_port="18082"}}[{RI}]))',
              "{{http_response_status_code}}{{error_type}}")], unit="reqps", w=12),
        *npgsql_pool("payments"),
        *dotnet_runtime("payments"),
        row("Traces"),
        traces("Recent payments-service traces with errors", '{ resource.service.name = "payments" && status = error }', w=12),
        traces("Slowest saga runs (last hour)", '{ resource.service.name = "payments" && name =~ "saga .*" } | duration > 1s', w=12),
        *service_logs("payments"),
    ]
    return dashboard("paymentlab-payments", "PaymentLab · Payments", p,
                     "Payment API, saga orchestrator, outbox/inbox, DB pool and runtime of the Payments service.")


def wallet():
    p = [
        row("Wallet operations"),
        ts("Operations / s by result", [q(f'sum by (operation, result) (rate(wallet_operations_total[{RI}]))', "{{operation}} {{result}}")],
           unit="ops", w=12),
        ts("Reserve / commit / release latency p95",
           [q(f'histogram_quantile(0.95, sum by (le, http_route) (rate(http_server_request_duration_seconds_bucket{{job="wallet", http_route=~"/reservations.*"}}[{RI}])))',
              "{{http_route}}", exemplar=True)], unit="s", w=12),
        row("API"),
        *http_red("wallet"),
        *npgsql_pool("wallet"),
        *dotnet_runtime("wallet", http_client=False),
        *service_logs("wallet"),
    ]
    return dashboard("paymentlab-wallet", "PaymentLab · Wallet", p, "Accounts, reservations, ledger: API, DB pool and runtime.")


def connector():
    p = [
        row("Consuming"),
        stat("Deliveries / s", f'sum(rate(connector_deliveries_total[{RI}]))', "ops", w=6, decimals=1),
        stat("In flight", 'sum(connector_messages_inflight)', w=6),
        stat("Bank p95", f'histogram_quantile(0.95, sum by (le) (rate(bank_call_duration_seconds_bucket[{RI}])))', "s", w=6,
             steps=thr(("green", None), ("orange", 1), ("red", 3))),
        stat("AMQP reconnects (1h)", 'sum(increase(connector_amqp_reconnects_total[1h]))', w=6,
             steps=thr(("green", None), ("orange", 1), ("red", 5)), graph=False),
        ts("Deliveries / s by queue", [q(f'sum by (queue) (rate(connector_deliveries_total[{RI}]))', "{{queue}}")], unit="ops", w=8),
        ts("Settled by result", [q(f'sum by (queue, result) (rate(connector_messages_handled_total[{RI}]))', "{{queue}} {{result}}")],
           unit="ops", w=8, desc="published = result sent and command acked; retry_scheduled = sent to the delay queue; "
                                 "dead_lettered / malformed = nacked to the DLQ; requeued = nacked back to the queue."),
        ts("In flight by queue", [q('sum by (queue) (connector_messages_inflight)', "{{queue}}"),
                                  q('sum by (queue) (rabbitmq_queue_messages_unacked{queue=~"connector.*"})', "unacked (broker) {{queue}}")], w=8),
        ts("Processing time per delivery (p50 / p95 / p99)",
           [q(f'histogram_quantile({p}, sum by (le, queue) (rate(connector_message_processing_duration_seconds_bucket[{RI}])))',
              f"p{int(p * 100)} {{{{queue}}}}") for p in (0.5, 0.95, 0.99)], unit="s", w=12),
        ts("Connector queues (ready)", [q('sum by (queue) (rabbitmq_queue_messages_ready{queue=~"connector.*"})', "{{queue}}")], w=12),
        row("Bank calls"),
        ts("Bank calls by outcome", [q(f'sum by (operation, outcome) (rate(bank_calls_total[{RI}]))', "{{operation}} {{outcome}}")],
           unit="reqps", w=8),
        ts("Bank call latency (p50 / p95 / p99)",
           [q(f'histogram_quantile({p}, sum by (le, operation) (rate(bank_call_duration_seconds_bucket[{RI}])))',
              f"p{int(p * 100)} {{{{operation}}}}", exemplar=(p == 0.95)) for p in (0.5, 0.95, 0.99)], unit="s", w=8),
        ts("HTTP client to bank by status",
           [q(f'sum by (http_response_status_code, error_type) (rate(http_client_request_duration_seconds_count{{job="connector"}}[{RI}]))',
              "{{http_response_status_code}}{{error_type}}")], unit="reqps", w=8),
        row("Publishing"),
        ts("Publishes by exchange / result", [q(f'sum by (exchange, result) (rate(connector_publishes_total[{RI}]))', "{{exchange}} {{result}}")],
           unit="ops", w=12),
        ts("Publish + confirm latency (p95 / p99)",
           [q(f'histogram_quantile({p}, sum by (le) (rate(connector_publish_duration_seconds_bucket[{RI}])))', f"p{int(p * 100)}")
            for p in (0.95, 0.99)], unit="s", w=12),
        *go_runtime("connector"),
        *service_logs("connector"),
    ]
    return dashboard("paymentlab-connector", "PaymentLab · Connector", p, "RabbitMQ consumer that calls the bank and publishes results.")


def fakebank():
    p = [
        row("Bank"),
        ts("Transfers by result", [q(f'sum by (result) (rate(fakebank_transfers_total[{RI}]))', "{{result}}")], unit="ops", w=8, stack=True),
        ts("Transfers kept in memory", [q('sum(fakebank_transfers_stored)', "stored")], w=8),
        ts("Responses by status", [q(f'sum by (http_route, http_response_status_code) (rate(http_server_request_duration_seconds_count{{job="fakebank"}}[{RI}]))',
                                     "{{http_route}} {{http_response_status_code}}")], unit="reqps", w=8),
        *http_red("fakebank"),
        *go_runtime("fakebank"),
        *service_logs("fakebank"),
    ]
    return dashboard("paymentlab-fakebank", "PaymentLab · Fake Bank", p, "The simulated external bank (server side).")


def rabbitmq():
    qs = 'queue=~"$queue"'
    p = [
        row("Overview"),
        stat("Messages (all queues)", 'sum(rabbitmq_queue_messages)', steps=thr(("green", None), ("orange", 1000), ("red", 10000))),
        stat("Unacked", 'sum(rabbitmq_queue_messages_unacked)'),
        stat("Dead-lettered (DLQ depth)", 'sum(rabbitmq_queue_messages{queue=~".*\\\\.dlq"})', steps=thr(("green", None), ("red", 1))),
        stat("Connections", 'sum(rabbitmq_connections)', graph=False),
        stat("Consumers", 'sum(rabbitmq_consumers)', graph=False),
        stat("Memory used / watermark", 'sum(rabbitmq_process_resident_memory_bytes) / sum(rabbitmq_resident_memory_limit_bytes)', "percentunit",
             decimals=1, steps=thr(("green", None), ("orange", 0.7), ("red", 0.9))),
        row("Queues"),
        ts("Ready messages", [q(f'sum by (queue) (rabbitmq_queue_messages_ready{{{qs}}})', "{{queue}}")], w=8),
        ts("Unacked messages", [q(f'sum by (queue) (rabbitmq_queue_messages_unacked{{{qs}}})', "{{queue}}")], w=8),
        ts("Consumers per queue", [q(f'sum by (queue) (rabbitmq_queue_consumers{{{qs}}})', "{{queue}}")], w=8),
        ts("Published into queue / s", [q(f'sum by (queue) (rate(rabbitmq_queue_messages_published_total{{{qs}}}[{RI}]))', "{{queue}}")], unit="ops", w=8),
        ts("Delivered / acked / s", [q(f'sum by (queue) (rate(rabbitmq_queue_messages_delivered_ack_total{{{qs}}}[{RI}]))', "delivered {{queue}}"),
                                     q(f'sum by (queue) (rate(rabbitmq_queue_messages_acked_total{{{qs}}}[{RI}]))', "acked {{queue}}")], unit="ops", w=8),
        ts("Redelivered / s", [q(f'sum by (queue) (rate(rabbitmq_queue_messages_redelivered_total{{{qs}}}[{RI}]))', "{{queue}}")], unit="ops", w=8),
        ts("Consumer utilisation", [q(f'max by (queue) (rabbitmq_queue_consumer_utilisation{{{qs}}})', "{{queue}}")], unit="percentunit", w=12, max=1,
           desc="Fraction of time the queue could deliver to consumers immediately (1 = consumers keep up; low = limited by prefetch / slow consumers)."),
        ts("Queue memory", [q(f'sum by (queue) (rabbitmq_queue_process_memory_bytes{{{qs}}})', "{{queue}}")], unit="bytes", w=12),
        row("Broker"),
        ts("Global message flow / s", [q(f'sum(rate(rabbitmq_global_messages_received_total[{RI}]))', "received"),
                                       q(f'sum(rate(rabbitmq_global_messages_delivered_total[{RI}]))', "delivered"),
                                       q(f'sum(rate(rabbitmq_global_messages_acknowledged_total[{RI}]))', "acked"),
                                       q(f'sum(rate(rabbitmq_global_messages_confirmed_total[{RI}]))', "confirmed (publisher)"),
                                       q(f'sum(rate(rabbitmq_global_messages_redelivered_total[{RI}]))', "redelivered")], unit="ops", w=8),
        ts("Dead-lettered / unroutable / s",
           [*[q(f'sum(rate(rabbitmq_global_messages_dead_lettered_{r}_total[{RI}]))', f"dead-lettered ({r})")
              for r in ("rejected", "expired", "maxlen", "delivery_limit")],
            q(f'sum(rate(rabbitmq_global_messages_unroutable_returned_total[{RI}]))', "unroutable returned"),
            q(f'sum(rate(rabbitmq_global_messages_unroutable_dropped_total[{RI}]))', "unroutable dropped")], unit="ops", w=8),
        ts("Connections / channels", [q('sum(rabbitmq_connections)', "connections"), q('sum(rabbitmq_channels)', "channels"),
                                      q(f'sum(rate(rabbitmq_connections_opened_total[{RI}]))', "opened / s"),
                                      q(f'sum(rate(rabbitmq_connections_closed_total[{RI}]))', "closed / s")], w=8),
        ts("Memory", [q('sum(rabbitmq_process_resident_memory_bytes)', "resident"),
                      q('sum(rabbitmq_resident_memory_limit_bytes)', "high watermark")], unit="bytes", w=8),
        ts("Alarms", [q('rabbitmq_alarms_memory_used_watermark', "memory alarm"),
                      q('rabbitmq_alarms_free_disk_space_watermark', "disk alarm")], w=8, max=1),
        ts("Erlang processes / run queue", [q('rabbitmq_erlang_processes_used', "processes"),
                                            q('rabbitmq_erlang_scheduler_run_queue', "run queue")], w=8),
    ]
    variables = [var_query("queue", 'label_values(rabbitmq_queue_messages, queue)')]
    return dashboard("paymentlab-rabbitmq", "PaymentLab · RabbitMQ", p, "Broker and per-queue metrics.", variables)


def postgres():
    d = 'db="$db"'
    top_time = (f'topk(15, sum by (queryid) (rate(pg_stat_statements_seconds_total{{{d}}}[5m]))) '
                f'* on (queryid) group_left(query) max by (queryid, query) (pg_stat_statements_query_id{{{d}}})')
    top_calls = (f'topk(15, sum by (queryid) (rate(pg_stat_statements_calls_total{{{d}}}[5m]))) '
                 f'* on (queryid) group_left(query) max by (queryid, query) (pg_stat_statements_query_id{{{d}}})')
    top_mean = (f'topk(15, (sum by (queryid) (rate(pg_stat_statements_seconds_total{{{d}}}[5m])) '
                f'/ (sum by (queryid) (rate(pg_stat_statements_calls_total{{{d}}}[5m])) > 0))) '
                f'* on (queryid) group_left(query) max by (queryid, query) (pg_stat_statements_query_id{{{d}}})')
    hide = {"id": "organize", "options": {"excludeByName": {"Time": True, "queryid": True}, "indexByName": {"Value": 0, "query": 1},
                                          "renameByName": {}}}
    p = [
        row("Connections and sessions"),
        stat("Up", f'pg_up{{{d}}}', graph=False, steps=thr(("red", None), ("green", 1))),
        stat("Connections / max", f'sum(pg_stat_activity_count{{{d}}}) / max(pg_settings_max_connections{{{d}}})', "percentunit", decimals=1,
             steps=thr(("green", None), ("orange", 0.7), ("red", 0.9))),
        stat("Blocked sessions", f'sum(pg_blocked_sessions{{{d}}})', steps=thr(("green", None), ("orange", 1), ("red", 5))),
        stat("Oldest transaction", f'max(pg_oldest_transaction_age_seconds{{{d}}})', "s", steps=thr(("green", None), ("orange", 10), ("red", 60))),
        stat("Deadlocks (1h)", f'sum(increase(pg_stat_database_deadlocks{{{d}, datname="$db"}}[1h]))', graph=False,
             steps=thr(("green", None), ("red", 1))),
        stat("Cache hit ratio", f'sum(rate(pg_stat_database_blks_hit{{{d}, datname="$db"}}[5m])) / '
                                f'(sum(rate(pg_stat_database_blks_hit{{{d}, datname="$db"}}[5m])) + sum(rate(pg_stat_database_blks_read{{{d}, datname="$db"}}[5m])))',
             "percentunit", decimals=2, steps=thr(("red", None), ("orange", 0.9), ("green", 0.99))),
        ts("Sessions by application and state", [q(f'sum by (application_name, state) (pg_sessions_count{{{d}}})', "{{application_name}} {{state}}")],
           w=8, stack=True, fill=30),
        ts("Sessions waiting (by wait type)", [q(f'sum by (wait_event_type) (pg_sessions_count{{{d}, state="active", wait_event_type!="none"}})', "{{wait_event_type}}"),
                                               q(f'sum(pg_blocked_sessions{{{d}}})', "blocked by another session")], w=8),
        ts("Oldest open transaction by session state", [q(f'max by (state) (pg_oldest_transaction_age_seconds{{{d}}})', "{{state}}")], unit="s", w=8),
        row("Locks and transactions"),
        ts("Locks by mode", [q(f'sum by (mode) (pg_locks_count{{{d}, datname="$db"}})', "{{mode}}")], w=8),
        ts("Commits / rollbacks / s", [q(f'sum(rate(pg_stat_database_xact_commit{{{d}, datname="$db"}}[{RI}]))', "commit"),
                                       q(f'sum(rate(pg_stat_database_xact_rollback{{{d}, datname="$db"}}[{RI}]))', "rollback")], unit="ops", w=8),
        ts("Deadlocks / conflicts", [q(f'sum(increase(pg_stat_database_deadlocks{{{d}, datname="$db"}}[{RI}]))', "deadlocks"),
                                     q(f'sum(pg_long_running_transactions{{{d}}})', "long-running transactions")], w=8),
        ts("Rows / s", [q(f'sum(rate(pg_stat_database_tup_fetched{{{d}, datname="$db"}}[{RI}]))', "fetched"),
                        q(f'sum(rate(pg_stat_database_tup_returned{{{d}, datname="$db"}}[{RI}]))', "returned (scanned)"),
                        q(f'sum(rate(pg_stat_database_tup_inserted{{{d}, datname="$db"}}[{RI}]))', "inserted"),
                        q(f'sum(rate(pg_stat_database_tup_updated{{{d}, datname="$db"}}[{RI}]))', "updated"),
                        q(f'sum(rate(pg_stat_database_tup_deleted{{{d}, datname="$db"}}[{RI}]))', "deleted")], unit="short", w=8),
        ts("Temp files written", [q(f'sum(rate(pg_stat_database_temp_bytes{{{d}, datname="$db"}}[{RI}]))', "temp bytes / s")], unit="Bps", w=8),
        ts("Block reads (disk) vs hits", [q(f'sum(rate(pg_stat_database_blks_read{{{d}, datname="$db"}}[{RI}]))', "read"),
                                          q(f'sum(rate(pg_stat_database_blks_hit{{{d}, datname="$db"}}[{RI}]))', "hit")], unit="short", w=8),
        row("Tables"),
        ts("Sequential scans / s by table", [q(f'sum by (relname) (rate(pg_stat_user_tables_seq_scan{{{d}}}[{RI}]))', "{{relname}}")], w=8),
        ts("Rows read by seq scans / s", [q(f'sum by (relname) (rate(pg_stat_user_tables_seq_tup_read{{{d}}}[{RI}]))', "{{relname}}")], w=8),
        ts("Index scans / s by table", [q(f'sum by (relname) (rate(pg_stat_user_tables_idx_scan{{{d}}}[{RI}]))', "{{relname}}")], w=8),
        ts("Table size (incl. indexes)", [q(f'sum by (relname) (pg_table_total_bytes{{{d}}})', "{{relname}}")], unit="bytes", w=8),
        ts("Live rows", [q(f'sum by (relname) (pg_table_live_rows{{{d}}})', "{{relname}}")], w=8),
        ts("Dead rows", [q(f'sum by (relname) (pg_table_dead_rows{{{d}}})', "{{relname}}")], w=8),
        row("Statements (pg_stat_statements)"),
        table("Top statements by total execution time (seconds per second)", [q(top_time, instant=True, fmt="table")],
              w=24, h=9, transformations=[hide], unit="s"),
        table("Top statements by mean time per call", [q(top_mean, instant=True, fmt="table")], w=12, h=9, transformations=[hide], unit="s"),
        table("Top statements by calls / s", [q(top_calls, instant=True, fmt="table")], w=12, h=9, transformations=[hide], unit="ops"),
        row("Server logs"),
        text("Postgres server logs (slow statements > 500 ms, lock waits) are not shipped to Loki; read them with "
             "`./lab.ps1 logs payments-db` or `./lab.ps1 logs wallet-db`.", h=2),
    ]
    variables = [var_custom("db", ["payments", "wallet"], "Database")]
    return dashboard("paymentlab-postgres", "PaymentLab · Postgres", p, "Database health, sessions, locks, tables and statements.", variables)


def containers():
    s = 'service=~"$service"'
    p = [
        row("CPU"),
        ts("CPU usage (cores)", [q(f'sum by (service) (rate(container_cpu_usage_seconds_total{{{s}}}[{RI}]))', "{{service}}")],
           w=12, legend_calcs=["max", "lastNotNull"]),
        ts("CPU usage (% of limit)",
           [q(f'sum by (service) (rate(container_cpu_usage_seconds_total{{{s}}}[{RI}])) '
              f'/ (max by (service) (container_spec_cpu_quota{{{s}}}) / max by (service) (container_spec_cpu_period{{{s}}}))', "{{service}}")],
           unit="percentunit", w=12, legend_calcs=["max", "lastNotNull"]),
        ts("CPU throttling (share of periods throttled)",
           [q(f'sum by (service) (rate(container_cpu_cfs_throttled_periods_total{{{s}}}[{RI}])) '
              f'/ sum by (service) (rate(container_cpu_cfs_periods_total{{{s}}}[{RI}]))', "{{service}}")],
           unit="percentunit", w=12, legend_calcs=["max", "lastNotNull"],
           desc="When a container uses its whole CPU quota it is paused until the next period: latency goes up although CPU 'looks' fine."),
        ts("Throttled time (s/s)", [q(f'sum by (service) (rate(container_cpu_cfs_throttled_seconds_total{{{s}}}[{RI}]))', "{{service}}")],
           unit="s", w=12, legend_calcs=["max", "lastNotNull"]),
        row("Memory"),
        ts("Working set", [q(f'max by (service) (container_memory_working_set_bytes{{{s}}})', "{{service}}")], unit="bytes", w=12,
           legend_calcs=["max", "lastNotNull"]),
        ts("Working set (% of limit)",
           [q(f'max by (service) (container_memory_working_set_bytes{{{s}}}) / max by (service) (container_spec_memory_limit_bytes{{{s}}} > 0)', "{{service}}")],
           unit="percentunit", w=12, legend_calcs=["max", "lastNotNull"]),
        ts("OOM kills / memory limit hits", [q(f'sum by (service) (increase(container_oom_events_total{{{s}}}[{RI}]))', "OOM {{service}}"),
                                             q(f'sum by (service) (increase(container_memory_failcnt{{{s}}}[{RI}]))', "limit hit {{service}}")], w=12),
        ts("Container (re)starts", [q(f'sum by (service) (changes(container_start_time_seconds{{{s}}}[5m]))', "{{service}}")], w=12,
           desc="Number of times the container start time changed in the last 5 minutes (restart, crash, OOM)."),
        row("Network"),
        ts("Network received", [q(f'sum by (service) (rate(container_network_receive_bytes_total{{{s}}}[{RI}]))', "{{service}}")], unit="Bps", w=12),
        ts("Network sent", [q(f'sum by (service) (rate(container_network_transmit_bytes_total{{{s}}}[{RI}]))', "{{service}}")], unit="Bps", w=12),
        row("Limits"),
        table("Configured limits", [
            q(f'max by (service) (container_spec_memory_limit_bytes{{{s}}})', instant=True, fmt="table", ref="A"),
            q(f'max by (service) (container_spec_cpu_quota{{{s}}}) / max by (service) (container_spec_cpu_period{{{s}}})', instant=True, fmt="table", ref="B"),
        ], w=24, h=10, transformations=[
            {"id": "merge", "options": {}},
            {"id": "organize", "options": {"excludeByName": {"Time": True},
                                           "renameByName": {"Value #A": "memory limit", "Value #B": "CPU limit (cores)"}}},
        ], overrides=[{"matcher": {"id": "byName", "options": "memory limit"}, "properties": [{"id": "unit", "value": "bytes"}]}]),
    ]
    variables = [var_query("service", 'label_values(container_memory_working_set_bytes{service!=""}, service)')]
    return dashboard("paymentlab-containers", "PaymentLab · Containers", p, "cAdvisor: CPU, throttling, memory, restarts per container.", variables)


DASHBOARDS = {
    "overview.json": overview,
    "gateway.json": gateway,
    "payments.json": payments,
    "wallet.json": wallet,
    "connector.json": connector,
    "fakebank.json": fakebank,
    "rabbitmq.json": rabbitmq,
    "postgres.json": postgres,
    "containers.json": containers,
}

if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    for name, fn in DASHBOARDS.items():
        write(os.path.join(OUT, name), fn())
        print("wrote", name)
