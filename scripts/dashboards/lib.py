"""Tiny helper library to build Grafana dashboard JSON from Python (see generate.py)."""
import json

PROM = {"type": "prometheus", "uid": "prometheus"}
LOKI = {"type": "loki", "uid": "loki"}
TEMPO = {"type": "tempo", "uid": "tempo"}

RI = "$__rate_interval"


def q(expr, legend="", exemplar=False, instant=False, fmt=None, ref=None):
    t = {"expr": expr, "legendFormat": legend or "__auto", "range": not instant, "instant": instant}
    if exemplar:
        t["exemplar"] = True
    if fmt:
        t["format"] = fmt
    if ref:
        t["refId"] = ref
    return t


def _refs(targets):
    for i, t in enumerate(targets):
        t.setdefault("refId", chr(ord("A") + i))
    return targets


def ts(title, targets, unit="short", w=12, h=8, stack=False, fill=10, desc=None, min=None, max=None,
       legend_calcs=None, overrides=None, ds=PROM, draw="line", legend="list"):
    defaults = {
        "unit": unit,
        "color": {"mode": "palette-classic"},
        "custom": {
            "drawStyle": draw,
            "lineWidth": 1,
            "fillOpacity": fill,
            "showPoints": "never",
            "spanNulls": False,
            "stacking": {"mode": "normal" if stack else "none", "group": "A"},
            "axisSoftMin": 0,
        },
    }
    if min is not None:
        defaults["min"] = min
    if max is not None:
        defaults["max"] = max
    return {
        "type": "timeseries", "title": title, "description": desc or "", "datasource": ds,
        "_w": w, "_h": h,
        "fieldConfig": {"defaults": defaults, "overrides": overrides or []},
        "options": {
            "legend": {"displayMode": "table" if legend_calcs else legend, "placement": "bottom", "showLegend": True,
                       "calcs": legend_calcs or []},
            "tooltip": {"mode": "multi", "sort": "desc"},
        },
        "targets": _refs(targets),
    }


def stat(title, expr, unit="short", w=4, h=4, steps=None, desc=None, decimals=None, graph=True, legend="",
         color_mode="value", text_mode="auto", instant=False):
    defaults = {
        "unit": unit,
        "color": {"mode": "thresholds"},
        "thresholds": {"mode": "absolute", "steps": steps or [{"color": "green", "value": None}]},
    }
    if decimals is not None:
        defaults["decimals"] = decimals
    return {
        "type": "stat", "title": title, "description": desc or "", "datasource": PROM, "_w": w, "_h": h,
        "fieldConfig": {"defaults": defaults, "overrides": []},
        "options": {
            "reduceOptions": {"calcs": ["lastNotNull"], "fields": "", "values": False},
            "colorMode": color_mode, "graphMode": "area" if graph else "none", "textMode": text_mode,
            "justifyMode": "auto", "orientation": "auto",
        },
        "targets": _refs([q(expr, legend, instant=instant)]),
    }


def thr(*pairs):
    """thr(('green', None), ('orange', 1), ('red', 5))"""
    return [{"color": c, "value": v} for c, v in pairs]


def logs(title, expr, w=24, h=10, desc=None):
    return {
        "type": "logs", "title": title, "description": desc or "", "datasource": LOKI, "_w": w, "_h": h,
        "options": {"showTime": True, "wrapLogMessage": True, "enableLogDetails": True, "sortOrder": "Descending",
                    "dedupStrategy": "none", "prettifyLogMessage": False, "showCommonLabels": False,
                    "showLabels": False, "enableInfiniteScrolling": True},
        "targets": _refs([{"expr": expr, "queryType": "range"}]),
    }


def log_volume(title, expr, w=12, h=8, desc=None):
    p = ts(title, [{"expr": expr, "legendFormat": "{{detected_level}}", "queryType": "range"}], unit="short",
           w=w, h=h, stack=True, fill=60, desc=desc, ds=LOKI, draw="bars")
    p["fieldConfig"]["overrides"] = [
        {"matcher": {"id": "byName", "options": n}, "properties": [{"id": "color", "value": {"mode": "fixed", "fixedColor": c}}]}
        for n, c in (("error", "red"), ("warn", "orange"), ("info", "green"), ("debug", "blue"))
    ]
    return p


