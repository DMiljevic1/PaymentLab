"""Runs every query of the provisioned dashboards against Prometheus / Loki and reports errors and empty results.

    python scripts/dashboards/check.py            # needs the stack running (ports 9090, 3100)
"""
import glob
import json
import os
import sys
import urllib.parse
import urllib.request

PROM = "http://localhost:9090/api/v1/query"
LOKI = "http://localhost:3100/loki/api/v1/query_range"
VARS = {"$__rate_interval": "1m", "$__auto": "1m", "$__range": "1h", "$queue": ".+", "$service": ".+", "$db": "payments"}


def run(url, expr):
    for k, v in VARS.items():
        expr = expr.replace(k, v)
    full = url + "?" + urllib.parse.urlencode({"query": expr})
    try:
        with urllib.request.urlopen(full, timeout=20) as r:
            data = json.load(r)
    except urllib.error.HTTPError as e:
        return "error", e.read().decode()[:300]
    except Exception as e:  # noqa: BLE001
        return "error", str(e)
    if data.get("status") != "success":
        return "error", str(data)[:300]
    return ("ok" if data["data"]["result"] else "empty"), ""


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    root = os.path.join(os.path.dirname(__file__), "..", "..", "infra", "grafana", "dashboards")
    errors = empties = total = 0
    for path in sorted(glob.glob(os.path.join(root, "*.json"))):
        d = json.load(open(path, encoding="utf-8"))
        for p in d["panels"]:
            for t in p.get("targets", []):
                uid = (t.get("datasource") or p.get("datasource") or {}).get("uid")
                if uid not in ("prometheus", "loki"):
                    continue
                total += 1
                status, msg = run(PROM if uid == "prometheus" else LOKI, t["expr"])
                if status == "ok":
                    continue
                if status == "error":
                    errors += 1
                else:
                    empties += 1
                print(f"[{status:5}] {os.path.basename(path)} :: {p['title']} :: {t.get('legendFormat', '')} {msg}")
    print(f"{total} queries, {errors} errors, {empties} empty (empty can be fine: no retries / no errors yet)")
    sys.exit(1 if errors else 0)


if __name__ == "__main__":
    main()