def table(title, targets, w=24, h=8, desc=None, transformations=None, overrides=None, unit=None, ds=PROM):
    defaults = {"custom": {"align": "auto", "cellOptions": {"type": "auto"}, "inspect": True}}
    if unit:
        defaults["unit"] = unit
    return {
        "type": "table", "title": title, "description": desc or "", "datasource": ds, "_w": w, "_h": h,
        "fieldConfig": {"defaults": defaults, "overrides": overrides or []},
        "options": {"showHeader": True, "cellHeight": "sm", "footer": {"show": False}},
        "targets": _refs(targets),
        "transformations": transformations or [],
    }


def traces(title, traceql, w=24, h=8, limit=20, desc=None):
    return {
        "type": "table", "title": title, "description": desc or "", "datasource": TEMPO, "_w": w, "_h": h,
        "fieldConfig": {"defaults": {"custom": {"align": "auto"}}, "overrides": []},
        "options": {"showHeader": True, "cellHeight": "sm", "footer": {"show": False}},
        "targets": [{"refId": "A", "queryType": "traceql", "query": traceql, "limit": limit, "tableType": "traces",
                     "datasource": TEMPO}],
    }


def text(content, w=24, h=3, title=""):
    return {"type": "text", "title": title, "_w": w, "_h": h, "options": {"mode": "markdown", "content": content}}


def row(title, collapsed=False):
    return {"type": "row", "title": title, "collapsed": collapsed, "_w": 24, "_h": 1, "panels": []}


def layout(panels):
    """Assign gridPos left-to-right, wrapping at 24 columns; rows start on a new line."""
    out, x, y, line_h, pid = [], 0, 0, 0, 1
    for p in panels:
        w, h = p.pop("_w"), p.pop("_h")
        if p["type"] == "row" or x + w > 24:
            y += line_h
            x, line_h = 0, 0
        p["gridPos"] = {"x": x, "y": y, "w": w, "h": h}
        p["id"] = pid
        pid += 1
        out.append(p)
        if p["type"] == "row":
            y += 1
            continue
        x += w
        line_h = max(line_h, h)
    return out


def var_query(name, query, label=None, multi=True, include_all=True, ds=PROM, regex=""):
    return {
        "name": name, "label": label or name, "type": "query", "datasource": ds,
        "query": {"query": query, "refId": "var"}, "definition": query, "refresh": 2,
        "multi": multi, "includeAll": include_all, "allValue": ".+" if include_all else None,
        "current": {"selected": True, "text": ["All"], "value": ["$__all"]} if include_all else {},
        "sort": 1, "regex": regex, "hide": 0,
    }


def var_custom(name, values, label=None):
    return {
        "name": name, "label": label or name, "type": "custom", "query": ",".join(values),
        "current": {"selected": True, "text": values[0], "value": values[0]},
        "options": [{"selected": i == 0, "text": v, "value": v} for i, v in enumerate(values)],
        "multi": False, "includeAll": False, "hide": 0,
    }


def dashboard(uid, title, panels, description="", variables=None, refresh="10s", time_from="now-30m"):
    return {
        "uid": uid,
        "title": title,
        "description": description,
        "tags": ["paymentlab"],
        "timezone": "browser",
        "editable": True,
        "graphTooltip": 1,
        "refresh": refresh,
        "schemaVersion": 41,
        "time": {"from": time_from, "to": "now"},
        "templating": {"list": variables or []},
        "annotations": {"list": [{
            "builtIn": 1, "datasource": {"type": "grafana", "uid": "-- Grafana --"}, "enable": True, "hide": True,
            "iconColor": "rgba(0, 211, 255, 1)", "name": "Annotations & Alerts", "type": "dashboard",
        }]},
        "links": [{"title": "PaymentLab", "type": "dashboards", "tags": ["paymentlab"], "asDropdown": True,
                   "includeVars": False, "keepTime": True}],
        "panels": layout(panels),
    }


def write(path, d):
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(d, f, indent=2)
        f.write("\n")
